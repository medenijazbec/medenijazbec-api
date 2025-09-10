using honey_badger_api.Abstractions;
using honey_badger_api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace honey_badger_api.Controllers
{
    [Authorize(Roles = "Admin")]
    [ApiController]
    [Route("api/shealth")]
    public sealed class ShealthController : ControllerBase
    {
        private readonly ShealthConfig _cfg;
        private readonly ILogger<ShealthController> _logger;
        private readonly AppDbContext _db;
        private readonly UserManager<AppUser> _userManager;
        private readonly IWebHostEnvironment _env;
        public ShealthController(
            ShealthConfig cfg,
            ILogger<ShealthController> logger,
            AppDbContext db,
            UserManager<AppUser> userManager,
            IWebHostEnvironment env)
        {
            _cfg = cfg;
            _logger = logger;
            _db = db;
            _userManager = userManager;
            _env = env;
        }

        public sealed class ZipUploadDto
        {
            // form-data key must be "file"
            [FromForm(Name = "file")] public IFormFile File { get; set; } = default!;
            // optional label for naming
            [FromForm(Name = "label")] public string? Label { get; set; }
        }

        [HttpPost("upload-zip")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(1024L * 1024L * 1024L)] // 1 GB cap; adjust as needed
        public async Task<IActionResult> UploadZip([FromForm] ZipUploadDto dto, CancellationToken ct)
        {
            try
            {
                if (dto?.File == null || dto.File.Length == 0)
                    return BadRequest("No file uploaded.");

                var ext = Path.GetExtension(dto.File.FileName).ToLowerInvariant();
                if (ext != ".zip")
                    return BadRequest("Please upload a .zip file.");

                Directory.CreateDirectory(_cfg.ZipDir);
                Directory.CreateDirectory(_cfg.RawDir);

                // label -> folder-safe
                var safeLabel = string.IsNullOrWhiteSpace(dto.Label) ? "upload"
                    : Regex.Replace(dto.Label, @"[^a-zA-Z0-9._-]+", "-");

                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var baseName = $"samsunghealth_{safeLabel}_{stamp}";

                var zipFileName = baseName + ".zip";
                var zipPath = Path.Combine(_cfg.ZipDir, zipFileName);

                // Save to disk
                await using (var fs = System.IO.File.Create(zipPath))
                    await dto.File.CopyToAsync(fs, ct);

                // Extract to RAW_DATA/baseName
                var destDir = Path.Combine(_cfg.RawDir, baseName);
                var originalDestDir = destDir;
                var i = 2;
                while (Directory.Exists(destDir))
                {
                    destDir = originalDestDir + $"_{i++}";
                }
                Directory.CreateDirectory(destDir);

                // SAFER extraction: stream entries and normalize paths
                // This prevents traversal (../), weird roots, and partially overwriting project files.
                await using (var zipStream = System.IO.File.OpenRead(zipPath))
                using (var archive = new System.IO.Compression.ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false))
                {
                    var destRoot = Path.GetFullPath(destDir);
                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Skip directory entries that are empty names
                        if (string.IsNullOrEmpty(entry.FullName)) continue;

                        // Normalize: replace backslashes with OS separator
                        var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar)
                                                     .Replace('\\', Path.DirectorySeparatorChar);

                        // Prevent Zip-Slip: combined path must stay inside destRoot
                        var fullPath = Path.GetFullPath(Path.Combine(destRoot, relative));
                        if (!fullPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"Blocked path traversal attempt: {entry.FullName}");

                        // If directory
                        if (fullPath.EndsWith(Path.DirectorySeparatorChar))
                        {
                            Directory.CreateDirectory(fullPath);
                            continue;
                        }

                        // Ensure directory exists
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                        // Extract file
                        await using var inStream = entry.Open();
                        await using var outStream = System.IO.File.Create(fullPath);
                        await inStream.CopyToAsync(outStream, ct);
                    }
                }

                _logger.LogInformation("ZIP saved to {zip} and extracted to {dest}", zipPath, destDir);

                return Ok(new
                {
                    zipSavedAs = zipFileName,
                    zipFullPath = zipPath,
                    extractedFolderName = Path.GetFileName(destDir),
                    extractedFullPath = destDir,
                });
            }
            catch (OperationCanceledException)
            {
                return Problem("Upload cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UploadZip crashed");
                return Problem($"Upload failed: {ex.Message}");
            }
        }


        

        // GET /api/shealth/list
        [HttpGet("list")]
        public IActionResult List()
        {
            Directory.CreateDirectory(_cfg.ZipDir);
            Directory.CreateDirectory(_cfg.RawDir);

            var zips = Directory.EnumerateFiles(_cfg.ZipDir, "*.zip")
                .Select(p => new
                {
                    fileName = Path.GetFileName(p),
                    fullPath = p,
                    size = new FileInfo(p).Length,
                    createdUtc = System.IO.File.GetCreationTimeUtc(p)
                })
                .OrderByDescending(z => z.createdUtc)
                .ToArray();

            var folders = Directory.EnumerateDirectories(_cfg.RawDir)
                .Select(d => new
                {
                    folderName = Path.GetFileName(d),
                    fullPath = d,
                    createdUtc = Directory.GetCreationTimeUtc(d)
                })
                .OrderByDescending(x => x.createdUtc)
                .ToArray();

            return Ok(new { zipFiles = zips, extracted = folders });
        }

        // POST /api/shealth/process/{folder}?userId=<optional>
        [HttpPost("process/{folder}")]
        public async Task<IActionResult> Process(string folder, [FromQuery] string? userId, CancellationToken ct)
        {
            var targetDir = Path.Combine(_cfg.RawDir, folder);
            if (!Directory.Exists(targetDir))
                return NotFound($"Folder not found under RAW_DATA: {folder}");

            // Resolve user
            string? uid = userId;
            if (string.IsNullOrWhiteSpace(uid))
            {
                var me = await _userManager.GetUserAsync(User);
                uid = me?.Id;
            }
            if (string.IsNullOrWhiteSpace(uid))
                return BadRequest("Cannot determine target UserId. Pass ?userId=... or sign in.");

            // Run python
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "tools", "shealth_steps_pipeline.py");
            if (!System.IO.File.Exists(scriptPath))
                return Problem($"Script missing at {scriptPath}");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "python",
                ArgumentList = { scriptPath, targetDir },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!
            };
            psi.Environment["SHEALTH_DIR"] = targetDir;

            var proc = System.Diagnostics.Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            await proc.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var exit = proc.ExitCode;

            var logPath = Path.Combine(targetDir, "process.log.txt");
            try
            {
                await System.IO.File.WriteAllTextAsync(
                    logPath,
                    $"UTC: {DateTime.UtcNow:O}\nEXIT: {exit}\n--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}\n",
                    ct
                );
            }
            catch { /* ignore */ }

            if (exit != 0)
            {
                // include a short message for UI
                var shortErr = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (shortErr?.Length > 800) shortErr = shortErr[..800];
                return Problem($"Python failed (exit {exit}). See log: {logPath}\n{shortErr}");
            }

            // Expect output CSV
            var csvPath = Path.Combine(targetDir, "steps_summary_pedometer_fixed.csv");
            if (!System.IO.File.Exists(csvPath))
                return Problem($"Expected output not found: {csvPath}");

            // Parse & insert (INSERT IGNORE keeps existing rows)
            int inserted = 0, skipped = 0;
            var lines = await System.IO.File.ReadAllLinesAsync(csvPath, ct);
            if (lines.Length > 0 && lines[0].StartsWith("date"))
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var line = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line)) { continue; }
                    var parts = line.Split(',');
                    if (parts.Length < 3) { continue; }

                    var dateStr = parts[0].Trim();
                    var stepsStr = parts[1].Trim();
                    var distStr = parts[2].Trim();

                    if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                                DateTimeStyles.None, out var day))
                        continue;
                    if (!int.TryParse(stepsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var steps))
                        steps = 0;
                    if (!decimal.TryParse(distStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var distKm))
                        distKm = 0m;

                    // INSERT IGNORE avoids touching existing (UserId, Day) rows
                    var affected = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                        INSERT IGNORE INTO FitnessDaily (UserId, Day, Steps, DistanceKm)
                        VALUES ({uid}, {day:yyyy-MM-dd}, {steps}, {distKm});
                    ", ct);

                    if (affected > 0) inserted++; else skipped++;
                }
            }

            return Ok(new
            {
                folder,
                csv = csvPath,
                inserted,
                skipped,
                log = logPath
            });
        }
    }
}
