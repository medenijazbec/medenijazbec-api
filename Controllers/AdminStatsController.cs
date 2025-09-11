using honey_badger_api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Controllers
{
    [ApiController]
    [Route("api/admin/stats")]
    [Authorize(Roles = "Admin")]
    public sealed class AdminStatsController : ControllerBase
    {
        private readonly AppDbContext _db;
        public AdminStatsController(AppDbContext db) { _db = db; }

        [HttpGet("overview")]
        public async Task<IActionResult> Overview()
        {
            var utcNow = DateTime.UtcNow;
            var today = DateOnly.FromDateTime(utcNow);
            var fromToday = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            var totalUsers = await _db.Users.CountAsync();
            var req24h = await _db.RequestLogs.CountAsync(r => r.StartedUtc >= utcNow.AddHours(-24));
            var err5xx24h = await _db.RequestLogs.CountAsync(r => r.StartedUtc >= utcNow.AddHours(-24) && r.StatusCode >= 500);
            var uniqueIpsToday = await _db.RequestLogs.Where(r => r.StartedUtc >= fromToday).Select(r => r.Ip).Distinct().CountAsync();
            var topIpToday = await _db.DailyTopIps.Where(d => d.Day == today).OrderBy(d => d.Rank).FirstOrDefaultAsync();

            // last outage / auth health placeholder: compute from 5xx spikes
            var last5xx = await _db.RequestLogs.Where(r => r.StatusCode >= 500).OrderByDescending(r => r.StartedUtc).Select(r => r.StartedUtc).FirstOrDefaultAsync();

            return Ok(new
            {
                totalUsers,
                req24h,
                err5xx24h,
                uniqueIpsToday,
                topIpToday,
                authProviderHealth = new
                {
                    status = "ok",
                    lastOutageUtc = last5xx == default ? (DateTime?)null : last5xx
                }
            });
        }

        [HttpGet("rps")]
        public async Task<IActionResult> Rps([FromQuery] DateTime? fromUtc = null, [FromQuery] DateTime? toUtc = null)
        {
            var to = toUtc ?? DateTime.UtcNow;
            var from = fromUtc ?? to.AddHours(-24);

            var snaps = await _db.MetricSnapshots
                .Where(s => s.WindowEndUtc >= from && s.WindowEndUtc <= to)
                .OrderBy(s => s.WindowEndUtc)
                .Select(s => new {
                    t = s.WindowEndUtc,
                    requests = s.Requests,
                    p50 = s.P50Ms,
                    p95 = s.P95Ms,
                    p99 = s.P99Ms,
                    errors = s.Errors4xx + s.Errors5xx
                })
                .ToListAsync();

            return Ok(snaps);
        }
    }
}
