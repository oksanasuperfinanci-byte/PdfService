using Microsoft.Extensions.Hosting;
using PdfService.Application.Interfaces;
using PdfService.Application.Models;
using System.Runtime.CompilerServices;

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
/// Масштабируемость:
/// - Можно запустить несколько инстансов Worker для параллельной обработки
/// - Channel в InMemoryTaskStore гарантирует, что каждая задача будет взята только один раз
/// - Для некскольких инстансов приожения нужен Redis dvtcnj in-memory storage
///
/// Graseful Shutdown
/// - При остановке приложения CancellationToken отменяется
/// - Текущая задача завершается, новые не будут браться
/// - Незавершенные задачи останутся в статусе Processing (проблема для MVP)
///
/// TODO для production
/// - Добавление hearbeat для отслеживания "зависших" задач
/// - Реализовать retry логику с экспоненциальным backoff
/// - Добавить dead letter queue (DLQ) для постоянно падающих задач
/// </summary>
public class PdfProcessingWorker : BackgroundService
{
    private readonly ITaskStore _taskStore;
    private readonly IPdfProcessor _pdfProcessor;

    public PdfProcessingWorker(
        ITaskStore taskStore,
        IPdfProcessor pdfProcessor)
    {
        _taskStore = taskStore;
        _pdfProcessor = pdfProcessor;
    }



    #region BackgroundService
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Основной цикл обработки задач
        while (false == stoppingToken.IsCancellationRequested)
        {
            try
            {
                // DequeueAsync блокируется, пока не появится задача
                // Это эффективнее, чем polling с Task.Delay()
                var task = await _taskStore.DequeueAsync(stoppingToken);
                if (task == null)
                {
                    continue;
                }

                await ProcessTaskAsync(task, stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                // нормальное завершение работы
                break;
            }
            catch (Exception ex)
            {
                // Критическая ошибка в цикле обработки (не в конкретной задачи)               
            }
        }
    }

    private async Task ProcessTaskAsync(PdfTask task, CancellationToken stoppingToken)
    {
        // Меняем статус на Processing
        task.Status = Models.TaskStatus.Processing;
        task.ProgressPercent = 0;
        await _taskStore.UpdateAsync(task, stoppingToken);

        // Processing callback для обновления прогресса в реальном времени
        var progress = new Progress<int>(async percent =>
        {
            task.ProgressPercent = percent;
            // оБновляем только если изменение значительное (10%)
            // чтобы не забить storage лишними записями
            if (percent % 10 == 0)
            {
                await _taskStore.UpdateAsync(task, CancellationToken.None);
            }
        });

        try
        {
            // Выполняем обработку PDF
            var outputPath = await _pdfProcessor.ProcessAsync(task, progress, stoppingToken);

            // Успех!
            task.OutputFilePath = outputPath;
            task.Status = Models.TaskStatus.Completed;
            task.ProgressPercent = 100;
        }
        catch(OperationCanceledException)
        {
            // Задача была отменена (shutdown или timeout)
            task.Status = Models.TaskStatus.Failed;
            task.ErrorMessage = "Task was cancelled";
        }
        catch(PdfProcessingException ex)
        {
            // Ожидаемая ошибка обработки PDF
            task.Status = Models.TaskStatus.Failed;
            task.ErrorMessage = ex.Message;
        }
        catch (Exception)
        {
            // неожиданная ошибка
            task.Status = Models.TaskStatus.Failed;
            task.ErrorMessage = $"Internal processing error";
        }
        finally
        {
            // Всегда обновляем винальный статус
            await _taskStore.UpdateAsync(task, CancellationToken.None);
        }
    }
    #endregion
}
