using Microsoft.IO;
using PdfService.Application.Interfaces;
using PdfService.Application.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
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
            PdfOperation.HtmlToPdf => false,
            PdfOperation.Rotate => false,
            PdfOperation.ExtractPages => false,
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
                /*PdfOperation.HtmlToPdf => await HtmlToPdfPdfAsync(task, progress, cancellationToken),
                PdfOperation.ExtractPages => await ExtractPagesPdfAsync(task, progress, cancellationToken),
                PdfOperation.Rotate => await RotatePdfAsync(task, progress, cancellationToken),
                PdfOperation.OfficeToPdf => await OfficeToPdfAsync(task, progress, cancellationToken),*/

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

    #endregion

    #region Helper methods
    private bool IsLibreOfficeAvaliable()
    {
        // тут будут запускаться отдельный процес libreOffice
        return false;
    }

    private async Task<string> SaveOutputDocumentAsync(PdfDocument outputDocument, string suggestedName)
    {
        var outputPath = $"{Guid.NewGuid():N}_{suggestedName}";

        using var memoryStream = manager.GetStream();
        outputDocument.Save(memoryStream);
        memoryStream.Position = 0;

        return await _storage.SaveAsync(memoryStream, outputPath);
    }
    #endregion
}
