using PdfService.Application.Models;

namespace PdfService.Application.Interfaces;


/// <summary>
/// Хранилище задач для обработки PDF
///
/// Для MVP используется ConcurrentDirectory в памяти
/// В production это должен быть Redis или PostgresSQL, потому что:
///  - Перезапуск приложения = потеря данных всех задач
///  - Несколько истансов приложения не могут шарить очередь
///  - Нет персистентности для аудита
/// </summary>
public interface ITaskStore
{
    Task<PdfTask> AddTask(PdfTaskRequest request, CancellationToken cancellationToken = default);

    Task<PdfTask?> GetAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task UpdateAsync(PdfTask task, CancellationToken cancellationToken = default);
    Task<PdfTask?> DequeueAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PdfTask>> GetByStatusAsync(Models.TaskStatus status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PdfTask>> GetOlderThenAsync(DateTime threshold,  CancellationToken cancellationToken = default);
}
