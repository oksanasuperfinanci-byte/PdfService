using PdfService.Application.Interfaces;
using PdfService.Application.Models;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PdfService.Infrastructure.Storage;

/// <summary>
/// In-memory реализация хранилища задач
///
/// Тспользует две структуры данных:
///  - ConcurrencyDictionary для хранения всех задач (O(1) доступ по ID)
///  - Channel для очереди ождающих задач (thread-safe FIFO очередь)
///
/// Channel выбран вместо ConcurrencyQueue потому, что
///  - Поддерживает async/await из коробки
///  - Позволяет ожидать появления новых элементов (WaitToReadAsync)
///  - Bounded channel защищает от переполнение памяти
///
/// ОГРАНИЧЕНИЯ:
///  - Данные теряются при перезапуске приложения
///  - Не работает с несколькими инстансами (нет shared state)
///  - Нет персистентности для аудита
/// </summary>
public class InMemoryTaskStore : ITaskStore
{
    private readonly ConcurrentDictionary<Guid, PdfTask> _tasks = new();
    private readonly Channel<Guid> _pendingQueue;

    public InMemoryTaskStore()
    {
        _pendingQueue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false, // Несколько worker'ов могут читать
            SingleWriter = false, // Несколько запросов могут писать
        });
    }

    #region ITaskStore
    public async Task<PdfTask> AddTask(PdfTaskRequest request, CancellationToken cancellationToken = default)
    {
        var task = new PdfTask
        {
            Operation = request.Operation,
            InputFilePaths = request.InputFilePaths,
            Options = request.Options,
            Status = Application.Models.TaskStatus.Pending
        };

        // Добавляем в хранилище
        if (false == _tasks.TryAdd(task.Id, task))
        {
            // В теории невозможно при использовании Guid.NewGuid()
            throw new InvalidOperationException($"Task with ID {task.Id} already exists");
        }

        // Добавляем в очередь на обработку
        await _pendingQueue.Writer.WriteAsync(task.Id, cancellationToken);

        return task;
    }

    public Task DeleteAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (_tasks.TryRemove(taskId, out var removed)
        {
            // TODO: можно в лог записать
        }
        return Task.CompletedTask;
    }

    public async Task<PdfTask?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        // Ждём появления задачи в очереди
        // Это блокирующая операция, но async - но занимает поток 
        while (await _pendingQueue.Reader.WaitToReadAsync(cancellationToken))
        {
            // Пытаемся прочитать ID задачи
            if(_pendingQueue.Reader.TryRead(out var taskId))
            {
                // Пролучаем саму задачу
                if (_tasks.TryGetValue(taskId, out var task))
                {
                    // Проверяем, что задача еще в статусе Pendind
                    // (могла быть отменена или уже взята другим worker'ом)
                    if (task.Status == Application.Models.TaskStatus.Pending)
                    {
                        return task;
                    }
                }
            }    
        }

        return null;
    }

    public Task<PdfTask?> GetAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        _tasks.TryGetValue(taskId, out var task);
        return Task.FromResult(task);
    }

    public Task<IReadOnlyList<PdfTask>> GetByStatusAsync(Application.Models.TaskStatus status, CancellationToken cancellationToken = default)
    {
        var tasks = _tasks.Values.
            Where(t => t.Status == status).
            OrderBy(t => t.CreateAt).
            ToList();

        return Task.FromResult<IReadOnlyList<PdfTask>>(tasks);
    }

    public Task<IReadOnlyList<PdfTask>> GetOlderThenAsync(DateTime threshold, CancellationToken cancellationToken = default)
    {
        var oldTasks = _tasks.Values.Where(t => t.CreateAt < threshold).ToList();

        return Task.FromResult<IReadOnlyList<PdfTask>>(oldTasks);
    }

    public Task UpdateAsync(PdfTask task, CancellationToken cancellationToken = default)
    {
        task.UpdateAt = DateTime.UtcNow;

        // ConcurrencyDictionary.AddOrUpdate гарантирует атомарность
        _tasks.AddOrUpdate(task.Id, task, (_, _) => task);

        return Task.CompletedTask;
    }
    #endregion
}
