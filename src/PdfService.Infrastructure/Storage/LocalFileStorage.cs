using Microsoft.Extensions.Options;
using PdfService.Application.Interfaces;

namespace PdfService.Infrastructure.Storage;

/// <summary>
/// Реализация файлового хранилища на локальной файловой системе
///
/// Для production рукомендуется заменить на S3/Azure Blob реализацию, потому что
///  - Локальные файлы не переживут пересоздание контейнера
///  - Нельзя горизонтально масштабировать (каждый инстанс имеет свои файлы)
///  - Нет встроенного backup/replication
///
/// Но для MVP и single-instace deployment это нормальное решение.
/// </summary>
public class LocalFileStorage : IFileStorage
{
    private readonly FileStorageOptions _options;
    private readonly string _basePath;

    public LocalFileStorage(IOptions<FileStorageOptions> options)
    {
        _options = options.Value;

        // Конвертируем относительный путь в абсолютный
        _basePath = Path.GetFullPath(_options.BasePath);

        // Создаем директорию если ее нет
        if(false == Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
        }
    }

    #region IFileStorege
    public Task DeleteAsync(string filePath)
    {
        var fullPath = GetFullPath(filePath);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);            
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string filePath)
    {
        var fullPath = GetFullPath(filePath);
        return Task.FromResult(File.Exists(fullPath));
    }

    public async IAsyncEnumerable<string> GetAllFilesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        foreach (var file in Directory.EnumerateFiles(_basePath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Возвращаем относительный путь
            yield return Path.GetFileName(file);

            // Даем возможность другим потокам работать
            await Task.Yield();
        }
    }

    public Task<FileInfo?> GetFileInfoAsync(string filePath)
    {
        var fullPath = GetFullPath(filePath);
        var fileInfo = new FileInfo(fullPath);

        return Task.FromResult(fileInfo.Exists ? fileInfo : null);
    }

    public string GetFullPath(string relativePath)
    {
        // Защита от path traversal атак (../../etc/passwd)
        var fullPath = Path.GetFullPath(Path.Combine(_basePath, relativePath));

        if (false == fullPath.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Access denied to path outside storage: {relativePath}");
        }

        return fullPath;
    }

    public Task<Stream> OpenReadAsync(string filePath)
    {
        var fullPath = GetFullPath(filePath);

        if (false == File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {filePath}", filePath);
        }

        // Важно: возвращаем поток, который вызывающий код должен закрыть!
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true
            );

        return Task.FromResult<Stream>(stream);
    }

    public async Task<string> SaveAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        // Генериуеи уникальное имя, чтобы избежать коллзий
        // Формат: {guid}_{original_name}
        // Это позволит сохранить оригинальное имя для отладки, но гарантировать уникальность
        var uniqueFileName = $"{Guid.NewGuid():N}_{SanitizeFileName(fileName)}";
        var relativePath = uniqueFileName;
        var fullPath = Path.Combine(_basePath, relativePath);

        // Используем FileStream с буфером для эффективной записи
        // UseAsync = true важно для неблокируюего I/O
        await using var fileStream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 8192, // 80KB буфер - думаю хорошо для PDF
            useAsync: true
            );

        await stream.CopyToAsync( fileStream, cancellationToken );

        return relativePath;
    }

    #endregion
    private object SanitizeFileName(string fileName)
    {
        // Удаляем все символы, которые могут быть опасны в пути
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

        // Ограничиваем длину имени
        const int maxLength = 100;
        if (sanitized.Length > maxLength)
        {
            var extension = Path.GetExtension(sanitized);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(sanitized);
            sanitized = nameWithoutExt[..(maxLength - extension.Length)] + extension;
        }

        return sanitized;
    }
}
