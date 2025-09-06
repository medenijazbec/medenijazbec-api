using honey_badger_api.Data;
using honey_badger_api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Controllers
{
    [ApiController]
    [Route("api/fitness")]
    public class FitnessController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly UserManager<AppUser> _um;
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
    }
}
