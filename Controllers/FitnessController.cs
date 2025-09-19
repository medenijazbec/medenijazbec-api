using honey_badger_api.Data;
using honey_badger_api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace honey_badger_api.Controllers
{
    [ApiController]
    [Route("api/fitness")]
    public class FitnessController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly UserManager<AppUser> _um;

        // for distance fallback when inserting synthetic rows
        private const decimal STEP_TO_KM = 0.0007495m;

        public FitnessController(AppDbContext db, UserManager<AppUser> um) { _db = db; _um = um; }

        // Public read (you can lock this down if you want)
        [HttpGet("daily")]
        public async Task<IActionResult> GetDaily([FromQuery] string userId, [FromQuery] DateOnly from, [FromQuery] DateOnly to)
        {
            var data = await _db.FitnessDaily
                .Where(x => x.UserId == userId && x.Day >= from && x.Day <= to)
                .OrderBy(x => x.Day)
                .ToListAsync();
            return Ok(data);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost("daily/upsert")]
        public async Task<IActionResult> UpsertDaily(FitnessDaily dto)
        {
            var existing = await _db.FitnessDaily
                .FirstOrDefaultAsync(x => x.UserId == dto.UserId && x.Day == dto.Day);
            if (existing is null)
            {
                _db.FitnessDaily.Add(dto);
            }
            else
            {
                existing.CaloriesIn = dto.CaloriesIn;
                existing.CaloriesOut = dto.CaloriesOut;
                existing.Steps = dto.Steps;
                existing.SleepMinutes = dto.SleepMinutes;
                existing.WeightKg = dto.WeightKg;
                existing.Notes = dto.Notes;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
            return Ok(dto);
        }

        [HttpGet("sessions")]
        public async Task<IActionResult> GetSessions([FromQuery] string userId, [FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            var sessions = await _db.ExerciseSessions
                .Where(s => s.UserId == userId && s.StartTime >= from && s.StartTime <= to)
                .OrderBy(s => s.StartTime)
                .ToListAsync();
            return Ok(sessions);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost("sessions")]
        public async Task<IActionResult> CreateSession(ExerciseSession dto)
        {
            _db.ExerciseSessions.Add(dto);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetSessions), new { userId = dto.UserId, from = dto.StartTime, to = dto.StartTime }, dto);
        }

        [Authorize(Roles = "Admin")]
        [HttpPut("sessions/{id:long}")]
        public async Task<IActionResult> UpdateSession(long id, ExerciseSession dto)
        {
            var s = await _db.ExerciseSessions.FindAsync(id);
            if (s is null) return NotFound();
            s.StartTime = dto.StartTime;
            s.EndTime = dto.EndTime;
            s.Type = dto.Type;
            s.CaloriesBurned = dto.CaloriesBurned;
            s.DistanceKm = dto.DistanceKm;
            s.Notes = dto.Notes;
            s.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return NoContent();
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete("sessions/{id:long}")]
        public async Task<IActionResult> DeleteSession(long id)
        {
            var s = await _db.ExerciseSessions.FindAsync(id);
            if (s is null) return NotFound();
            _db.Remove(s);
            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ===================== NEW: Fill missing days =====================
        public sealed class FillMissingRequest
        {
            [Required] public string UserId { get; set; } = default!;
            // Optional range; if omitted, we use the min/max Day from existing rows for that user
            public string? From { get; set; } // "yyyy-MM-dd"
            public string? To { get; set; }   // "yyyy-MM-dd"
        }

        /// <summary>
        /// Inserts synthetic rows for any missing calendar days in the range.
        /// Steps = round(month average of existing days with >=1000 steps) + random(1000..2000).
        /// DistanceKm is derived from steps when missing. Never overwrites existing rows.
        /// Marks inserted rows as IsSynthetic = 1. Requires a UNIQUE(UserId, Day) in DB.
        /// </summary>
        [Authorize(Roles = "Admin")]
        [HttpPost("daily/fill-missing")]
        public async Task<IActionResult> FillMissing([FromBody] FillMissingRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.UserId))
                return BadRequest("UserId is required.");

            DateOnly? from = null, to = null;
            if (DateOnly.TryParseExact(req.From ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var df))
                from = df;
            if (DateOnly.TryParseExact(req.To ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                to = dt;

            // Infer bounds from existing rows if not given
            if (from is null || to is null)
            {
                var bounds = await _db.FitnessDaily.AsNoTracking()
                    .Where(x => x.UserId == req.UserId)
                    .OrderBy(x => x.Day)
                    .Select(x => x.Day)
                    .ToListAsync(ct);

                if (!bounds.Any())
                    return Ok(new { inserted = 0, message = "No existing data to infer range." });

                from ??= bounds.First();
                to ??= bounds.Last();
            }

            // Clamp to yesterday (UTC)
            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
            if (to > yesterday) to = yesterday;
            if (from > yesterday) from = yesterday;
            if (from > to) return BadRequest("Invalid date range.");

            // Month averages from existing rows (>=1000 steps)
            var sample = await _db.FitnessDaily.AsNoTracking()
                .Where(x => x.UserId == req.UserId && x.Day >= from && x.Day <= to)
                .Select(x => new { x.Day, x.Steps })
                .ToListAsync(ct);

            var monthAverages = sample
                .Where(r => (r.Steps ?? 0) >= 1000)
                .GroupBy(r => (r.Day.Year, r.Day.Month))
                .ToDictionary(g => (g.Key.Year, g.Key.Month), g => g.Average(r => (double)(r.Steps ?? 0)));

            // Existing days
            var existingDays = await _db.FitnessDaily.AsNoTracking()
                .Where(x => x.UserId == req.UserId && x.Day >= from && x.Day <= to)
                .Select(x => x.Day)
                .ToListAsync(ct);
            var existing = existingDays.ToHashSet();

            var rng = new Random();
            int inserted = 0;
            const decimal STEP_TO_KM = 0.0007495m;

            for (var d = from.Value; d <= to.Value; d = d.AddDays(1))
            {
                if (existing.Contains(d)) continue;

                var key = (d.Year, d.Month);
                var baseAvg = monthAverages.TryGetValue(key, out var avg) ? avg : 1000.0;
                var steps = (int)Math.Round(baseAvg) + rng.Next(1000, 2001);
                var dist = Math.Round((decimal)steps * STEP_TO_KM, 2);

                var affected = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT IGNORE INTO FitnessDaily (UserId, Day, Steps, DistanceKm, IsSynthetic)
            VALUES ({req.UserId}, {d:yyyy-MM-dd}, {steps}, {dist}, 1);
        ", ct);

                if (affected > 0) inserted++;
            }

            return Ok(new
            {
                inserted,
                from = from.Value.ToString("yyyy-MM-dd"),
                to = to.Value.ToString("yyyy-MM-dd")
            });
        }
    }
}
