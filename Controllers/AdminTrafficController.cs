using honey_badger_api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Controllers
{
    [ApiController]
    [Route("api/admin/traffic")]
    [Authorize(Roles = "Admin")]
    public sealed class AdminTrafficController : ControllerBase
    {
        private readonly AppDbContext _db;
        public AdminTrafficController(AppDbContext db) => _db = db;

        [HttpGet("top-ips")]
        public async Task<IActionResult> TopIps([FromQuery] DateOnly? day = null)
        {
            var d = day ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var list = await _db.DailyTopIps
                .Where(x => x.Day == d)
                .OrderBy(x => x.Rank)
                .Take(100)
                .ToListAsync();
            return Ok(new { day = d, top = list });
        }

        [HttpGet("status-split")]
        public async Task<IActionResult> StatusSplit([FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc)
        {
            var q = await _db.RequestLogs
                .Where(r => r.StartedUtc >= fromUtc && r.StartedUtc <= toUtc)
                .GroupBy(r => r.StatusCode / 100)
                .Select(g => new { bucket = g.Key + "xx", count = g.Count() })
                .ToListAsync();
            return Ok(q);
        }

        [HttpGet("recent")]
        public async Task<IActionResult> Recent([FromQuery] int take = 200)
        {
            var rows = await _db.RequestLogs
                .OrderByDescending(r => r.StartedUtc)
                .Take(Math.Clamp(take, 10, 1000))
                .Select(r => new {
                    r.StartedUtc,
                    r.Method,
                    r.Path,
                    r.StatusCode,
                    r.DurationMs,
                    r.Ip,
                    r.UserAgent,
                    r.UserId
                })
                .ToListAsync();
            return Ok(rows);
        }
    }
}
