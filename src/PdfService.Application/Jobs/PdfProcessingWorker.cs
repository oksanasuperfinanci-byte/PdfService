using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfService.Application.Interfaces;
using PdfService.Application.Models;

namespace PdfService.Application.Jobs;

/// <summary>
/// Background Service для обработки PDF задач из очереди
///
/// Архитектура:
/// 1. Worker постоянно ждёт новые задачи из ITaskStore.DequeueAsync()
/// 2. При получении задачи меняет её статус на Processing
/// 3. Вызывает IPdfProcessing.ProcessAsync() для выполнения
/// 4. Обновляет статус на Completed или Failed
///
/// Параллельность:
/// - Регистрируется N инстансов через WorkerCount в настройках
/// - Channel в InMemoryTaskStore гарантирует, что каждая задача будет взята только одним воркером
/// - Каждый воркер имеет свой WorkerId для удобного логирования
/// </summary>
public class PdfProcessingWorker : BackgroundService
{
    private readonly ITaskStore _taskStore;
    private readonly IPdfProcessor _pdfProcessor;
    private readonly ILogger<PdfProcessingWorker> _logger;
    private readonly int _workerId;

    public PdfProcessingWorker(
        ITaskStore taskStore,
        IPdfProcessor pdfProcessor,
        ILogger<PdfProcessingWorker> logger,
        int workerId = 0)
    {
        _taskStore = taskStore;
        _pdfProcessor = pdfProcessor;
        _logger = logger;
        _workerId = workerId;
    }

    private const int MaxErrorDelaySeconds = 30;

    #region BackgroundService
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker-{WorkerId} started", _workerId);

        var consecutiveErrors = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var task = await _taskStore.DequeueAsync(stoppingToken);
                if (task == null)
                {
                    continue;
                }

                consecutiveErrors = 0; // сброс при успешном получении задачи

                _logger.LogInformation(
                    "Worker-{WorkerId} picked up task {TaskId} ({Operation})",
                    _workerId, task.Id, task.Operation);

                await ProcessTaskAsync(task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                var delaySeconds = Math.Min(
                    (int)Math.Pow(2, consecutiveErrors),
                    MaxErrorDelaySeconds);

                _logger.LogError(ex,
                    "Worker-{WorkerId}: critical error in processing loop (attempt {Attempt}), retrying in {Delay}s",
                    _workerId, consecutiveErrors, delaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
        }

        _logger.LogInformation("Worker-{WorkerId} stopped", _workerId);
    }

    private async Task ProcessTaskAsync(PdfTask task, CancellationToken stoppingToken)
    {
        task.Status = Models.TaskStatus.Processing;
        task.ProgressPercent = 0;
        await _taskStore.UpdateAsync(task, stoppingToken);

        var progress = new Progress<int>(async percent =>
        {
            task.ProgressPercent = percent;
            if (percent % 10 == 0)
            {
                await _taskStore.UpdateAsync(task, CancellationToken.None);
            }
        });

        try
        {
            var outputPath = await _pdfProcessor.ProcessAsync(task, progress, stoppingToken);

            task.OutputFilePath = outputPath;
            task.Status = Models.TaskStatus.Completed;
            task.ProgressPercent = 100;

            _logger.LogInformation(
                "Worker-{WorkerId}: task {TaskId} completed successfully",
                _workerId, task.Id);
        }
        catch (OperationCanceledException)
        {
            task.Status = Models.TaskStatus.Failed;
            task.ErrorMessage = "Task was cancelled";
            _logger.LogWarning("Worker-{WorkerId}: task {TaskId} was cancelled", _workerId, task.Id);
        }
        catch (PdfProcessingException ex)
        {
            task.Status = Models.TaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            _logger.LogWarning(ex, "Worker-{WorkerId}: task {TaskId} failed", _workerId, task.Id);
        }
        catch (Exception ex)
        {
            task.Status = Models.TaskStatus.Failed;
            task.ErrorMessage = "Internal processing error";
            _logger.LogError(ex, "Worker-{WorkerId}: task {TaskId} unexpected error", _workerId, task.Id);
        }
        finally
        {
            await _taskStore.UpdateAsync(task, CancellationToken.None);
        }
    }
    #endregion
}
