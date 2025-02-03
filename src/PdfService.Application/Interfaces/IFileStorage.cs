using System.Diagnostics;

namespace PdfService.Application.Interfaces;

public interface IFileStorage
{
    Task<string> SaveAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string filePath);
    string GetFullPath(string relativePath);

    Task DeleteAsync(string filePath);

    Task<bool> ExistsAsync(string filePath);

    Task<FileInfo?> GetFileInfoAsync(string filePath);
    IAsyncEnumerable<string> GetAllFilesAsync(CancellationToken cancellationToken = default);
}

// Почему здесь данный класс - простота (KISS / YAGNI), у нас MVP.
// В данном проекте применено упрощение(Trade-off).
public class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    public string BasePath { get; set; } = "./temp-storage";

    public int FileLifetimeHours { get; set; } = 2;

    public int MaxFileSizeMb { get; set; } = 100;
}
