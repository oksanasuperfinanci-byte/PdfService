using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using PdfService.Application.Interfaces;
using PdfService.Application.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfService.Infrastructure.Services;

/// <summary>
/// Реализация IPdfProcessor, делегирующая тяжёлые операции в Gotenberg:
///   - HtmlToPdf     → POST /forms/chromium/convert/html
///   - Compress       → POST /forms/pdfengines/merge (LibreOffice PDF/A optimize)
///   - OfficeToPdf    → POST /forms/libreoffice/convert
///
/// Лёгкие операции (Merge, Split, Rotate, ExtractPages) выполняются локально через PdfSharp,
/// как и в оригинальном PdfProcessor — нет смысла гонять трафик к Gotenberg.
/// </summary>
public class GotenbergPdfProcessor : IPdfProcessor
{
    private readonly HttpClient _httpClient;
    private readonly IFileStorage _storage;
    private readonly ILogger<GotenbergPdfProcessor> _logger;

    private static readonly RecyclableMemoryStreamManager MemoryStreamManager = new();

    public GotenbergPdfProcessor(
        HttpClient httpClient,
        IFileStorage storage,
        ILogger<GotenbergPdfProcessor> logger)
    {
        _httpClient = httpClient;
        _storage = storage;
        _logger = logger;
    }

    public bool IsOperationSupported(PdfOperation operation)
    {
        return operation switch
        {
            PdfOperation.Merge => true,
            PdfOperation.Split => true,
            PdfOperation.Rotate => true,
            PdfOperation.ExtractPages => true,
            PdfOperation.HtmlToPdf => true,
            PdfOperation.Compress => true,
            PdfOperation.OfficeToPdf => true,
            PdfOperation.AddWatermark => false,
            _ => false
        };
    }

    public async Task<string> ProcessAsync(
        PdfTask task,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return task.Operation switch
            {
                // Лёгкие операции — локально через PdfSharp
                PdfOperation.Merge => await MergePdfAsync(task, progress, cancellationToken),
                PdfOperation.Split => await SplitPdfAsync(task, progress, cancellationToken),
                PdfOperation.Rotate => await RotatePdfAsync(task, progress, cancellationToken),
                PdfOperation.ExtractPages => await ExtractPagesAsync(task, progress, cancellationToken),

                // Тяжёлые операции — делегируем Gotenberg
                PdfOperation.HtmlToPdf => await HtmlToPdfViaGotenbergAsync(task, progress, cancellationToken),
                PdfOperation.Compress => await CompressViaGotenbergAsync(task, progress, cancellationToken),
                PdfOperation.OfficeToPdf => await OfficeToPdfViaGotenbergAsync(task, progress, cancellationToken),

                _ => throw new PdfProcessingException(
                    $"Operation {task.Operation} is not supported",
                    task.Operation)
            };
        }
        catch (Exception ex) when (ex is not PdfProcessingException)
        {
            throw new PdfProcessingException(
                $"Unexpected error during {task.Operation}: {ex.Message}",
                task.Operation,
                task.InputFilePaths.FirstOrDefault(),
                ex);
        }
    }

    #region Gotenberg operations

    /// <summary>
    /// HTML → PDF через Gotenberg Chromium.
    /// POST /forms/chromium/convert/html
    /// Gotenberg ожидает файл index.html в multipart/form-data.
    /// </summary>
    private async Task<string> HtmlToPdfViaGotenbergAsync(
        PdfTask task,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        string htmlContent;
        if (task.Options?.TryGetValue("html", out var htmlObj) == true)
        {
            htmlContent = htmlObj.ToString()!;
        }
        else if (task.InputFilePaths.Count > 0)
        {
            await using var stream = await _storage.OpenReadAsync(task.InputFilePaths[0]);
            using var reader = new StreamReader(stream);
            htmlContent = await reader.ReadToEndAsync(cancellationToken);
        }
        else
        {
            throw new PdfProcessingException("No HTML content provided", PdfOperation.HtmlToPdf);
        }

        progress?.Report(10);

        using var form = new MultipartFormDataContent();

        // Gotenberg Chromium ожидает файл с именем index.html
        var htmlBytes = System.Text.Encoding.UTF8.GetBytes(htmlContent);
        var htmlFile = new ByteArrayContent(htmlBytes);
        htmlFile.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        form.Add(htmlFile, "files", "index.html");

        // Настройки страницы
        form.Add(new StringContent("8.27"), "paperWidth");   // A4 в дюймах
        form.Add(new StringContent("11.7"), "paperHeight");
        form.Add(new StringContent("0.79"), "marginTop");    // ~20mm
        form.Add(new StringContent("0.79"), "marginBottom");
        form.Add(new StringContent("0.59"), "marginLeft");   // ~15mm
        form.Add(new StringContent("0.59"), "marginRight");
        form.Add(new StringContent("true"), "printBackground");

        progress?.Report(30);

        var pdfBytes = await SendToGotenbergAsync(
            "/forms/chromium/convert/html", form, cancellationToken);

        progress?.Report(80);

        var outputPath = $"{Guid.NewGuid():N}_converted.pdf";
        await using var outputStream = new MemoryStream(pdfBytes);
        var savedPath = await _storage.SaveAsync(outputStream, outputPath, cancellationToken);

        progress?.Report(100);
        return savedPath;
    }

    /// <summary>
    /// Сжатие PDF через Gotenberg pdfengines.
    /// POST /forms/pdfengines/merge
    /// Gotenberg конвертирует в PDF/A, что часто уменьшает размер.
    /// </summary>
    private async Task<string> CompressViaGotenbergAsync(
        PdfTask task,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var inputPath = task.InputFilePaths?.FirstOrDefault()
            ?? throw new PdfProcessingException("No input file provided", PdfOperation.Compress);

        progress?.Report(10);

        using var form = new MultipartFormDataContent();

        await using var inputStream = await _storage.OpenReadAsync(inputPath);
        var streamContent = new StreamContent(inputStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(streamContent, "files", "document.pdf");

        // Формат PDF/A для оптимизации
        form.Add(new StringContent("PDF/A-2b"), "pdfa");

        progress?.Report(30);

        var pdfBytes = await SendToGotenbergAsync(
            "/forms/pdfengines/merge", form, cancellationToken);

        progress?.Report(80);

        var outputName = $"{Guid.NewGuid():N}_compressed.pdf";
        await using var outputStream = new MemoryStream(pdfBytes);
        var savedPath = await _storage.SaveAsync(outputStream, outputName, cancellationToken);

        progress?.Report(100);
        return savedPath;
    }

    /// <summary>
    /// Office → PDF через Gotenberg LibreOffice.
    /// POST /forms/libreoffice/convert
    /// Поддерживает .docx, .xlsx, .pptx, .odt, .ods и другие форматы.
    /// </summary>
    private async Task<string> OfficeToPdfViaGotenbergAsync(
        PdfTask task,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var inputPath = task.InputFilePaths?.FirstOrDefault()
            ?? throw new PdfProcessingException("No input file provided", PdfOperation.OfficeToPdf);

        progress?.Report(10);

        using var form = new MultipartFormDataContent();

        await using var inputStream = await _storage.OpenReadAsync(inputPath);
        var streamContent = new StreamContent(inputStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        // Gotenberg определяет формат по расширению файла
        var originalName = Path.GetFileName(inputPath);
        form.Add(streamContent, "files", originalName);

        // Ландшафтный режим по запросу
        if (task.Options?.TryGetValue("landscape", out var landscape) == true
            && landscape is true or "true")
        {
            form.Add(new StringContent("true"), "landscape");
        }

        progress?.Report(30);

        var pdfBytes = await SendToGotenbergAsync(
            "/forms/libreoffice/convert", form, cancellationToken);

        progress?.Report(80);

        var baseName = Path.GetFileNameWithoutExtension(originalName);
        var outputName = $"{Guid.NewGuid():N}_{baseName}.pdf";
        await using var outputStream = new MemoryStream(pdfBytes);
        var savedPath = await _storage.SaveAsync(outputStream, outputName, cancellationToken);

        progress?.Report(100);
        return savedPath;
    }

    /// <summary>
    /// Общий метод отправки запроса к Gotenberg.
    /// Обрабатывает HTTP-ошибки и возвращает PDF-байты.
    /// </summary>
    private async Task<byte[]> SendToGotenbergAsync(
        string endpoint,
        MultipartFormDataContent form,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(endpoint, form, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PdfProcessingException(
                $"Gotenberg request to {endpoint} timed out",
                PdfOperation.Merge, // операция будет переопределена выше
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PdfProcessingException(
                $"Failed to connect to Gotenberg at {endpoint}: {ex.Message}",
                PdfOperation.Merge,
                innerException: ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Gotenberg {Endpoint} returned {StatusCode}: {Body}",
                endpoint, (int)response.StatusCode, errorBody);

            throw new PdfProcessingException(
                $"Gotenberg returned {(int)response.StatusCode}: {errorBody}",
                PdfOperation.Merge);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    #endregion

    #region Local PdfSharp operations

    private async Task<string> MergePdfAsync(
        PdfTask task, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (task.InputFilePaths.Count < 2)
            throw new PdfProcessingException("Merge requires at least 2 input files", PdfOperation.Merge);

        using var outputDocument = new PdfDocument();
        var totalFiles = task.InputFilePaths.Count;

        for (int i = 0; i < totalFiles; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var inputStream = await _storage.OpenReadAsync(task.InputFilePaths[i]);
            using var inputDocument = PdfReader.Open(inputStream, PdfDocumentOpenMode.Import);

            for (int p = 0; p < inputDocument.PageCount; p++)
                outputDocument.AddPage(inputDocument.Pages[p]);

            progress?.Report((i + 1) * 100 / totalFiles);
        }

        return await SaveDocumentAsync(outputDocument, "merged.pdf");
    }

    private async Task<string> SplitPdfAsync(
        PdfTask task, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var inputPath = task.InputFilePaths.FirstOrDefault()
            ?? throw new PdfProcessingException("No input file provided", PdfOperation.Split);

        await using var inputStream = await _storage.OpenReadAsync(inputPath);
        using var inputDocument = PdfReader.Open(inputStream, PdfDocumentOpenMode.Import);

        var pageCount = inputDocument.PageCount;
        var outputPaths = new List<string>();

        for (int i = 0; i < pageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var singlePageDoc = new PdfDocument();
            singlePageDoc.AddPage(inputDocument.Pages[i]);

            var pagePath = await SaveDocumentAsync(singlePageDoc, $"page_{i + 1}.pdf");
            outputPaths.Add(pagePath);

            progress?.Report((i + 1) * 100 / pageCount);
        }

        return outputPaths.First();
    }

    private async Task<string> RotatePdfAsync(
        PdfTask task, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var inputPath = task.InputFilePaths.FirstOrDefault()
            ?? throw new PdfProcessingException("No input file provided", PdfOperation.Rotate);

        var angle = 90;
        if (task.Options?.TryGetValue("angle", out var angleObj) == true)
            angle = Convert.ToInt32(angleObj);

        if (angle % 90 != 0)
            throw new PdfProcessingException(
                $"Rotation angle must be a multiple of 90, got {angle}", PdfOperation.Rotate);

        await using var inputStream = await _storage.OpenReadAsync(inputPath);
        using var document = PdfReader.Open(inputStream, PdfDocumentOpenMode.Modify);

        for (int i = 0; i < document.PageCount; i++)
        {
            document.Pages[i].Rotate = (document.Pages[i].Rotate + angle) % 360;
            progress?.Report((i + 1) * 100 / document.PageCount);
        }

        return await SaveDocumentAsync(document, "rotated.pdf");
    }

    private async Task<string> ExtractPagesAsync(
        PdfTask task, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var inputPath = task.InputFilePaths.FirstOrDefault()
            ?? throw new PdfProcessingException("No input file provided", PdfOperation.ExtractPages);

        var pagesString = task.Options?.GetValueOrDefault("pages")?.ToString() ?? "1";
        var pageNumbers = ParsePageNumbers(pagesString);

        await using var inputStream = await _storage.OpenReadAsync(inputPath);
        using var inputDocument = PdfReader.Open(inputStream, PdfDocumentOpenMode.Import);
        using var outputDocument = new PdfDocument();

        var processed = 0;
        foreach (var pageNumber in pageNumbers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (pageNumber < 1 || pageNumber > inputDocument.PageCount)
                continue;

            outputDocument.AddPage(inputDocument.Pages[pageNumber - 1]);
            processed++;
            progress?.Report(processed * 100 / pageNumbers.Count);
        }

        if (outputDocument.PageCount == 0)
            throw new PdfProcessingException("No valid pages to extract", PdfOperation.ExtractPages);

        return await SaveDocumentAsync(outputDocument, "extracted.pdf");
    }

    #endregion

    #region Helpers

    private async Task<string> SaveDocumentAsync(PdfDocument document, string suggestedName)
    {
        var outputPath = $"{Guid.NewGuid():N}_{suggestedName}";

        using var memoryStream = MemoryStreamManager.GetStream();
        document.Save(memoryStream);
        memoryStream.Position = 0;

        return await _storage.SaveAsync(memoryStream, outputPath);
    }

    private static List<int> ParsePageNumbers(string pagesString)
    {
        var result = new List<int>();

        foreach (var part in pagesString.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();

            if (trimmed.Contains('-'))
            {
                var rangeParts = trimmed.Split('-');
                if (rangeParts.Length == 2
                    && int.TryParse(rangeParts[0], out var start)
                    && int.TryParse(rangeParts[1], out var end))
                {
                    for (int i = start; i <= end; i++)
                        result.Add(i);
                }
            }
            else if (int.TryParse(trimmed, out var pageNumber))
            {
                result.Add(pageNumber);
            }
        }

        return result.Distinct().OrderBy(x => x).ToList();
    }

    #endregion
}
