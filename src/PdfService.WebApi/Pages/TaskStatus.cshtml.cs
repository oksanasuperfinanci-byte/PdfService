using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PdfService.Application.Interfaces;
using PdfService.Application.Models;

namespace PdfService.WebApi.Pages;

public class TaskStatusModel : PageModel
{
    private readonly ITaskStore _taskStore;
    private readonly IFileStorage _fileStorage;

    public TaskStatusModel(ITaskStore taskStore, IFileStorage fileStorage)
    {
        _taskStore = taskStore;
        _fileStorage = fileStorage;
    }

    public PdfTask? PdfTask { get; set; }
    public bool FileExists { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        PdfTask = await _taskStore.GetAsync(id);

        if (PdfTask == null)
            return NotFound();

        if (PdfTask.Status == Application.Models.TaskStatus.Completed
            && !string.IsNullOrEmpty(PdfTask.OutputFilePath))
        {
            FileExists = await _fileStorage.ExistsAsync(PdfTask.OutputFilePath);
        }

        return Page();
    }
}
