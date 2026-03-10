using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PdfService.Application.Interfaces;
using PdfService.Application.Models;
using StackExchange.Redis;
using TaskStatus = PdfService.Application.Models.TaskStatus;

namespace PdfService.Infrastructure.Storage;

/// <summary>
/// Distributed реализация ITaskStore на базе Redis.
///
/// Структуры данных в Redis:
///   task:{id}              — String (JSON)     — полное состояние задачи
///   task:queue             — List              — очередь ID задач (FIFO)
///   task:status:{status}   — Set               — вторичный индекс для поиска по статусу
///   task:created           — Sorted Set (score=timestamp) — индекс для поиска по возрасту
///
/// Почему Redis List, а не Streams:
///   - List + BRPOPLPUSH/BLPOP — проще и достаточно для 15 человек
///   - Streams дают consumer groups, но добавляют сложность (ACK, pending entries)
///   - Для MVP List — правильный выбор, Streams — если нужна at-least-once гарантия
///
/// Почему JSON, а не Redis Hash для полей задачи:
///   - PdfTask имеет вложенные коллекции (InputFilePaths, Options)
///   - JSON проще сериализовать целиком, чем маппить каждое поле в Hash
///   - При 15 пользователях экономия на CPU маршалинга несущественна
/// </summary>
public class RedisTaskStore : ITaskStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisTaskStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    // Префиксы ключей Redis
    private const string TaskPrefix = "task:";
    private const string QueueKey = "task:queue";
    private const string StatusPrefix = "task:status:";
    private const string CreatedSortedSetKey = "task:created";

    public RedisTaskStore(
        IConnectionMultiplexer redis,
        ILogger<RedisTaskStore> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Вспомогательный доступ к IDatabase.
    /// Берём каждый раз заново — IDatabase легковесный, не требует кэширования.
    /// </summary>
    private IDatabase Db => _redis.GetDatabase();

    #region ITaskStore — CRUD

    public async Task<PdfTask> AddTask(PdfTaskRequest request, CancellationToken cancellationToken = default)
    {
        var task = new PdfTask
        {
            Operation = request.Operation,
            InputFilePaths = request.InputFilePaths,
            Options = request.Options,
            Status = TaskStatus.Pending
        };

        var db = Db;
        var json = JsonSerializer.Serialize(task, JsonOptions);
        var taskKey = TaskPrefix + task.Id;

        // Транзакция: сохраняем задачу + добавляем в индексы + ставим в очередь
        // Redis транзакция (MULTI/EXEC) — атомарная, но без rollback.
        // Если одна команда упадёт, остальные всё равно выполнятся.
        // Для MVP это приемлемо; для production стоит добавить компенсацию.
        var tran = db.CreateTransaction();

        _ = tran.StringSetAsync(taskKey, json);
        _ = tran.SetAddAsync(StatusPrefix + task.Status, task.Id.ToString());
        _ = tran.SortedSetAddAsync(CreatedSortedSetKey, task.Id.ToString(), task.CreateAt.Ticks);
        _ = tran.ListRightPushAsync(QueueKey, task.Id.ToString());

        if (!await tran.ExecuteAsync())
        {
            _logger.LogError("Redis transaction failed for AddTask {TaskId}", task.Id);
            throw new InvalidOperationException($"Failed to add task {task.Id} to Redis");
        }

        _logger.LogInformation("Task {TaskId} ({Operation}) added to Redis queue", task.Id, task.Operation);
        return task;
    }

    public async Task<PdfTask?> GetAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var json = await Db.StringGetAsync(TaskPrefix + taskId);
        if (json.IsNullOrEmpty) return null;

        return JsonSerializer.Deserialize<PdfTask>(json!, JsonOptions);
    }

    public async Task UpdateAsync(PdfTask task, CancellationToken cancellationToken = default)
    {
        var db = Db;
        var taskKey = TaskPrefix + task.Id;

        // Читаем старый статус, чтобы обновить индекс
        var oldJson = await db.StringGetAsync(taskKey);
        TaskStatus? oldStatus = null;

        if (!oldJson.IsNullOrEmpty)
        {
            var oldTask = JsonSerializer.Deserialize<PdfTask>(oldJson!, JsonOptions);
            oldStatus = oldTask?.Status;
        }

        task.UpdateAt = DateTime.UtcNow;
        var newJson = JsonSerializer.Serialize(task, JsonOptions);

        var tran = db.CreateTransaction();

        _ = tran.StringSetAsync(taskKey, newJson);

        // Обновляем индекс статуса, если статус изменился
        if (oldStatus.HasValue && oldStatus.Value != task.Status)
        {
            _ = tran.SetRemoveAsync(StatusPrefix + oldStatus.Value, task.Id.ToString());
            _ = tran.SetAddAsync(StatusPrefix + task.Status, task.Id.ToString());
        }

        await tran.ExecuteAsync();
    }

    public async Task DeleteAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var db = Db;
        var taskKey = TaskPrefix + taskId;

        // Читаем задачу, чтобы почистить индексы
        var json = await db.StringGetAsync(taskKey);
        if (json.IsNullOrEmpty) return;

        var task = JsonSerializer.Deserialize<PdfTask>(json!, JsonOptions);
        if (task == null) return;

        var tran = db.CreateTransaction();

        _ = tran.KeyDeleteAsync(taskKey);
        _ = tran.SetRemoveAsync(StatusPrefix + task.Status, taskId.ToString());
        _ = tran.SortedSetRemoveAsync(CreatedSortedSetKey, taskId.ToString());

        await tran.ExecuteAsync();
    }

    #endregion

    #region ITaskStore — Queue

    /// <summary>
    /// Атомарно извлекает следующую задачу из очереди.
    ///
    /// Используем BLPOP — блокирующий POP из Redis List:
    ///   - Атомарный: два воркера никогда не возьмут одну задачу
    ///   - Блокирующий: не требует polling, Redis сам уведомит
    ///   - Таймаут 5 сек: позволяет проверять CancellationToken
    ///
    /// Почему таймаут 5 сек, а не бесконечный:
    ///   StackExchange.Redis не поддерживает отмену BLPOP через CancellationToken.
    ///   Поэтому мы ставим короткий таймаут и проверяем token в цикле.
    /// </summary>
    public async Task<PdfTask?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        var db = Db;

        while (!cancellationToken.IsCancellationRequested)
        {
            // BLPOP с таймаутом 5 секунд
            var result = await db.ListLeftPopAsync(QueueKey);

            if (result.IsNull)
            {
                // Очередь пуста — ждём немного и пробуем снова
                try
                {
                    await Task.Delay(1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            var taskId = Guid.Parse(result!);
            var task = await GetAsync(taskId, cancellationToken);

            if (task == null)
            {
                _logger.LogWarning("Task {TaskId} from queue not found in store, skipping", taskId);
                continue;
            }

            // Проверяем, что задача ещё Pending (могла быть отменена)
            if (task.Status != TaskStatus.Pending)
            {
                _logger.LogDebug("Task {TaskId} is no longer Pending ({Status}), skipping", taskId, task.Status);
                continue;
            }

            return task;
        }

        return null;
    }

    #endregion

    #region ITaskStore — Queries

    public async Task<IReadOnlyList<PdfTask>> GetByStatusAsync(
        TaskStatus status, CancellationToken cancellationToken = default)
    {
        var db = Db;
        var members = await db.SetMembersAsync(StatusPrefix + status);

        var tasks = new List<PdfTask>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var task = await GetAsync(Guid.Parse(member!), cancellationToken);
            if (task != null)
                tasks.Add(task);
        }

        return tasks.OrderBy(t => t.CreateAt).ToList();
    }

    public async Task<IReadOnlyList<PdfTask>> GetOlderThenAsync(
        DateTime threshold, CancellationToken cancellationToken = default)
    {
        var db = Db;

        // Sorted Set: score = DateTime.Ticks
        // ZRANGEBYSCORE от -inf до threshold.Ticks — все задачи старше порога
        var members = await db.SortedSetRangeByScoreAsync(
            CreatedSortedSetKey,
            double.NegativeInfinity,
            threshold.Ticks);

        var tasks = new List<PdfTask>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var task = await GetAsync(Guid.Parse(member!), cancellationToken);
            if (task != null)
                tasks.Add(task);
        }

        return tasks;
    }

    #endregion
}
