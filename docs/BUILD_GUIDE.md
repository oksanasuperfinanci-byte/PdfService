# PdfService — Полное руководство по созданию с нуля

> Микросервис обработки PDF на .NET 9 с Clean Architecture.
> От первого `dotnet new` до distributed-системы с Redis и Gotenberg.

---

## Оглавление

1. [Философия проекта](#1-философия-проекта)
2. [Этап 0 — Создание решения](#2-этап-0--создание-решения)
3. [Этап 1 — Модели (Словарь приложения)](#3-этап-1--модели-словарь-приложения)
4. [Этап 2 — Интерфейсы (Контракты)](#4-этап-2--интерфейсы-контракты)
5. [Этап 3 — Бизнес-логика (PdfProcessingWorker)](#5-этап-3--бизнес-логика-pdfprocessingworker)
6. [Этап 4 — Infrastructure: хранилище файлов](#6-этап-4--infrastructure-хранилище-файлов)
7. [Этап 5 — Infrastructure: InMemoryTaskStore](#7-этап-5--infrastructure-inmemorytaskstore)
8. [Этап 6 — Infrastructure: PdfProcessor (PdfSharp)](#8-этап-6--infrastructure-pdfprocessor-pdfsharp)
9. [Этап 7 — WebAPI: контроллер и DI](#9-этап-7--webapi-контроллер-и-di)
10. [Этап 8 — Новые операции: Rotate, ExtractPages](#10-этап-8--новые-операции-rotate-extractpages)
11. [Этап 9 — HtmlToPdf через PuppeteerSharp](#11-этап-9--htmltopdf-через-puppeteersharp)
12. [Этап 10 — Compress через Ghostscript](#12-этап-10--compress-через-ghostscript)
13. [Этап 11 — Рефакторинг: убираем протекающую абстракцию](#13-этап-11--рефакторинг-убираем-протекающую-абстракцию)
14. [Этап 12 — Стабилизация MVP](#14-этап-12--стабилизация-mvp)
15. [Этап 13 — Docker и Gotenberg](#15-этап-13--docker-и-gotenberg)
16. [Этап 14 — Redis: distributed task queue](#16-этап-14--redis-distributed-task-queue)
17. [Архитектурные решения и trade-offs](#17-архитектурные-решения-и-trade-offs)

---

## 1. Философия проекта

### Для кого

MVP для офиса на 15 человек. Пользователи загружают PDF через REST API,
система обрабатывает их в фоне (merge, split, rotate, compress, конвертация),
пользователь скачивает результат по task ID.

### Архитектура

**Clean Architecture (Луковая архитектура)** — зависимости идут только внутрь:

```
┌─────────────────────────────────────────┐
│              WebAPI Layer               │  ← Контроллеры, DI, конфиг
│  ┌───────────────────────────────────┐  │
│  │        Application Layer          │  │  ← Интерфейсы, модели, воркеры
│  │  ┌─────────────────────────────┐  │  │
│  │  │    Infrastructure Layer     │  │  │  ← PdfSharp, Redis, файловая система
│  │  └─────────────────────────────┘  │  │
│  └───────────────────────────────────┘  │
└─────────────────────────────────────────┘
```

**Правило:** Application не знает об Infrastructure. Он работает с интерфейсами.
Это позволяет заменить `InMemoryTaskStore` на `RedisTaskStore` без изменения
бизнес-логики.

### Принципы

- **KISS** — не усложняй, пока не вынудят
- **YAGNI** — не строй то, что не нужно сегодня
- **Dependency Inversion** — зависим от абстракций, не от реализаций
- **Asynchronous by default** — все I/O операции через async/await

---

## 2. Этап 0 — Создание решения

> Коммит: `1596996 Init solution and project`

```bash
# Создаём solution
dotnet new sln -n PdfService

# Три проекта — три слоя
dotnet new classlib -n PdfService.Application -o src/PdfService.Application
dotnet new classlib -n PdfService.Infrastructure -o src/PdfService.Infrastructure
dotnet new webapi -n PdfService.WebApi -o src/PdfService.WebApi

# Подключаем к solution
dotnet sln add src/PdfService.Application
dotnet sln add src/PdfService.Infrastructure
dotnet sln add src/PdfService.WebApi

# Зависимости между проектами (стрелки Clean Architecture)
cd src/PdfService.Infrastructure
dotnet add reference ../PdfService.Application

cd ../PdfService.WebApi
dotnet add reference ../PdfService.Application
dotnet add reference ../PdfService.Infrastructure
```

**Структура после этого шага:**

```
PdfService/
├── PdfService.sln
└── src/
    ├── PdfService.Application/          ← Бизнес-логика, интерфейсы
    ├── PdfService.Infrastructure/       ← Реализации, библиотеки
    └── PdfService.WebApi/               ← REST API, точка входа
```

**Почему три проекта, а не один:**

| Один проект | Три проекта |
|---|---|
| Всё в куче, контроллер может напрямую дёргать `File.Delete()` | Компилятор запрещает Application зависеть от Infrastructure |
| Сменить хранилище = переписать всё | Сменить хранилище = написать новый класс, поменять одну строку в DI |
| "Быстрее начать" | "Быстрее масштабировать" |

Для MVP из одного файла три проекта — overkill. Но мы планируем расти
(Gotenberg, Redis, возможно S3), поэтому закладываем структуру сразу.

---

## 3. Этап 1 — Модели (Словарь приложения)

> Коммит: `43ecff5 Create Models and Interfaces`

**Первый файл** во всём проекте — `PdfTask.cs`. Нельзя написать интерфейс
`ProcessAsync(PdfTask task)`, пока не существует класса `PdfTask`.

### `src/PdfService.Application/Models/PdfTask.cs`

```csharp
// 1. Какие операции мы вообще умеем делать?
public enum PdfOperation
{
    Merge,          // Объединение нескольких PDF в один
    Split,          // Разделение PDF на отдельные страницы
    Compress,       // Сжатие (уменьшение размера)
    HtmlToPdf,      // HTML → PDF
    OfficeToPdf,    // Word/Excel → PDF
    Rotate,         // Поворот страниц
    ExtractPages,   // Извлечение выбранных страниц
    AddWatermark    // Водяной знак (TODO)
}

// 2. Как выглядит "единица работы"?
public class PdfTask
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required PdfOperation Operation { get; init; }
    public required IReadOnlyList<string> InputFilePaths { get; init; }
    public Dictionary<string, object>? Options { get; init; }
    public string? OutputFilePath { get; set; }
    public TaskStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreateAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdateAt { get; set; } = DateTime.UtcNow;
    public int ProgressPercent { get; set; }
}

// 3. Статусы жизненного цикла задачи
public enum TaskStatus
{
    Pending,     // В очереди
    Processing,  // Выполняется
    Completed,   // Готово
    Failed,      // Ошибка
    Expired      // Файл удалён
}
```

### DTO: Request и Response

```csharp
// Что пользователь может нам ОТПРАВИТЬ (урезанный набор полей)
public record PdfTaskRequest
{
    public required PdfOperation Operation { get; init; }
    public required IReadOnlyList<string> InputFilePaths { get; init; }
    public Dictionary<string, object>? Options { get; init; }
}

// Что мы ОТДАЁМ пользователю (без внутренних путей!)
public record PdfTaskResponse
{
    public required Guid TaskId { get; init; }
    public required TaskStatus Status { get; init; }
    public required PdfOperation Operation { get; init; }
    public int ProcessPercent { get; init; }
    public string? ErrorMessage { get; init; }
    public string? DownloadUrl { get; init; }    // URL, а не путь на диске!
    public DateTime? CreateAt { get; init; }
    public DateTime? UpdateAt { get; init; }
}
```

**Зачем разделять PdfTask / PdfTaskRequest / PdfTaskResponse:**

| Без разделения (опасно) | С разделением (безопасно) |
|---|---|
| Хакер может отправить `"Status": "Completed"` в JSON | `PdfTaskRequest` не имеет поля `Status` — физически невозможно подделать |
| API выдаёт `OutputFilePath: /var/www/temp/123.pdf` | `PdfTaskResponse` выдаёт `DownloadUrl: /api/pdf/download/123` |
| Внутренние поля утекают наружу | Чёрный ящик: вход → обработка → выход |

---

## 4. Этап 2 — Интерфейсы (Контракты)

> Коммит: `43ecff5 Create Models and Interfaces` (тот же коммит)

Теперь, когда "словарь" готов, можно описать, **что система должна уметь делать**,
не говоря **как**.

### `IPdfProcessor` — Процессор PDF

```csharp
public interface IPdfProcessor
{
    Task<string> ProcessAsync(
        PdfTask task,
        IProgress<int>? progress = null,         // callback прогресса
        CancellationToken cancellationToken = default);

    bool IsOperationSupported(PdfOperation operation);
}
```

Один метод `ProcessAsync` на все операции. Как система понимает, **что** делать?
Через свойство `task.Operation` (enum), который мы определили на этапе 1.

### `ITaskStore` — Хранилище задач

```csharp
public interface ITaskStore
{
    Task<PdfTask> AddTask(PdfTaskRequest request, CancellationToken ct = default);
    Task<PdfTask?> GetAsync(Guid taskId, CancellationToken ct = default);
    Task DeleteAsync(Guid taskId, CancellationToken ct = default);
    Task UpdateAsync(PdfTask task, CancellationToken ct = default);

    // Блокирующее ожидание — воркер "спит", пока не появится задача
    Task<PdfTask?> DequeueAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PdfTask>> GetByStatusAsync(TaskStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<PdfTask>> GetOlderThenAsync(DateTime threshold, CancellationToken ct = default);
}
```

### `IFileStorage` — Файловое хранилище

```csharp
public interface IFileStorage
{
    Task<string> SaveAsync(Stream stream, string fileName, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string filePath);
    Task DeleteAsync(string filePath);
    Task<bool> ExistsAsync(string filePath);
    Task<FileInfo?> GetFileInfoAsync(string filePath);
    IAsyncEnumerable<string> GetAllFilesAsync(CancellationToken ct = default);
}
```

**Обрати внимание:** здесь нет метода `GetFullPath()`. Интерфейс работает
с относительными путями и потоками (`Stream`). Это позволит в будущем заменить
локальный диск на S3 без изменения бизнес-логики.

### `FileStorageOptions` — Настройки хранилища

```csharp
public class FileStorageOptions
{
    public const string SectionName = "FileStorage";
    public string BasePath { get; set; } = "./temp-storage";
    public int FileLifetimeHours { get; set; } = 2;
    public int MaxFileSizeMb { get; set; } = 100;
}
```

**Trade-off:** `BasePath` — это деталь реализации (локальная файловая система),
ей не место в Application. Но для MVP одна секция настроек `FileStorage`
удобнее, чем две (`StoragePolicy` + `LocalStorageConfig`).
Это осознанный технический долг — KISS/YAGNI.

---

## 5. Этап 3 — Бизнес-логика (PdfProcessingWorker)

> Коммит: `e482396 Finish Application Layer`

Третий файл в Application — **фоновый воркер**. Он описывает сценарий:
"Как задача берётся из очереди и обрабатывается".

### `src/PdfService.Application/Jobs/PdfProcessingWorker.cs`

```csharp
public class PdfProcessingWorker : BackgroundService
{
    private readonly ITaskStore _taskStore;
    private readonly IPdfProcessor _pdfProcessor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // 1. Ждём задачу (блокирующий await — не тратит CPU)
            var task = await _taskStore.DequeueAsync(stoppingToken);
            if (task == null) continue;

            // 2. Обрабатываем
            await ProcessTaskAsync(task, stoppingToken);
        }
    }

    private async Task ProcessTaskAsync(PdfTask task, CancellationToken ct)
    {
        task.Status = TaskStatus.Processing;
        await _taskStore.UpdateAsync(task, ct);

        try
        {
            var outputPath = await _pdfProcessor.ProcessAsync(task, progress, ct);
            task.OutputFilePath = outputPath;
            task.Status = TaskStatus.Completed;
        }
        catch (PdfProcessingException ex)
        {
            task.Status = TaskStatus.Failed;
            task.ErrorMessage = ex.Message;
        }
        finally
        {
            await _taskStore.UpdateAsync(task, CancellationToken.None);
        }
    }
}
```

**Почему Worker в Application, а не в Infrastructure:**

Этот класс — **бизнес-правило** ("обрабатываем задачи по очереди").
Он зависит только от интерфейсов (`ITaskStore`, `IPdfProcessor`).
Не знает ни о PdfSharp, ни о Redis, ни о файловой системе.

Если завтра мы перенесём приложение из Docker в Azure Functions —
этот код не изменится. Изменятся только реализации интерфейсов.

**Почему нельзя обрабатывать PDF прямо в контроллере:**

Merge 100 PDF может занять 2 минуты. HTTP-таймаут браузера — 30 секунд.
Поэтому контроллер сразу отвечает `202 Accepted` с `taskId`,
а тяжёлую работу делает фоновый воркер.

---

## 6. Этап 4 — Infrastructure: хранилище файлов

> Коммит: `ab9add7 Infrastructure layer`

Переходим от "чертежей" к "строительству". Первая реализация —
`LocalFileStorage`.

### `src/PdfService.Infrastructure/Storage/LocalFileStorage.cs`

Ключевые моменты реализации:

```csharp
public class LocalFileStorage : IFileStorage
{
    private readonly string _basePath;

    public async Task<string> SaveAsync(Stream stream, string fileName, CancellationToken ct)
    {
        // Уникальное имя: {guid}_{sanitized_name} — нет коллизий
        var uniqueFileName = $"{Guid.NewGuid():N}_{SanitizeFileName(fileName)}";
        var fullPath = Path.Combine(_basePath, uniqueFileName);

        await using var fileStream = new FileStream(
            fullPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 8192, useAsync: true);

        await stream.CopyToAsync(fileStream, ct);
        return uniqueFileName;  // Возвращаем ОТНОСИТЕЛЬНЫЙ путь
    }

    // GetFullPath — НЕ в интерфейсе, только внутренний метод
    public string GetFullPath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_basePath, relativePath));

        // Защита от path traversal (../../etc/passwd)
        if (!fullPath.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Access denied");

        return fullPath;
    }
}
```

**Безопасность:**
- `SanitizeFileName` — удаляет опасные символы, ограничивает длину до 100
- `GetFullPath` — проверка на path traversal атаку
- `useAsync: true` — неблокирующий I/O, не засоряет ThreadPool

---

## 7. Этап 5 — Infrastructure: InMemoryTaskStore

> Коммит: `ab9add7 Infrastructure layer` (тот же коммит)

### `src/PdfService.Infrastructure/Storage/InMemoryTaskStore.cs`

Используем две структуры данных:

```csharp
public class InMemoryTaskStore : ITaskStore
{
    // O(1) доступ по ID
    private readonly ConcurrentDictionary<Guid, PdfTask> _tasks = new();

    // Thread-safe FIFO очередь
    private readonly Channel<Guid> _pendingQueue;

    public InMemoryTaskStore()
    {
        _pendingQueue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,   // Несколько воркеров могут читать
            SingleWriter = false,   // Несколько запросов могут писать
        });
    }
}
```

**Почему `Channel<T>`, а не `ConcurrentQueue<T>`:**

| ConcurrentQueue | Channel |
|---|---|
| `TryDequeue` — polling (жрёт CPU в цикле) | `WaitToReadAsync` — блокирующий await (0% CPU) |
| Нет ограничения размера | `BoundedChannel(1000)` — защита от переполнения |
| Нет async/await из коробки | Полная поддержка async |

**Ограничения MVP (осознанные):**
- Данные теряются при перезапуске → решим Redis-ом на этапе 14
- Не работает с несколькими инстансами → решим Redis-ом
- Нет персистентности для аудита → решим позже

---

## 8. Этап 6 — Infrastructure: PdfProcessor (PdfSharp)

> Коммит: `ab9add7 Infrastructure layer`

### `src/PdfService.Infrastructure/Services/PdfProcessor.cs`

Паттерн "швейцарский нож" — один `ProcessAsync`, внутри switch по операциям:

```csharp
public async Task<string> ProcessAsync(PdfTask task, IProgress<int>? progress, CancellationToken ct)
{
    return task.Operation switch
    {
        PdfOperation.Merge => await MergePdfAsync(task, progress, ct),
        PdfOperation.Split => await SplitPdfAsync(task, progress, ct),
        PdfOperation.Rotate => await RotatePdfAsync(task, progress, ct),
        PdfOperation.ExtractPages => await ExtractPagesPdfAsync(task, progress, ct),
        PdfOperation.HtmlToPdf => await HtmlToPdfAsync(task, progress, ct),
        PdfOperation.Compress => await CompressAsync(task, progress, ct),
        _ => throw new PdfProcessingException($"Not supported: {task.Operation}", task.Operation)
    };
}
```

**Первая операция — Merge:**

```csharp
private async Task<string> MergePdfAsync(PdfTask task, IProgress<int>? progress, CancellationToken ct)
{
    using var outputDocument = new PdfDocument();

    foreach (var inputPath in task.InputFilePaths)
    {
        ct.ThrowIfCancellationRequested();

        await using var inputStream = await _storage.OpenReadAsync(inputPath);
        using var inputDocument = PdfReader.Open(inputStream, PdfDocumentOpenMode.Import);

        for (int i = 0; i < inputDocument.PageCount; i++)
            outputDocument.AddPage(inputDocument.Pages[i]);

        progress?.Report(processedFiles * 100 / totalFiles);
    }

    return await SaveDocumentAsync(outputDocument, "merged.pdf");
}
```

**NuGet-пакеты для Infrastructure:**
- `PDFsharp 6.2.4` — Merge, Split, Rotate, ExtractPages
- `Microsoft.IO.RecyclableMemoryStream` — эффективные буферы без фрагментации

---

## 9. Этап 7 — WebAPI: контроллер и DI

> Коммит: `5840239 Create first WebApi Controller: Merge`

### Dependency Injection — связываем слои

`src/PdfService.WebApi/Extensions/ServiceCollectionExtensions.cs`:

```csharp
public static IServiceCollection AddPdfServices(
    this IServiceCollection services, IConfiguration configuration)
{
    services.Configure<FileStorageOptions>(
        configuration.GetSection(FileStorageOptions.SectionName));

    services.AddSingleton<IFileStorage, LocalFileStorage>();
    services.AddSingleton<ITaskStore, InMemoryTaskStore>();
    services.AddSingleton<IPdfProcessor, PdfProcessor>();

    services.AddHostedService<PdfProcessingWorker>();

    return services;
}
```

**Это место, где Application встречается с Infrastructure.**
Без этого файла — набор несвязанных классов. С ним — работающее приложение.

### Контроллер — "пункт приёма заказов"

```csharp
[HttpPost("merge")]
[RequestSizeLimit(500_000_000)]
public async Task<ActionResult<PdfTaskResponse>> Merge(
    List<IFormFile> files, CancellationToken ct)
{
    if (files == null || files.Count < 2)
        return BadRequest(new { error = "At least 2 files required" });

    return await CreatePdfTaskAsync(PdfOperation.Merge, files, null, ct);
}
```

**Контроллер НЕ обрабатывает PDF.** Он:
1. Валидирует вход (размер, формат)
2. Сохраняет файлы через `IFileStorage`
3. Создаёт задачу в `ITaskStore`
4. Отвечает `202 Accepted` с `taskId`

Обработка происходит в `PdfProcessingWorker`.

### Скачивание результата

```csharp
[HttpGet("download/{taskId:Guid}")]
public async Task<IActionResult> Download(Guid taskId)
{
    var task = await _taskStore.GetAsync(taskId);

    if (task?.Status != TaskStatus.Completed)
        return BadRequest(new { status = task?.Status });

    var stream = await _storage.OpenReadAsync(task.OutputFilePath!);
    return File(stream, "application/pdf", "result.pdf");
}
```

---

## 10. Этап 8 — Новые операции: Rotate, ExtractPages

> Коммит: `876fb7d Add Rotate pdf docs and Extract pages from PDF docs`

Добавляем операции, не меняя интерфейсы. Только новые методы в `PdfProcessor`:

**Rotate** — поворот всех страниц:

```csharp
private async Task<string> RotatePdfAsync(PdfTask task, ...)
{
    var angle = Convert.ToInt32(task.Options?["angle"] ?? 90);
    // ...
    for (int i = 0; i < document.PageCount; i++)
        document.Pages[i].Rotate = (document.Pages[i].Rotate + angle) % 360;
}
```

**ExtractPages** — извлечение по номерам ("1,3-5,10"):

```csharp
private List<int> ParsePageNumbers(string pagesString)
{
    // "1,3-5,10" → [1, 3, 4, 5, 10]
}
```

**Контроллер** получает новые эндпоинты, но `CreatePdfTaskAsync` общий:

```csharp
[HttpPost("rotate")]
public async Task<ActionResult<PdfTaskResponse>> Rotate(
    IFormFile file, [FromQuery] int angle = 90, CancellationToken ct = default)
```

---

## 11. Этап 9 — HtmlToPdf через PuppeteerSharp

> Коммит: `2a144c8 Add converter HTML to PDF with PuppeteerSharp (chromium)`

**Зачем отдельная библиотека:**
PdfSharp не умеет рендерить HTML (CSS, шрифты, JS).
PuppeteerSharp запускает headless Chromium — полноценный браузер.

```csharp
private async Task<string> HtmlToPdfAsync(PdfTask task, ...)
{
    await EnsureBrowserDownloadedAsync();  // lazy download Chromium

    await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
    {
        Headless = true,
        Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }  // Docker
    });

    await using var page = await browser.NewPageAsync();
    await page.SetContentAsync(htmlContent);

    var pdfBytes = await page.PdfDataAsync(new PdfOptions
    {
        Format = PaperFormat.A4,
        PrintBackground = true,
        MarginOptions = new MarginOptions
        {
            Top = "20mm", Bottom = "20mm",
            Left = "15mm", Right = "15mm"
        }
    });

    // Сохраняем через IFileStorage
    await using var outputStream = new MemoryStream(pdfBytes);
    return await _storage.SaveAsync(outputStream, "converted.pdf", ct);
}
```

**Ключевой момент — ленивая загрузка браузера:**

```csharp
private static readonly SemaphoreSlim _browserDownloadLock = new(1, 1);

private async Task EnsureBrowserDownloadedAsync()
{
    if (_browserDownloaded) return;

    await _browserDownloadLock.WaitAsync();
    try
    {
        if (_browserDownloaded) return;  // double-check locking
        await new BrowserFetcher().DownloadAsync();
        _browserDownloaded = true;
    }
    finally { _browserDownloadLock.Release(); }
}
```

---

## 12. Этап 10 — Compress через Ghostscript

> Коммит: `3ef3c06 Add compress PDF files with GhostScript`

Ghostscript — внешний CLI-процесс. Нужен физический путь к файлу.

```csharp
private async Task<string> CompressWithGhostscriptAsync(PdfTask task, ...)
{
    // Ghostscript требует путь на диске → нужен LocalFileStorage
    if (_storage is not LocalFileStorage localStorage)
        throw new PdfProcessingException("Only supported on local storage", ...);

    var inputPath = localStorage.GetFullPath(inputFileName);
    var outputPath = localStorage.GetFullPath(outputName);

    var startInfo = new ProcessStartInfo { FileName = gsPath };
    startInfo.ArgumentList.Add("-sDEVICE=pdfwrite");
    startInfo.ArgumentList.Add($"-dPDFSETTINGS={pdfsettings}");
    startInfo.ArgumentList.Add($"-sOutputFile={outputPath}");
    startInfo.ArgumentList.Add(inputPath);

    using var process = new Process { StartInfo = startInfo };
    process.Start();

    // Читаем stdout/stderr ДО WaitForExit — иначе дедлок!
    var stdErr = process.StandardError.ReadToEndAsync(ct);
    await process.WaitForExitAsync(ct);
}
```

**Защита от инъекций:**
- `ArgumentList.Add()` — экранирует аргументы автоматически
- `ResolvePdfSettings()` — whitelist допустимых значений `/screen`, `/ebook`, `/printer`

---

## 13. Этап 11 — Рефакторинг: убираем протекающую абстракцию

> Коммит: `7ce3d24 refactor: remove leaky abstraction from IFileStorage`

**Проблема:** Метод `GetFullPath` был в интерфейсе `IFileStorage`.
Это "протекающая абстракция" — Application знает, что файлы на диске.
При переходе на S3 метод теряет смысл.

**Решение:**
1. Удалили `GetFullPath` из `IFileStorage` (интерфейс Application)
2. Оставили метод в `LocalFileStorage` (реализация Infrastructure)
3. `PdfProcessor` делает type cast: `if (_storage is LocalFileStorage ls)`

---

## 14. Этап 12 — Стабилизация MVP

> Коммит: `97e30db feat: add storage cleanup, parallel workers, timeouts, and upload validation`

Четыре доработки для production-ready MVP:

### 1. StorageCleanupWorker

Новый `BackgroundService` — раз в 30 минут удаляет файлы старше `FileLifetimeHours`:

```csharp
private async Task CleanupAsync(CancellationToken ct)
{
    var threshold = DateTime.UtcNow.AddHours(-_options.FileLifetimeHours);

    await foreach (var filePath in _storage.GetAllFilesAsync(ct))
    {
        var fileInfo = await _storage.GetFileInfoAsync(filePath);
        if (fileInfo?.CreationTimeUtc < threshold)
            await _storage.DeleteAsync(filePath);
    }

    // Помечаем задачи как Expired
    var oldTasks = await _taskStore.GetOlderThenAsync(threshold, ct);
    foreach (var task in oldTasks.Where(t => t.Status is Completed or Failed))
    {
        task.Status = TaskStatus.Expired;
        await _taskStore.UpdateAsync(task, ct);
    }
}
```

### 2. Параллельные воркеры

```csharp
// ServiceCollectionExtensions.cs
for (int i = 0; i < workerCount; i++)
{
    var workerId = i;
    services.AddSingleton<IHostedService>(sp =>
        new PdfProcessingWorker(..., workerId));
}
```

`Channel<T>` с `SingleReader = false` гарантирует: каждую задачу берёт
ровно один воркер — атомарная операция.

### 3. Тайм-ауты для внешних процессов

```csharp
using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ExternalProcessTimeoutSeconds));
```

Linked token отменяется при тайм-ауте ИЛИ при shutdown. Различаем:
```csharp
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    throw new PdfProcessingException("Timed out after 180s", ...);
}
```

### 4. Валидация суммарного размера

```csharp
long totalSize = files.Sum(f => f.Length);
if (totalSize > maxTotalSizeBytes)
    return BadRequest(new { error = $"Total {totalSize / 1024 / 1024}MB exceeds {limit}MB" });
```

---

## 15. Этап 13 — Docker и Gotenberg

> Коммиты: `08c4c06`, `c846726`, `e468902`

### Зачем Gotenberg

Вместо локального Ghostscript и PuppeteerSharp — отдельный контейнер,
внутри которого LibreOffice + Chromium + Ghostscript с REST API.

| До (локально) | После (Gotenberg) |
|---|---|
| Cold start LibreOffice на каждый файл | Пул прогретых инстансов |
| Зомби-процессы при ошибках | Контейнер сам перезапускает |
| PuppeteerSharp скачивает 300MB Chromium | Chromium внутри контейнера |

### docker-compose.yml

```yaml
services:
  pdf-api:
    build: .
    ports: ["5212:8080"]         # Единственный порт наружу
    environment:
      - Gotenberg__BaseUrl=http://gotenberg:3000
      - Redis__ConnectionString=redis:6379
    depends_on:
      gotenberg: { condition: service_started }
      redis: { condition: service_healthy }

  gotenberg:
    image: gotenberg/gotenberg:8
    # НЕТ ports: → не доступен снаружи!
    deploy:
      resources:
        limits: { cpus: "2.0", memory: 2G }

  redis:
    image: redis:7-alpine
    command: redis-server --appendonly yes --maxmemory 256mb --maxmemory-policy noeviction
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
```

**Безопасность:** Gotenberg и Redis не имеют `ports:` → доступны только
внутри Docker-сети `pdf-network`. Снаружи доступен только `pdf-api:5212`.

### GotenbergPdfProcessor

Новая реализация `IPdfProcessor` — делегирует тяжёлые операции Gotenberg,
лёгкие (Merge, Split, Rotate) оставляет локально через PdfSharp:

```csharp
public class GotenbergPdfProcessor : IPdfProcessor
{
    private readonly HttpClient _httpClient;  // Typed HttpClient через DI

    // HTML → PDF через Gotenberg Chromium
    private async Task<string> HtmlToPdfViaGotenbergAsync(PdfTask task, ...)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(htmlBytes), "files", "index.html");
        form.Add(new StringContent("8.27"), "paperWidth");   // A4

        var pdfBytes = await SendToGotenbergAsync("/forms/chromium/convert/html", form, ct);
        // ...
    }

    // Office → PDF через Gotenberg LibreOffice (НОВАЯ операция!)
    private async Task<string> OfficeToPdfViaGotenbergAsync(PdfTask task, ...)
    {
        await using var inputStream = await _storage.OpenReadAsync(inputPath);
        form.Add(new StreamContent(inputStream), "files", originalName);

        var pdfBytes = await SendToGotenbergAsync("/forms/libreoffice/convert", form, ct);
        // ...
    }
}
```

### Регистрация с typed HttpClient

```csharp
if (!string.IsNullOrEmpty(gotenbergUrl))
{
    services.AddHttpClient<IPdfProcessor, GotenbergPdfProcessor>(client =>
    {
        client.BaseAddress = new Uri(gotenbergUrl);
        client.Timeout = TimeSpan.FromSeconds(180);
    });
}
else
{
    // Gotenberg не настроен → fallback на локальный
    services.AddSingleton<IPdfProcessor, PdfProcessor>();
}
```

**Почему `AddHttpClient`, а не `new HttpClient()`:**
- `IHttpClientFactory` управляет пулом соединений (избегаем Socket Exhaustion)
- Автоматический retry, circuit breaker через Polly
- Typed client привязан к конкретному классу

---

## 16. Этап 14 — Redis: distributed task queue

> Коммиты: `dd356cd`, `5ce1ad3`, `3891d34`, `856dc34`, `8ab1af1`

### Зачем Redis

| InMemory | Redis |
|---|---|
| Данные теряются при рестарте | AOF-персистентность |
| Один инстанс API | N инстансов за балансировщиком |
| Очередь в памяти одного процесса | Общая очередь |
| `Channel<T>` | `LPOP` — атомарно между процессами |

### Структуры данных в Redis

```
task:{id}              → String (JSON)      — полное состояние задачи
task:queue             → List               — FIFO очередь ID задач
task:status:{status}   → Set                — индекс для GetByStatusAsync
task:created           → Sorted Set (Ticks) — индекс для GetOlderThenAsync
```

**Почему List, а не Streams:**
- List + LPOP — проще и достаточно для 15 человек
- Streams дают consumer groups и ACK, но добавляют сложность
- Для MVP — List; если нужна at-least-once гарантия — Streams

**Почему JSON, а не Redis Hash:**
- `PdfTask` имеет вложенные коллекции (`InputFilePaths`, `Options`)
- JSON проще сериализовать целиком
- При 15 пользователях экономия на маршалинге несущественна

### RedisTaskStore

```csharp
public class RedisTaskStore : ITaskStore
{
    // AddTask — транзакция из 4 операций
    public async Task<PdfTask> AddTask(PdfTaskRequest request, CancellationToken ct)
    {
        var tran = db.CreateTransaction();

        _ = tran.StringSetAsync(taskKey, json);                              // Сохраняем
        _ = tran.SetAddAsync(StatusPrefix + status, id);                     // Индекс статуса
        _ = tran.SortedSetAddAsync(CreatedSortedSetKey, id, ticks);          // Индекс времени
        _ = tran.ListRightPushAsync(QueueKey, id);                           // В очередь

        await tran.ExecuteAsync();  // MULTI/EXEC — атомарно
    }

    // DequeueAsync — LPOP + polling
    public async Task<PdfTask?> DequeueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await db.ListLeftPopAsync(QueueKey);  // Атомарно!

            if (result.IsNull)
            {
                await Task.Delay(1000, ct);  // Polling 1 раз/сек
                continue;
            }

            var task = await GetAsync(Guid.Parse(result!), ct);
            if (task?.Status == TaskStatus.Pending) return task;
        }
        return null;
    }
}
```

### Регистрация с fallback

```csharp
var redisConnection = configuration.GetSection("Redis").GetValue<string>("ConnectionString");

if (!string.IsNullOrEmpty(redisConnection))
{
    services.AddSingleton<IConnectionMultiplexer>(sp =>
    {
        var options = ConfigurationOptions.Parse(redisConnection);
        options.AbortOnConnectFail = false;  // Не падаем при старте
        options.ConnectRetry = 3;
        return ConnectionMultiplexer.Connect(options);
    });

    services.AddSingleton<ITaskStore, RedisTaskStore>();
}
else
{
    services.AddSingleton<ITaskStore, InMemoryTaskStore>();
}
```

---

## 17. Архитектурные решения и trade-offs

### Что хорошо

| Решение | Почему |
|---|---|
| Clean Architecture | Замена InMemory → Redis = 1 новый файл + 1 строка DI |
| Channel<T> для очереди | async/await, bounded, не жрёт CPU |
| Typed HttpClient | Пул соединений, нет Socket Exhaustion |
| Gotenberg в отдельном контейнере | Изоляция, масштабирование, стабильность |
| AOF в Redis | Данные переживают перезапуск |
| `noeviction` policy | Redis не удалит задачи — лучше ошибка, чем потеря |

### Осознанный технический долг

| Долг | Почему не сейчас | Когда чинить |
|---|---|---|
| `FileStorageOptions.BasePath` в Application | KISS — одна секция настроек | При переходе на S3 |
| JSON вместо Redis Hash | Проще, нет вложенных полей | При >1000 задач/мин |
| Polling 1 сек вместо BLPOP | StackExchange.Redis не поддерживает CancellationToken в BLPOP | При переходе на Redis Streams |
| Split возвращает первый файл, а не ZIP | MVP | Следующий спринт |
| Нет retry с backoff для упавших задач | MVP | Когда появится dead letter queue |

### Порядок разработки (итого)

```
1. dotnet new sln                          ← скелет
2. PdfTask.cs, enums                       ← словарь
3. IPdfProcessor, ITaskStore, IFileStorage ← контракты
4. PdfProcessingWorker                     ← бизнес-сценарий
5. LocalFileStorage                        ← файлы на диск
6. InMemoryTaskStore                       ← очередь в памяти
7. PdfProcessor (Merge)                    ← первая операция
8. PdfController + DI                      ← API, связываем всё
9. Rotate, ExtractPages                    ← расширяем
10. HtmlToPdf (PuppeteerSharp)             ← тяжёлая артиллерия
11. Compress (Ghostscript)                 ← внешний процесс
12. Рефакторинг GetFullPath                ← чистим абстракции
13. Стабилизация (cleanup, workers, timeouts) ← production-ready
14. Docker + Gotenberg                     ← микросервисы
15. Redis                                  ← distributed
```
