using PdfService.Application.Models;

namespace PdfService.Application.Interfaces;

public interface IPdfProcessor
{
    Task<string> ProcessAsync(PdfTask task, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    bool IsOperationSupported(PdfOperation operation);
}

/// <summary>
/// Исключение при ошибке обработки PDF.
/// Содержит дополнительную информацию для диагностики.
/// </summary>
public class PdfProcessingException : Exception
{
    public PdfOperation Operation { get; }
    public string? FilePath { get; set; }

    public PdfProcessingException(string message, PdfOperation operation, string? filePath = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Operation = operation;
        FilePath = filePath;
    }
}
