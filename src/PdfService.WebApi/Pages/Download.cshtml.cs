using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PdfService.Application.Interfaces;

namespace PdfService.WebApi.Pages;

public class DownloadModel : PageModel
{
    private readonly ITaskStore _taskStore;
    private readonly IFileStorage _fileStorage;

    public DownloadModel(ITaskStore taskStore, IFileStorage fileStorage)
    {
        _taskStore = taskStore;
        _fileStorage = fileStorage;
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var task = await _taskStore.GetAsync(id);

        if (task == null)
            return NotFound();

        if (task.Status != Application.Models.TaskStatus.Completed
            || string.IsNullOrEmpty(task.OutputFilePath))
        {
            return RedirectToPage("TaskStatus", new { id });
        }

        if (!await _fileStorage.ExistsAsync(task.OutputFilePath))
            return NotFound("File has expired or been deleted.");

        var stream = await _fileStorage.OpenReadAsync(task.OutputFilePath);
        var fileName = $"{task.Operation}_{task.Id:N}.pdf";

        return File(stream, "application/pdf", fileName);
    }
}
