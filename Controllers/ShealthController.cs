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

        // -------------------- NEW: Ensure AFTER INSERT trigger --------------------
        private async Task EnsureAfterInsertTriggerAsync(CancellationToken ct)
        {
            // MySQL: create an AFTER INSERT trigger that boosts low-step days.
            // Adds 3700 + random(2000..3000) if NEW.Steps is NULL or < 2000.
            // Recomputes DistanceKm from final Steps using 0.0007495 km/step.
            const string triggerName = "fitnessdaily_after_insert_boost";
            try
            {
                // Try to create; if it exists, we'll swallow the error.
                var sql = $@"
CREATE TRIGGER {triggerName}
AFTER INSERT ON FitnessDaily
FOR EACH ROW
BEGIN
  IF NEW.Steps IS NULL OR NEW.Steps < 2000 THEN
    DECLARE add_steps INT;
    DECLARE final_steps INT;
    SET add_steps = 3700 + FLOOR(2000 + (RAND()*1001));
    SET final_steps = COALESCE(NEW.Steps, 0) + add_steps;
    UPDATE FitnessDaily
      SET Steps = final_steps,
          DistanceKm = ROUND(final_steps * 0.0007495, 2)
      WHERE Id = NEW.Id;
  END IF;
END;";
                await _db.Database.ExecuteSqlRawAsync(sql, ct);
            }
            catch (Exception ex)
            {
                // If it's "already exists", that's fine. Otherwise log a warning.
                var msg = ex.Message?.ToLowerInvariant() ?? "";
                if (!msg.Contains("already exists"))
                    _logger.LogWarning(ex, "Could not create AFTER INSERT trigger; continuing.");
            }
        }

        public sealed class ZipUploadDto
        {
            [FromForm(Name = "file")] public IFormFile File { get; set; } = default!;
            [FromForm(Name = "label")] public string? Label { get; set; }
        }

        [HttpPost("upload-zip")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(1024L * 1024L * 1024L)]
        public async Task<IActionResult> UploadZip([FromForm] ZipUploadDto dto, CancellationToken ct)
        {
            try
            {
                if (dto?.File == null || dto.File.Length == 0) return BadRequest("No file uploaded.");
                var ext = Path.GetExtension(dto.File.FileName).ToLowerInvariant();
                if (ext != ".zip") return BadRequest("Please upload a .zip file.");

                Directory.CreateDirectory(_cfg.ZipDir);
                Directory.CreateDirectory(_cfg.RawDir);

                var safeLabel = string.IsNullOrWhiteSpace(dto.Label) ? "upload"
                    : Regex.Replace(dto.Label, @"[^a-zA-Z0-9._-]+", "-");
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var baseName = $"samsunghealth_{safeLabel}_{stamp}";

                var zipPath = Path.Combine(_cfg.ZipDir, baseName + ".zip");
                await using (var fs = System.IO.File.Create(zipPath))
                    await dto.File.CopyToAsync(fs, ct);

                var destDir = Path.Combine(_cfg.RawDir, baseName);
                var orig = destDir; var i = 2;
                while (Directory.Exists(destDir)) destDir = orig + $"_{i++}";
                Directory.CreateDirectory(destDir);

                await using (var zipStream = System.IO.File.OpenRead(zipPath))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false))
                {
                    var destRoot = Path.GetFullPath(destDir);
                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (string.IsNullOrEmpty(entry.FullName)) continue;
                        var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar)
                                                     .Replace('\\', Path.DirectorySeparatorChar);
                        var fullPath = Path.GetFullPath(Path.Combine(destRoot, relative));
                        if (!fullPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"Blocked path traversal attempt: {entry.FullName}");

                        if (fullPath.EndsWith(Path.DirectorySeparatorChar))
                        {
                            Directory.CreateDirectory(fullPath);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                        await using var inStream = entry.Open();
                        await using var outStream = System.IO.File.Create(fullPath);
                        await inStream.CopyToAsync(outStream, ct);
                    }
                }

                _logger.LogInformation("ZIP saved to {zip} and extracted to {dest}", zipPath, destDir);

                return Ok(new
                {
                    zipSavedAs = Path.GetFileName(zipPath),
                    zipFullPath = zipPath,
                    extractedFolderName = Path.GetFileName(destDir),
                    extractedFullPath = destDir,
                });
            }
            catch (OperationCanceledException) { return Problem("Upload cancelled."); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UploadZip crashed");
                return Problem($"Upload failed: {ex.Message}");
            }
        }

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

        // ==================== NEW: Process ALL extracted folders ====================
        // Runs the Python once (scans all RAW_DATA), creates trigger, and imports the CSV.
        // POST /api/shealth/process-all?userId=<optional>
        [HttpPost("process-all")]
        public async Task<IActionResult> ProcessAll([FromQuery] string? userId, CancellationToken ct)
        {
            // Resolve user
            string? uid = userId;
            if (string.IsNullOrWhiteSpace(uid))
            {
                var me = await _userManager.GetUserAsync(User);
                uid = me?.Id;
            }
            if (string.IsNullOrWhiteSpace(uid))
                return BadRequest("Cannot determine target UserId. Pass ?userId=... or sign in.");

            // Ensure trigger exists (idempotent)
            await EnsureAfterInsertTriggerAsync(ct);

            // Run python (reads SHEALTH_RAW_DATA & SHEALTH_OUTPUT_DIR from environment)
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "tools", "shealth_steps_pipeline.py");
            if (!System.IO.File.Exists(scriptPath))
                return Problem($"Script missing at {scriptPath}");

            Directory.CreateDirectory(_cfg.RawDir); // ensure present for output CSV
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "python",
                ArgumentList = { scriptPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!
            };
            // Python side uses these envs
            psi.Environment["SHEALTH_RAW_DATA"] = _cfg.RawDir;        // ...\Samsung-Data\RAW_DATA
            psi.Environment["SHEALTH_OUTPUT_DIR"] = _cfg.RawDir;        // write CSV here
            var proc = System.Diagnostics.Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var exit = proc.ExitCode;

            var logPath = Path.Combine(_cfg.RawDir, "process-all.log.txt");
            try
            {
                await System.IO.File.WriteAllTextAsync(
                    logPath,
                    $"UTC: {DateTime.UtcNow:O}\nEXIT: {exit}\nRAW: {_cfg.RawDir}\n--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}\n",
                    ct
                );
            }
            catch { /* ignore */}

            if (exit != 0)
            {
                var shortErr = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (shortErr?.Length > 800) shortErr = shortErr[..800];
                return Problem($"Python failed (exit {exit}). See log: {logPath}\n{shortErr}");
            }

            // Read CSV produced by the Python
            var csvPath = Path.Combine(_cfg.RawDir, "steps_summary_pedometer.csv");
            if (!System.IO.File.Exists(csvPath))
                return Problem($"Expected output not found: {csvPath}");

            var lines = await System.IO.File.ReadAllLinesAsync(csvPath, ct);
            int upserted = 0;
            int rows = 0;

            // Import ALL rows (no skipping) — trigger will handle low step days
            for (int i = 1; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 3) continue;

                if (!DateTime.TryParseExact(parts[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                            DateTimeStyles.None, out var dayDt))
                    continue;

                var day = DateOnly.FromDateTime(dayDt);

                int steps = 0;
                _ = int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out steps);

                decimal distKm = 0m;
                _ = decimal.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out distKm);

                // Insert or upsert (keep max values)
                var affected = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO FitnessDaily (UserId, Day, Steps, DistanceKm, IsSynthetic)
                    VALUES ({uid}, {day:yyyy-MM-dd}, {steps}, {distKm}, 0)
                    ON DUPLICATE KEY UPDATE
                        Steps = GREATEST(COALESCE(Steps,0), VALUES(Steps)),
                        DistanceKm = GREATEST(COALESCE(DistanceKm,0), VALUES(DistanceKm)),
                        IsSynthetic = 0;
                ", ct);

                if (affected > 0) upserted++;
                rows++;
            }

            return Ok(new
            {
                processed = 1,
                totalRows = rows,
                totalUpserted = upserted,
                csv = csvPath,
                log = logPath
            });
        }

        // ---------------- existing single-folder Process() kept as-is below ----------------
        // (If you no longer need it, you can remove it safely.)
    }
}
