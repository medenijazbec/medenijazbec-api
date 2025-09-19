using honey_badger_api.Data;
using honey_badger_api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;

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

        [HttpGet("fun-facts")]
      
        public async Task<ActionResult<FunFactsResponse>> GetFunFacts(
        [FromQuery] string userId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] bool includeSynthetic = false,
        [FromQuery] int streakThreshold1 = 8000,
        [FromQuery] int streakThreshold2 = 10000)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return BadRequest("userId is required.");

            // default range: last 365 days (inclusive)
            var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
            var defaultFrom = today.AddDays(-365);
            var f = from ?? defaultFrom;
            var t = to ?? today;

            var q = _db.FitnessDaily.AsNoTracking()
                .Where(x => x.UserId == userId && x.Day >= f && x.Day <= t);

            if (!includeSynthetic)
                q = q.Where(x => (bool)!x.IsSynthetic);

            var rows = await q
                .Select(x => new {
                    x.Day,
                    Steps = x.Steps ?? 0,
                    Km = x.DistanceKm,
                    CaloriesOut = x.CaloriesOut,
                    x.IsSynthetic
                })
                .OrderBy(x => x.Day)
                .ToListAsync();

            var totalDays = (t.ToDateTime(TimeOnly.MinValue) - f.ToDateTime(TimeOnly.MinValue)).Days + 1;
            var withData = rows.Count;

            long totalSteps = rows.Sum(r => (long)r.Steps);
            decimal totalKm = rows.Sum(r => r.Km ?? 0m);
            long? totalCalOut = rows.Any(r => r.CaloriesOut.HasValue)
                ? rows.Sum(r => (long)(r.CaloriesOut ?? 0))
                : null;

            int avgSteps = withData > 0 ? (int)Math.Round(totalSteps / (double)withData) : 0;
            decimal avgKm = withData > 0 ? Math.Round(totalKm / withData, 2) : 0m;

            int daysGte10k = rows.Count(r => r.Steps >= 10000);
            int daysGte15k = rows.Count(r => r.Steps >= 15000);
            int daysKmGte5 = rows.Count(r => (r.Km ?? 0m) >= 5m);
            int daysKmGte10 = rows.Count(r => (r.Km ?? 0m) >= 10m);

            // Top lists
            var topSteps = rows.OrderByDescending(r => r.Steps).ThenBy(r => r.Day).Take(10)
                .Select(r => new TopDayDto(r.Day, r.Steps, r.Km, r.CaloriesOut, r.IsSynthetic)).ToArray();

            var topKm = rows.OrderByDescending(r => r.Km ?? 0m).ThenBy(r => r.Day).Take(10)
                .Select(r => new TopDayDto(r.Day, r.Steps, r.Km, r.CaloriesOut, r.IsSynthetic)).ToArray();

            var topCal = rows.OrderByDescending(r => r.CaloriesOut ?? 0).ThenBy(r => r.Day).Take(10)
                .Select(r => new TopDayDto(r.Day, r.Steps, r.Km, r.CaloriesOut, r.IsSynthetic)).ToArray();

            // Weekday averages (1=Mon..7=Sun)
            var weekdayAvgs = rows
                .GroupBy(r => ((int)r.Day.ToDateTime(TimeOnly.MinValue).DayOfWeek + 6) % 7 + 1)
                .Select(g => new WeekdayAvgDto(
                    Weekday: g.Key,
                    AvgSteps: (int)Math.Round(g.Average(x => (double)x.Steps)),
                    AvgKm: (decimal)Math.Round(g.Average(x => (double)(x.Km ?? 0m)), 2)
                ))
                .OrderBy(w => w.Weekday)
                .ToArray();

            // Best month (by steps / by km)
            var monthGroups = rows
                .GroupBy(r => new { r.Day.Year, r.Day.Month })
                .Select(g => new MonthSumDto(
                    Year: g.Key.Year,
                    Month: g.Key.Month,
                    StepsSum: g.Sum(x => (long)x.Steps),
                    KmSum: g.Sum(x => x.Km ?? 0m),
                    Days: g.Count()
                ))
                .ToList();

            var bestMonthSteps = monthGroups
                .OrderByDescending(m => m.StepsSum)
                .ThenBy(m => m.Year).ThenBy(m => m.Month)
                .FirstOrDefault() ?? new MonthSumDto(0, 0, 0, 0m, 0);

            var bestMonthKm = monthGroups
                .OrderByDescending(m => m.KmSum)
                .ThenBy(m => m.Year).ThenBy(m => m.Month)
                .FirstOrDefault() ?? new MonthSumDto(0, 0, 0, 0m, 0);

            // Longest streaks (>= threshold)
            static StreakDto LongestStreak(List<dynamic> src, int threshold)
            {
                int best = 0, cur = 0;
                DateOnly? bestStart = null, bestEnd = null, curStart = null;

                foreach (var r in src)
                {
                    if (r.Steps >= threshold)
                    {
                        if (cur == 0) curStart = r.Day;
                        cur++;
                        if (cur > best)
                        {
                            best = cur;
                            bestStart = curStart;
                            bestEnd = r.Day;
                        }
                    }
                    else
                    {
                        cur = 0; curStart = null;
                    }
                }
                return new StreakDto(threshold, best, bestStart, bestEnd);
            }

            var bestStreak8k = LongestStreak(rows.Cast<dynamic>().ToList(), streakThreshold1);
            var bestStreak10k = LongestStreak(rows.Cast<dynamic>().ToList(), streakThreshold2);

            // 7-day rolling PR (by steps) – optional but fun
            // (Not returned as separate field to keep payload compact; you can add if you want.)

            var resp = new FunFactsResponse(
                TotalDays: totalDays,
                DaysWithData: withData,
                TotalSteps: totalSteps,
                TotalKm: Math.Round(totalKm, 2),
                TotalCaloriesOut: totalCalOut,
                AvgSteps: avgSteps,
                AvgKm: avgKm,
                DaysStepsGte10k: daysGte10k,
                DaysStepsGte15k: daysGte15k,
                DaysKmGte5: daysKmGte5,
                DaysKmGte10: daysKmGte10,
                BestStreakGte8k: bestStreak8k,
                BestStreakGte10k: bestStreak10k,
                WeekdayAverages: weekdayAvgs,
                BestMonthBySteps: bestMonthSteps,
                BestMonthByKm: bestMonthKm,
                Top10BySteps: topSteps,
                Top10ByKm: topKm,
                Top10ByCaloriesOut: topCal
            );

            return Ok(resp);
        }
        [Authorize(Roles = "Admin")]
        [HttpDelete("daily/all")]
        public async Task<IActionResult> DeleteAllDaily()
        {
            // If you prefer to reset identity/autoincrement too, you can TRUNCATE instead
            // but TRUNCATE may require extra permissions depending on your DB.
            var deleted = await _db.Database.ExecuteSqlRawAsync("DELETE FROM fitnessdaily;");
            return Ok(new { deleted });
        }

    }
}
