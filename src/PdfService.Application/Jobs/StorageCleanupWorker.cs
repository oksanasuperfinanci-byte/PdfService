using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PdfService.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace PdfService.Application.Jobs;

/// <summary>
/// Фоновый сервис для очистки устаревших файлов из temp-storage.
/// Запускается каждые 30 минут, удаляет файлы старше FileLifetimeHours.
/// Также помечает связанные задачи как Expired.
/// </summary>
public class StorageCleanupWorker : BackgroundService
{
    private readonly IFileStorage _storage;
    private readonly ITaskStore _taskStore;
    private readonly FileStorageOptions _options;
    private readonly ILogger<StorageCleanupWorker> _logger;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(30);

    public StorageCleanupWorker(
        IFileStorage storage,
        ITaskStore taskStore,
        IOptions<FileStorageOptions> options,
        ILogger<StorageCleanupWorker> logger)
    {
        _storage = storage;
        _taskStore = taskStore;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ждём немного при старте, чтобы приложение полностью инициализировалось
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during storage cleanup");
            }

            await Task.Delay(CleanupInterval, stoppingToken);
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTime.UtcNow.AddHours(-_options.FileLifetimeHours);
        var deletedFiles = 0;

        // 1. Удаляем старые файлы из хранилища
        var filesToCheck = new List<string>();
        await foreach (var filePath in _storage.GetAllFilesAsync(cancellationToken))
        {
            filesToCheck.Add(filePath);
        }

        foreach (var filePath in filesToCheck)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fileInfo = await _storage.GetFileInfoAsync(filePath);
                if (fileInfo != null && fileInfo.CreationTimeUtc < threshold)
                {
                    await _storage.DeleteAsync(filePath);
                    deletedFiles++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete expired file: {FilePath}", filePath);
            }
        }

        // 2. Помечаем старые задачи как Expired
        var expiredTasks = await _taskStore.GetOlderThenAsync(threshold, cancellationToken);
        var expiredCount = 0;

        foreach (var task in expiredTasks)
        {
            if (task.Status == Models.TaskStatus.Completed || task.Status == Models.TaskStatus.Failed)
            {
                task.Status = Models.TaskStatus.Expired;
                await _taskStore.UpdateAsync(task, cancellationToken);
                expiredCount++;
            }
        }

        if (deletedFiles > 0 || expiredCount > 0)
        {
            _logger.LogInformation(
                "Storage cleanup completed: {DeletedFiles} files deleted, {ExpiredTasks} tasks marked as expired",
                deletedFiles, expiredCount);
        }
    }
}
