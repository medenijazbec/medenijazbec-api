// path: honey_badger_api/Controllers/UploadsController.cs
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace honey_badger_api.Controllers
{
    public class ImageUploadDto
    {
        // IMPORTANT for Swagger: bind from multipart/form-data
        [FromForm(Name = "file")]
        public IFormFile File { get; set; } = default!;
    }

    [ApiController]
    [Route("api/uploads")]
    public class UploadsController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<UploadsController> _log;

        public UploadsController(IWebHostEnvironment env, ILogger<UploadsController> log)
        {
            _env = env;
            _log = log;
        }

        [Authorize(Roles = "Admin")]
        [HttpPost("image")]
        [Consumes("multipart/form-data")] // <-- tells Swagger this is a file upload
        public async Task<IActionResult> Image([FromForm] ImageUploadDto dto)
        {
            var file = dto.File;
            if (file is null || file.Length == 0) return BadRequest("No file");

            var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg" };
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || !allowed.Contains(ext))
                return BadRequest("Unsupported file type");

            var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var imagesDir = Path.Combine(webRoot, "images");
            Directory.CreateDirectory(imagesDir);

            var baseName = Path.GetFileNameWithoutExtension(file.FileName);
            baseName = Regex.Replace(baseName, @"[^a-zA-Z0-9-_]+", "-").Trim('-').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "img";

            var fileName = $"{baseName}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}";
            var fullPath = Path.Combine(imagesDir, fileName);

            await using (var stream = System.IO.File.Create(fullPath))
                await file.CopyToAsync(stream);

            var url = $"/images/{fileName}";
            return Ok(new { url });
        }
    }
}
