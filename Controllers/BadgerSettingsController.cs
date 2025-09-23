// path: honey_badger_api/Controllers/BadgerSettingsController.cs
using honey_badger_api.Data;
using honey_badger_api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Controllers
{
    [ApiController]
    [Route("api/badger-settings")]
    public sealed class BadgerSettingsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ILogger<BadgerSettingsController> _log;

        public BadgerSettingsController(AppDbContext db, ILogger<BadgerSettingsController> log)
        {
            _db = db;
            _log = log;
        }

        private static readonly string CreateTableSql = @"
CREATE TABLE IF NOT EXISTS `BadgerSettings` (
  `Id` INT NOT NULL,
  `OffsetY` INT NOT NULL,
  `SaucerOffsetY` INT NOT NULL,
  `LightYaw` INT NOT NULL,
  `LightHeight` INT NOT NULL,
  `LightDist` INT NOT NULL,
  `ModelZoom` DOUBLE NOT NULL,
  `SaucerZoom` DOUBLE NOT NULL,
  `CameraZoom` DOUBLE NOT NULL,
  `UpdatedUtc` DATETIME(6) NOT NULL,
  PRIMARY KEY (`Id`)
)";

        private async Task EnsureTableAndRowAsync()
        {
            await _db.Database.ExecuteSqlRawAsync(CreateTableSql);

            var exists = await _db.Set<BadgerSettings>().AsNoTracking().AnyAsync();
            if (!exists)
            {
                _db.Add(new BadgerSettings
                {
                    Id = 1,
                    OffsetY = 0,
                    SaucerOffsetY = 0,
                    LightYaw = 0,
                    LightHeight = 120,
                    LightDist = 200,
                    ModelZoom = 1.0,
                    SaucerZoom = 1.0,
                    CameraZoom = 1.0,
                    UpdatedUtc = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();
            }
        }

        // Everyone can read the current (single-row) settings
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Get()
        {
            await EnsureTableAndRowAsync();
            var s = await _db.Set<BadgerSettings>().AsNoTracking().FirstAsync(x => x.Id == 1);
            return Ok(new
            {
                offsetY = s.OffsetY,
                saucerOffsetY = s.SaucerOffsetY,
                lightYaw = s.LightYaw,
                lightHeight = s.LightHeight,
                lightDist = s.LightDist,
                modelZoom = s.ModelZoom,
                saucerZoom = s.SaucerZoom,
                cameraZoom = s.CameraZoom,
                updatedUtc = s.UpdatedUtc
            });
        }

        public sealed class SaveRequest
        {
            public int OffsetY { get; set; }
            public int SaucerOffsetY { get; set; }
            public int LightYaw { get; set; }
            public int LightHeight { get; set; }
            public int LightDist { get; set; }
            public double ModelZoom { get; set; } = 1.0;
            public double SaucerZoom { get; set; } = 1.0;
            public double CameraZoom { get; set; } = 1.0;
        }

        // Admin must save manually
        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Save([FromBody] SaveRequest req)
        {
            await EnsureTableAndRowAsync();
            var s = await _db.Set<BadgerSettings>().FirstAsync(x => x.Id == 1);

            s.OffsetY = req.OffsetY;
            s.SaucerOffsetY = req.SaucerOffsetY;
            s.LightYaw = req.LightYaw;
            s.LightHeight = req.LightHeight;
            s.LightDist = req.LightDist;
            s.ModelZoom = Math.Clamp(req.ModelZoom, 0.4, 3.0);
            s.SaucerZoom = Math.Clamp(req.SaucerZoom, 0.4, 3.0);
            s.CameraZoom = Math.Clamp(req.CameraZoom, 0.5, 3.0);
            s.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return Ok(new { ok = true, settings = s });
        }

        // --- Optional CRUD for profiles (admin) ---

        [HttpGet("all")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ListAll()
        {
            await EnsureTableAndRowAsync();
            var rows = await _db.Set<BadgerSettings>().AsNoTracking().OrderBy(x => x.Id).ToListAsync();
            return Ok(rows);
        }

        [HttpPost("new")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateProfile([FromBody] SaveRequest req)
        {
            await EnsureTableAndRowAsync();
            var nextId = await _db.Set<BadgerSettings>().Select(x => x.Id).DefaultIfEmpty(0).MaxAsync() + 1;
            var s = new BadgerSettings
            {
                Id = nextId,
                OffsetY = req.OffsetY,
                SaucerOffsetY = req.SaucerOffsetY,
                LightYaw = req.LightYaw,
                LightHeight = req.LightHeight,
                LightDist = req.LightDist,
                ModelZoom = Math.Clamp(req.ModelZoom, 0.4, 3.0),
                SaucerZoom = Math.Clamp(req.SaucerZoom, 0.4, 3.0),
                CameraZoom = Math.Clamp(req.CameraZoom, 0.5, 3.0),
                UpdatedUtc = DateTime.UtcNow
            };
            _db.Add(s);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(Get), new { id = s.Id }, s);
        }

        [HttpPut("{id:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateProfile(int id, [FromBody] SaveRequest req)
        {
            await EnsureTableAndRowAsync();
            var s = await _db.Set<BadgerSettings>().FindAsync(id);
            if (s is null) return NotFound();

            s.OffsetY = req.OffsetY;
            s.SaucerOffsetY = req.SaucerOffsetY;
            s.LightYaw = req.LightYaw;
            s.LightHeight = req.LightHeight;
            s.LightDist = req.LightDist;
            s.ModelZoom = Math.Clamp(req.ModelZoom, 0.4, 3.0);
            s.SaucerZoom = Math.Clamp(req.SaucerZoom, 0.4, 3.0);
            s.CameraZoom = Math.Clamp(req.CameraZoom, 0.5, 3.0);
            s.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return NoContent();
        }

        [HttpDelete("{id:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteProfile(int id)
        {
            var s = await _db.Set<BadgerSettings>().FindAsync(id);
            if (s is null) return NotFound();
            if (s.Id == 1) return BadRequest("Cannot delete base settings row (Id=1).");

            _db.Remove(s);
            await _db.SaveChangesAsync();
            return NoContent();
        }

        // Convenience: set only the default zooms (admin)
        public sealed class DefaultZoomRequest
        {
            public double ModelZoom { get; set; } = 1.0;
            public double SaucerZoom { get; set; } = 1.0;
            public double CameraZoom { get; set; } = 1.0;
        }

        [HttpPost("default-zoom")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SetDefaultZoom([FromBody] DefaultZoomRequest req)
        {
            await EnsureTableAndRowAsync();
            var s = await _db.Set<BadgerSettings>().FirstAsync(x => x.Id == 1);

            s.ModelZoom = Math.Clamp(req.ModelZoom, 0.4, 3.0);
            s.SaucerZoom = Math.Clamp(req.SaucerZoom, 0.4, 3.0);
            s.CameraZoom = Math.Clamp(req.CameraZoom, 0.5, 3.0);
            s.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return Ok(new { ok = true, settings = s });
        }
    }
}
