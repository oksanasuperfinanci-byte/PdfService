namespace PdfService.Application.Interfaces;

/// <summary>
/// Настройки подключения к Gotenberg.
/// Секция "Gotenberg" в appsettings.json.
/// </summary>
public class GotenbergOptions
{
    public const string SectionName = "Gotenberg";

    /// <summary>
    /// Базовый URL Gotenberg (например, http://gotenberg:3000 в Docker)
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>
    /// Тайм-аут HTTP-запросов к Gotenberg (секунды)
    /// </summary>
    public int TimeoutSeconds { get; set; } = 180;
}
