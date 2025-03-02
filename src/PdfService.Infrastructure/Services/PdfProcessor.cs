using Microsoft.IO;
using PdfService.Application.Interfaces;
using PdfService.Application.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PuppeteerSharp.Media;
using System.Diagnostics;

namespace PdfService.Infrastructure.Services;

/// <summary>
/// Реализация процесса PDF операций
///
/// Используем паттерн Strategy внутри: каждая операция имеет свой метод обработки.
/// Это позволяет легко добавлять новые операции без изменений общей структуры.
///
/// Зависимости:
///  - PdfSharp: Merge, Split, Rotate, ExtractPages
///  
/// </summary>
public class PdfProcessor : IPdfProcessor
{

    private readonly IFileStorage _storage;

    private static readonly RecyclableMemoryStreamManager manager = new RecyclableMemoryStreamManager();

    private static bool _browserDownloaded;
    private static readonly SemaphoreSlim _browserDownloadLock = new(1, 1);

    public PdfProcessor(IFileStorage storage)
    {
        _storage = storage;
    }

    #region IPdfProcessor
    public bool IsOperationSupported(PdfOperation operation)
    {
        return operation switch
        {
            PdfOperation.Merge => true,
            PdfOperation.Split => true,
            PdfOperation.HtmlToPdf => true,
            PdfOperation.Rotate => true,
            PdfOperation.ExtractPages => true,
            PdfOperation.OfficeToPdf => IsLibreOfficeAvaliable(),
            PdfOperation.AddWatermark => false, // TODO: Реализовать через PdfStatus
            PdfOperation.Compress => false,     // TODO: Реализовать через GhostScript
            _ => false
        };
    }


    public async Task<string> ProcessAsync(PdfTask task, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var outputPath = task.Operation switch
            {
                PdfOperation.Merge => await MergePdfAsync(task, progress, cancellationToken),
                PdfOperation.Split => await SplitPdfAsync(task, progress, cancellationToken),
                PdfOperation.HtmlToPdf => await HtmlToPdfPdfAsync(task, progress, cancellationToken),
                PdfOperation.ExtractPages => await ExtractPagesPdfAsync(task, progress, cancellationToken),
                PdfOperation.Rotate => await RotatePdfAsync(task, progress, cancellationToken),
                /*PdfOperation.OfficeToPdf => await OfficeToPdfAsync(task, progress, cancellationToken),*/

                _ => throw new PdfProcessingException(
                    $"Operation {task.Operation} is not supported",
                    task.Operation)
            };

            stopwatch.Stop();

            return outputPath;
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

   
    #endregion

    #region PDF Operations

    // TODO: Реализовать разбивку по страницам указаным пользователем
    private async Task<string> SplitPdfAsync(PdfTask task, IProgress<int>? progress, CancellationToken cancellationToken)
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

            var pagePath = await SaveOutputDocumentAsync(singlePageDoc, $"page_{i + 1}.pdf");
            outputPaths.Add(pagePath);

            progress?.Report((i + 1) * 100 / pageCount);
        }

        // Для Split возвращаем путь к первому файлу
        // В реальности нужно создать ZIP аръив со всеми страницами
        // TODO: Добавить создание ZIP архива
        return outputPaths.First();
    }

    private async Task<string> MergePdfAsync(PdfTask task, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (task.InputFilePaths.Count < 2)
        {
            throw new PdfProcessingException(
                "Merge requires at least 2 input files",
                PdfOperation.Merge
                );
        }

        using var outputDocument = new PdfDocument();
        var totalFiles = task.InputFilePaths.Count;
        var processedFiles = 0;

        foreach (var inputPath in task.InputFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var inputStream = await _storage.OpenReadAsync(inputPath);

            // Import mode позволяет копировать страницы между документами
            using var inputDocument = PdfReader.Open(inputStream, PdfDocumentOpenMode.Import);

            var pageCount = inputDocument.PageCount;
            for (int i = 0; i < pageCount; i++)
            {
                outputDocument.AddPage(inputDocument.Pages[i]);
            }
            processedFiles++;
            progress?.Report(processedFiles * 100 / totalFiles);
        }
        
        // Сохраняем результат
        return await SaveOutputDocumentAsync(outputDocument, "merged.pdf");
    }

    private async Task<string> RotatePdfAsync(
        PdfTask task,
        IProgress<int>? progress,
        CancellationToken cancellationToken
        )
    {
        var inputPath = task.InputFilePaths.FirstOrDefault()
            ?? throw new PdfProcessingException("No input file provided", PdfOperation.Rotate);

        // Получаем угол поворота из опций (по умолчанию 90)
        var angle = 90;
        if(task.Options?.TryGetValue("angle", out var angleObj) == true)
        {
            angle = Convert.ToInt32(angleObj);
        }

        // Валидация угла
        if (angle % 90 != 0)
        {
            throw new PdfProcessingException($"Rotation angle must be a multiple of 90, got{angle}", PdfOperation.Rotate);
        }

        await using var inputStream = await _storage.OpenReadAsync(inputPath);
        using var document = PdfReader.Open(inputStream, PdfDocumentOpenMode.Modify);

        var pageCount = document.PageCount;
        for (int i = 0; i < pageCount; i++)
        {
            var page = document.Pages[i];
            //Rotate складывает углы, поэтому нормализуем
            page.Rotate = (page.Rotate + angle) % 360;

            progress?.Report((i + 1) * 100 / pageCount);
        }

        return await SaveOutputDocumentAsync(document, "rotated.pdf");

    }

    private async Task<string> ExtractPagesPdfAsync(
        PdfTask task,
        IProgress<int>? progress,
        CancellationToken cancellationToken
        )
    {
        var inputPath = task.InputFilePaths.FirstOrDefault()
           ?? throw new PdfProcessingException("No input file provided", PdfOperation.Rotate);

        var pagesString = task.Options?.GetValueOrDefault("pages")?.ToString() ?? "1";
        var pageNumbers = ParsePageNumers(pagesString);

        await using var inputStream = await _storage.OpenReadAsync(inputPath);
        using var inputDocument =  PdfReader.Open(inputStream, PdfDocumentOpenMode.Import);
        using var outputDocument = new PdfDocument();

        var totalPages = pageNumbers.Count;
        var progressed = 0;

        foreach (var pageNumber in pageNumbers)
        {
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Проверяем, что страница существует (нумерация с 1)
                if (pageNumber < 1 || pageNumber > inputDocument.PageCount)
                {
                    continue;
                }

                outputDocument.AddPage(inputDocument.Pages[pageNumber - 1]);
                progressed++;
                progress?.Report(progressed * 100 / totalPages);
            }

            if (outputDocument.PageCount == 0)
            {
                throw new PdfProcessingException(
                    "No valid pages to extract",
                    PdfOperation.ExtractPages
                    );
            }
        }
            return await SaveOutputDocumentAsync(outputDocument, "extracted.pdf");
    }
    private async Task<string> HtmlToPdfPdfAsync(PdfTask task, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        // Получаем HTML контент
        string htmlContent;
        if (task.Options?.TryGetValue("html", out var htmlObj) == true)
        {
            htmlContent = htmlObj.ToString();
        }
        else if (task.InputFilePaths.Count > 0)
        {
            // Читаем HTML из файла
            await using var stream = await _storage.OpenReadAsync(task.InputFilePaths[0]);
            using var reader = new StreamReader(stream);
            htmlContent = await reader.ReadToEndAsync(cancellationToken);
        }
        else
        {
            throw new PdfProcessingException(
                "No HTML content provided",
                PdfOperation.HtmlToPdf);
        }

        progress?.Report(10);

        // Убеждаемся, что браузер скачан
        await EnsureBrowserDownloadedAsync();
        progress?.Report(30);

        // Запускаем браузер
        await using var browser = await PuppeteerSharp.Puppeteer.LaunchAsync(new PuppeteerSharp.LaunchOptions
        {
            Headless = true,
            Args = new[]
            {
                "--no-sandbox", // нужно для Docker
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage" // Решает проблемы с памятью в контейнере
            }
        });

        await using var page = await browser.NewPageAsync();
        progress?.Report(50);

        await page.SetContentAsync(htmlContent);
        progress?.Report(70);

        // Генерируем PDF
        var pdfBytes = await page.PdfDataAsync(new PuppeteerSharp.PdfOptions
        {
            Format = PaperFormat.A4,
            PrintBackground = true,
            MarginOptions = new MarginOptions
            {
                Top = "20mm",
                Bottom = "20mm",
                Left = "15mm",
                Right = "15mm"
            }
        });

        progress?.Report(90);

        // Сохраняем результат
        var outputPath = $"{Guid.NewGuid():N}_convertd.pdf";
        await using var outputStream = new MemoryStream(pdfBytes);
        var savedPath = await _storage.SaveAsync(outputStream, outputPath, cancellationToken);

        progress?.Report(100);
        return savedPath;
    }
    #endregion

    #region Helper methods
    private static bool IsLibreOfficeAvaliable()
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "libreoffice",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> SaveOutputDocumentAsync(PdfDocument outputDocument, string suggestedName)
    {
        var outputPath = $"{Guid.NewGuid():N}_{suggestedName}";

        using var memoryStream = manager.GetStream();
        outputDocument.Save(memoryStream);
        memoryStream.Position = 0;

        return await _storage.SaveAsync(memoryStream, outputPath);
    }
    private List<int> ParsePageNumers(string pagesString)
    {
        var result = new List<int>();
        var parts = pagesString.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var trimmed = part.Trim();

            if (trimmed.Contains('-'))
            {
                // Это диапазон, например 5-10
                var rangeParts = trimmed.Split('-');
                if (rangeParts.Length == 2
                    && int.TryParse(rangeParts[0], out var start)
                    && int.TryParse(rangeParts[1], out var end))
                {
                    for (int i = start; i <= end; i++)
                    {
                        result.Add(i);
                    }
                }
            }

            else if (int.TryParse(trimmed, out var pageNumber))
            {
                result.Add(pageNumber);
            }
        }

        return result.Distinct().OrderBy(x => x).ToList();
    }
    private async Task EnsureBrowserDownloadedAsync()
    {
        if (_browserDownloaded) 
        {
            return;
        }

        await _browserDownloadLock.WaitAsync();
        try
        {
            if (_browserDownloaded)
            {
                return;
            }

            await new PuppeteerSharp.BrowserFetcher().DownloadAsync();
            _browserDownloaded = true;
        }
        finally
        {
            _browserDownloadLock.Release();
        }
    }

    #endregion
}
