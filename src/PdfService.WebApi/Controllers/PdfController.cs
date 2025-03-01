using Microsoft.AspNetCore.Mvc;
using PdfService.Application.Interfaces;
using PdfService.Application.Models;
using TaskStatus = PdfService.Application.Models.TaskStatus;

namespace PdfService.WebApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PdfController : ControllerBase
    {
        private readonly IFileStorage _storage;
        private readonly ITaskStore _taskStore;
        private readonly IPdfProcessor _processor;
        private readonly FileStorageOptions _storageOptions;
        private readonly ILogger<PdfController> _logger;

        public PdfController(
            IFileStorage storage,
            ITaskStore taskStore,
            IPdfProcessor processor,
            Microsoft.Extensions.Options.IOptions<FileStorageOptions> storageOptions,
            ILogger<PdfController> logger)
        {
            _storage = storage;
            _taskStore = taskStore;
            _processor = processor;
            _storageOptions = storageOptions.Value;
            _logger = logger;
        }

        [HttpPost("merge")]
        [RequestSizeLimit(500_000_000)]
        public async Task<ActionResult<PdfTaskResponse>> Merge(List<IFormFile> files, CancellationToken cancellationToken =default)
        {
            if (files == null || files.Count < 2)
            {
                return BadRequest(new { error = "At least 2 files are rewuired for merge" });
            }

            return await CreatePdfTaskAsync(PdfOperation.Merge, files, null, cancellationToken);
        }

        [HttpPost("split")]
        public async Task<ActionResult<PdfTaskResponse>> Split(IFormFile file, CancellationToken cancellationToken =default)
        {
            if (file == null)
            {
                return BadRequest(new { error = "File is required" });
            }

            return await CreatePdfTaskAsync(PdfOperation.Split, new List<IFormFile> { file }, null, cancellationToken);
        }

        [HttpPost("rotate")]
        public async Task<ActionResult<PdfTaskResponse>> Rotate(IFormFile file, [FromQuery] int angle=90, CancellationToken cancellationToken=default)
        {
            if (file == null)
            {
                return BadRequest(new { error = "File is required" });
            }
            if (angle % 90 != 0)
            {
                return BadRequest(new { error = "Angle must be 90, 180, 0, 270" });
            }

            return await CreatePdfTaskAsync(
                PdfOperation.Rotate,
                new List<IFormFile> { file },
                new Dictionary<string, object?> { ["angle"]= angle },
                cancellationToken
                );
        }

        [HttpPost("extract")]
        public async Task<ActionResult<PdfTaskResponse>> ExtractPages(IFormFile file, [FromQuery] string pages = "1", CancellationToken cancellationToken = default)
        {
            if (file == null)
            {
                return BadRequest(new { error = "File is required" });
            }

            return await CreatePdfTaskAsync(
                PdfOperation.ExtractPages,
                new List<IFormFile> { file },
                new Dictionary<string, object?> { ["pages"] = pages },
                cancellationToken);
        }

        [HttpGet("dowload/{taskId:Guid}")]
        public async Task<IActionResult> Download(Guid taskId)
        {
            var task = await _taskStore.GetAsync(taskId);

            if (task == null)
            {
                return NotFound(new {error ="Task not found"});
            }

            if (task.Status != TaskStatus.Completed)
            {
                return BadRequest(new
                {
                    error= "File not found",
                    status = task.Status.ToString(),
                    progress = task.ProgressPercent
                });
            }

            if (string.IsNullOrEmpty(task.OutputFilePath))
            {
                return StatusCode(500, new { error = "Output file path is missing" });
            }

            if (false == await _storage.ExistsAsync(task.OutputFilePath))
            {
                return StatusCode(410, new { error = "File has expired and was deleted" });
            }

            var stream = await _storage.OpenReadAsync(task.OutputFilePath);
            var fileName = task.Operation switch
            {
                PdfOperation.Merge => "merge_document.pdf",
                PdfOperation.Split => "page_1.pdf",
                PdfOperation.Rotate => "rotate_document.pdf",
                _ => "outpit.pdf"
            };

            return File(stream, "application/pdf", fileName);
        }

        #region Private helper methods

        private async Task<ActionResult<PdfTaskResponse>> CreatePdfTaskAsync(
            PdfOperation operation,
            List<IFormFile> files,
            Dictionary<string, object?> options = null,
            CancellationToken cancellationToken = default)
        {
            var maxSizeBytes = _storageOptions.MaxFileSizeMb * 1024L * 1024L;
            foreach (var file in files)
            {
                if (file.Length > maxSizeBytes)
                {
                    return BadRequest(new
                    {
                        error = $"File '{file.FileName}' exceeds maximum size of {_storageOptions.MaxFileSizeMb}MB"
                    });
                }
            }

            var inputPaths = new List<string>();
            try
            {
                foreach (var file in files)
                {
                    await using var stream = file.OpenReadStream();
                    var path = await _storage.SaveAsync(stream, file.FileName, cancellationToken);
                    inputPaths.Add(path);
                }
            }
            catch (Exception ex)
            {
                foreach (var path in inputPaths)
                {
                    await _storage.DeleteAsync(path);
                }

                return StatusCode(500, new { error = "Failed to save uploaded files" });
            }

            var taskRequest = new PdfTaskRequest
            {
                Operation = operation,
                InputFilePaths = inputPaths,
                Options = options
            };

            var task = await _taskStore.AddTask(taskRequest, cancellationToken);

            return Accepted(new PdfTaskResponse
            {
                TaskId = task.Id,
                Status = task.Status,
                Operation = task.Operation,
                ProcessPercent = task.ProgressPercent,
                ErrorMessage = task.ErrorMessage,
                DownloadUrl = task.Status == TaskStatus.Completed
                ? $"/api/pdf/download/{task.Id}"
                : null,
                CreateAt = task.CreateAt,
                UpdateAt = task.UpdateAt
            });
        }
        #endregion
    }
}
