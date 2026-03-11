using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PdfService.Application.Interfaces;
using PdfService.Application.Models;

namespace PdfService.WebApi.Pages;

public class IndexModel : PageModel
{
    private readonly ITaskStore _taskStore;
    private readonly IFileStorage _fileStorage;

    public IndexModel(ITaskStore taskStore, IFileStorage fileStorage)
    {
        _taskStore = taskStore;
        _fileStorage = fileStorage;
    }

    [BindProperty]
    public PdfOperation Operation { get; set; }

    [BindProperty]
    public List<IFormFile> Files { get; set; } = new();

    [BindProperty]
    public string? HtmlContent { get; set; }

    [BindProperty]
    public string? Pages { get; set; }

    [BindProperty]
    public int Angle { get; set; } = 90;

    public IReadOnlyList<PdfTask> RecentTasks { get; set; } = Array.Empty<PdfTask>();

    public async Task OnGetAsync()
    {
        var pending = await _taskStore.GetByStatusAsync(Application.Models.TaskStatus.Pending);
        var processing = await _taskStore.GetByStatusAsync(Application.Models.TaskStatus.Processing);
        var completed = await _taskStore.GetByStatusAsync(Application.Models.TaskStatus.Completed);
        var failed = await _taskStore.GetByStatusAsync(Application.Models.TaskStatus.Failed);

        RecentTasks = pending
            .Concat(processing)
            .Concat(completed)
            .Concat(failed)
            .OrderByDescending(t => t.CreateAt)
            .Take(20)
            .ToList();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var filePaths = new List<string>();
        Dictionary<string, object>? options = null;

        if (Operation == PdfOperation.HtmlToPdf)
        {
            if (string.IsNullOrWhiteSpace(HtmlContent))
            {
                ModelState.AddModelError(nameof(HtmlContent), "HTML content is required");
                await OnGetAsync();
                return Page();
            }

            var htmlFileName = $"{Guid.NewGuid()}.html";
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(HtmlContent));
            var savedPath = await _fileStorage.SaveAsync(stream, htmlFileName);
            filePaths.Add(savedPath);
        }
        else
        {
            if (Files == null || Files.Count == 0)
            {
                ModelState.AddModelError(nameof(Files), "Please select at least one file");
                await OnGetAsync();
                return Page();
            }

            foreach (var file in Files)
            {
                var fileName = $"{Guid.NewGuid()}_{file.FileName}";
                using var stream = file.OpenReadStream();
                var savedPath = await _fileStorage.SaveAsync(stream, fileName);
                filePaths.Add(savedPath);
            }
        }

        switch (Operation)
        {
            case PdfOperation.ExtractPages:
                if (!string.IsNullOrWhiteSpace(Pages))
                    options = new Dictionary<string, object> { ["pages"] = Pages };
                break;
            case PdfOperation.Rotate:
                options = new Dictionary<string, object> { ["angle"] = Angle };
                break;
            case PdfOperation.Split:
                if (!string.IsNullOrWhiteSpace(Pages))
                    options = new Dictionary<string, object> { ["pages"] = Pages };
                break;
        }

        var request = new PdfTaskRequest
        {
            Operation = Operation,
            InputFilePaths = filePaths,
            Options = options
        };

        var task = await _taskStore.AddTask(request);

        return RedirectToPage("TaskStatus", new { id = task.Id });
    }
}
