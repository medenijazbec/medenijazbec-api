using honey_badger_api.Data;
using honey_badger_api.Entities;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Services
{
    public sealed class TelemetryRollupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TelemetryRollupService> _log;

        public TelemetryRollupService(IServiceScopeFactory scopeFactory, ILogger<TelemetryRollupService> log)
        {
            _scopeFactory = scopeFactory;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // run every 5 minutes
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    await RollTopIps(db);
                    await SnapshotMinute(db);
                    await Cleanup(db);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "TelemetryRollup error");
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        private static async Task RollTopIps(AppDbContext db)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var days = new[] { today, today.AddDays(-1) };

            foreach (var day in days)
            {
                var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var to = day.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

                var Q = await db.RequestLogs
                    .Where(r => r.StartedUtc >= from && r.StartedUtc <= to)
                    .GroupBy(r => r.Ip)
                    .Select(g => new { Ip = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .Take(100)
                    .ToListAsync();

                // wipe & rewrite for that day (idempotent)
                var existing = db.DailyTopIps.Where(x => x.Day == day);
                db.DailyTopIps.RemoveRange(existing);

                int rank = 1;
                foreach (var row in Q)
                {
                    db.DailyTopIps.Add(new DailyTopIp
                    {
                        Day = day,
                        Ip = row.Ip,
                        Count = row.Count,
                        Rank = rank++
                    });
                }
            }
            await db.SaveChangesAsync();
        }

        private static async Task SnapshotMinute(AppDbContext db)
        {
            var end = DateTime.UtcNow;
            var start = end.AddMinutes(-5);

            var logs = await db.RequestLogs
                .Where(r => r.StartedUtc >= start && r.StartedUtc < end)
                .ToListAsync();

            if (logs.Count == 0) return;

            var ms = logs.Select(l => l.DurationMs).OrderBy(x => x).ToArray();
            double P(double p) => ms.Length == 0 ? 0 : ms[(int)Math.Clamp(Math.Round((p / 100.0) * (ms.Length - 1)), 0, ms.Length - 1)];
            var statusCounts = logs.GroupBy(l => l.StatusCode).ToDictionary(g => g.Key.ToString(), g => g.Count());
            var uniqueIps = logs.Select(l => l.Ip).Distinct().Count();

            db.MetricSnapshots.Add(new MetricSnapshot
            {
                WindowStartUtc = start,
                WindowEndUtc = end,
                Requests = logs.Count,
                P50Ms = P(50),
                P95Ms = P(95),
                P99Ms = P(99),
                UniqueIps = uniqueIps,
                Errors4xx = logs.Count(l => l.StatusCode >= 400 && l.StatusCode < 500),
                Errors5xx = logs.Count(l => l.StatusCode >= 500),
                StatusCountsJson = System.Text.Json.JsonSerializer.Serialize(statusCounts)
            });

            await db.SaveChangesAsync();
        }

        private static async Task Cleanup(AppDbContext db)
        {
            var cutoff = DateTime.UtcNow.AddDays(-45);
            var old = db.RequestLogs.Where(r => r.StartedUtc < cutoff);
            db.RequestLogs.RemoveRange(old);
            await db.SaveChangesAsync();
        }
    }
}
