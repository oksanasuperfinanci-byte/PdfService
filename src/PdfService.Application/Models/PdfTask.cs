namespace PdfService.Application.Models;

/// <summary>
/// Наш первый класс
/// Задача обработки PDF\
/// Созданиется при получении запросоа, обрабатывается фоновым сервисом
/// </summary>
public class PdfTask
{
    public Guid Id { get; init; }=Guid.NewGuid();
    public required PdfOperation Operation { get; init; }
    public required IReadOnlyList<string> InputFilePaths { get; init; }
    public Dictionary<string, object>? Options { get; init; }

    /// <summary>
    /// Путь к результирующему файлу (заполнится после успешной обработки)
    /// </summary>
    public string? OutputFilePath { get; set; }
    public TaskStatus Status { get; set; }

    /// <summary>
    /// Сообщение об ошибке (если Status == Failed)
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Время создания задачи
    /// </summary>
    public DateTime CreateAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Время последенго обновления статуса
    /// </summary>
    public DateTime UpdateAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Прогресс выполнения 0-100
    /// Полезно для длительных операций вроде Merge большого количества файлов
    /// </summary>
    public int ProgressPercent { get; set; }
}

// записи запроса и ответа задачи
public record PdfTaskRequest
{
    /// <summary>
    /// Тип операции для выполнения
    /// </summary>
    public required PdfOperation Operation { get; init; }

    /// <summary>
    /// Пути к входным файлам (относительно TempStorage).
    /// Для Merge - несколько файлов, для остальных операций - обычно один.
    /// </summary>
    public required IReadOnlyList<string> InputFilePaths { get; init; }

    /// <summary>
    /// Дополнительные параметры в зависимости от операции.
    /// Например: {"pages": "1,3,5-10"} для ExtractPages,
    ///           {"angle": 90} для Rotate,
    ///           {"html": "<h1>Hello</h1>"} для HtmlToPdf
    /// </summary>
    public Dictionary<string, object>? Options { get; init; }
}

/// <summary>
/// DTO для ответа клиента о статусе задачи
/// Не выставляем внутренние пути к файлам.
/// </summary>
public record PdfTaskResponse
{
    public required Guid TaskId { get; init; }
    public required TaskStatus Status { get; init; }
    public required PdfOperation Operation { get; init; }
    public int ProcessPercent { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// URL для скачивания результата (если Status == Completed)
    /// </summary>
    public string? DownloadUrl { get; init; }

    public DateTime? CreateAt { get; init; }
    public DateTime? UpdateAt { get; init; }
}

/// <summary>
/// типы операций над PDF
/// Каждая операция имеет свою стратегию обработки в PdfProcessor
/// </summary>
public enum PdfOperation
{
    Merge,          // Объединение нескольких PDF в один
    Split,          // Разделение PDF на отдельные страницы
    Compress,       // Сжатие PDF (уменьшение размера файла)
    HtmlToPdf,      // Конвертация HTML в PDF через Puppeteer
    OfficeToPdf,    // Конвертация Word/Excel через LibreOffice
    Rotate,         // Поворот страниц
    ExtractPages,   // Извлечение выбранных страниц
    AddWatermark    // Добавление водяного знака
}

/// <summary>
/// Статус выполнения задачи.
/// Клиент отправляет статус через GET /api/tasks/{id}/status
/// </summary>
public enum TaskStatus
{
    Pending,        // Задача создана, ожидает в очереди
    Processing,     // Задача выполняется
    Completed,      // Успешно завершена, файл готов к скачиванию
    Failed,         // Ошибка при выполнении
    Expired         // Результат удалён (прошло время хранения)
}
