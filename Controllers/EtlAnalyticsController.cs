using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using honey_badger_api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Controllers;

[ApiController]
[Route("api/etl")]
[Authorize(Roles = "Admin")]
public class EtlAnalyticsController : ControllerBase
{
    private readonly NvdaAlphaDbContext _db;
    public EtlAnalyticsController(NvdaAlphaDbContext db) => _db = db;

    // ---------------------------
    // Helpers
    // ---------------------------
    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var rank = (p / 100.0) * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high) return sorted[low];
        var weight = rank - low;
        return sorted[low] * (1 - weight) + sorted[high] * weight;
    }

    private static DateTime FloorToMinute(DateTime utc) =>
        new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);

    // ---------------------------
    // OVERVIEW (existing, extended)
    // ---------------------------
    [HttpGet("overview")]
    public async Task<IActionResult> Overview()
    {
        var now = DateTime.UtcNow;
        var since24 = now.AddDays(-1);

        // sequential to avoid DbContext concurrency
        var companies = await _db.Companies.AsNoTracking().CountAsync();
        var articlesGdelt = await _db.NewsArticles.AsNoTracking().CountAsync();
        var articlesExt = await _db.NewsArticlesExt.AsNoTracking().CountAsync();
        var requestsTotal = await _db.RequestLogs.AsNoTracking().CountAsync();
        var jobs = await _db.KeywordJobs.AsNoTracking().CountAsync();

        // per-source 24h
        var perSource24h = await _db.RequestLogs.AsNoTracking()
            .Where(r => r.AttemptAt >= since24)
            .GroupBy(r => r.Source)
            .Select(g => new
            {
                source = g.Key!,
                count = g.Count(),
                ok = g.Count(x => x.Outcome == "OK"),
                bad = g.Count(x => x.Outcome == "BAD"),
                s429 = g.Count(x => x.StatusCode == 429),
            })
            .OrderByDescending(x => x.count)
            .ToListAsync();

        // ✅ EF-translatable average without DefaultIfEmpty
        var okRate24h = await _db.RequestLogs.AsNoTracking()
            .Where(r => r.AttemptAt >= since24)
            .GroupBy(_ => 1)
            .Select(g => g.Average(r => r.Outcome == "OK" ? 1.0 : 0.0))
            .FirstOrDefaultAsync();

        return Ok(new
        {
            companies,
            articlesGdelt,
            articlesExt,
            requestsTotal,
            jobs,
            okRate24h, // 0..1
            perSource24h
        });
    }

    // ---------------------------
    // COUNTS BY COMPANY (existing)
    // ---------------------------
    [HttpGet("counts")]
    public async Task<IActionResult> CountsByCompany()
    {
        var rows = await _db.ArticleCompanyMap.AsNoTracking()
            .GroupBy(x => x.CompanyId)
            .Select(g => new { CompanyId = g.Key, Total = g.Count() })
            .Join(_db.Companies.AsNoTracking(),
                  g => g.CompanyId,
                  c => c.Id,
                  (g, c) => new { symbol = c.Symbol, total = g.Total })
            .OrderByDescending(x => x.total)
            .ToListAsync();

        return Ok(rows);
    }

    // =======================================================================
    // KEYS / CAPACITY
    // =======================================================================

    // Inventory by source (total, active, free, blocked, inactive)
    [HttpGet("keys/inventory")]
    public async Task<IActionResult> KeyInventory()
    {
        var now = DateTime.UtcNow;

        // Note: "free" = active AND (exhausted_until is null OR <= now)
        var rows = await _db.ApiAccounts.AsNoTracking()
            .GroupBy(a => a.Source)
            .Select(g => new
            {
                source = g.Key!,
                total = g.Count(),
                active = g.Count(x => x.IsActive),
                free = g.Count(x => x.IsActive && (x.ExhaustedUntil == null || x.ExhaustedUntil <= now)),
                blocked = g.Count(x => x.IsActive && x.ExhaustedUntil != null && x.ExhaustedUntil > now),
                inactive = g.Count(x => !x.IsActive)
            })
            .OrderBy(x => x.source)
            .ToListAsync();

        return Ok(rows);
    }

    // Next key to pick per source (LRU then lowest usage_count)
    [HttpGet("keys/next")]
    public async Task<IActionResult> NextKeyBySource()
    {
        var now = DateTime.UtcNow;
        var sources = await _db.ApiAccounts.AsNoTracking()
            .Select(a => a.Source).Distinct().OrderBy(s => s).ToListAsync();

        var results = new List<object>();
        foreach (var s in sources)
        {
            var key = await _db.ApiAccounts.AsNoTracking()
                .Where(a => a.Source == s && a.IsActive &&
                           (a.ExhaustedUntil == null || a.ExhaustedUntil <= now))
                .OrderBy(a => a.LastUsedAt == null ? 0 : 1) // nulls first (never used)
                .ThenBy(a => a.LastUsedAt)
                .ThenBy(a => a.UsageCount)
                .ThenBy(a => a.Id)
                .Select(a => new { a.Id, a.AccountLabel, a.LastUsedAt, a.UsageCount })
                .FirstOrDefaultAsync();

            results.Add(new { source = s, next = key });
        }

        return Ok(results);
    }

    // Currently blocked keys (and minutes remaining)
    [HttpGet("keys/blocked")]
    public async Task<IActionResult> BlockedKeys()
    {
        var now = DateTime.UtcNow;
        var rows = await _db.ApiAccounts.AsNoTracking()
            .Where(a => a.IsActive && a.ExhaustedUntil != null && a.ExhaustedUntil > now)
            .OrderBy(a => a.ExhaustedUntil)
            .Select(a => new
            {
                a.Id,
                a.Source,
                a.AccountLabel,
                exhaustedUntil = a.ExhaustedUntil!,
                minutesRemaining = EF.Functions.DateDiffMinute(now, a.ExhaustedUntil!.Value)
            })
            .ToListAsync();

        return Ok(rows);
    }

    // Stale active keys (unused since X hours or never)
    [HttpGet("keys/stale")]
    public async Task<IActionResult> StaleKeys([FromQuery] int hours = 24)
    {
        var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, hours));
        var rows = await _db.ApiAccounts.AsNoTracking()
            .Where(a => a.IsActive && (a.LastUsedAt == null || a.LastUsedAt < cutoff))
            .OrderBy(a => a.LastUsedAt)
            .Select(a => new { a.Id, a.Source, a.AccountLabel, a.LastUsedAt, a.UsageCount })
            .ToListAsync();

        return Ok(rows);
    }

    // Fairness per source (share of busiest key in last 24h).
    [HttpGet("keys/fairness")]
    public async Task<IActionResult> Fairness([FromQuery] int hours = 24)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Max(1, hours));

        // Count requests by (source, account_id)
        var counts = await _db.RequestLogs.AsNoTracking()
            .Where(r => r.AttemptAt >= since && r.AccountId != null)
            .GroupBy(r => new { r.Source, r.AccountId })
            .Select(g => new { g.Key.Source, AccountId = g.Key.AccountId!.Value, C = g.Count() })
            .ToListAsync();

        var result = counts
            .GroupBy(x => x.Source)
            .Select(g =>
            {
                var total = g.Sum(x => x.C);
                var max = g.Max(x => x.C);
                var fairness = total > 0 ? (double)max / total : 0.0;
                return new
                {
                    source = g.Key,
                    total,
                    busiestAccountId = g.OrderByDescending(x => x.C).First().AccountId,
                    busiestShare = fairness, // 0..1
                    busiestPct = Math.Round(fairness * 100, 2)
                };
            })
            .OrderByDescending(x => x.busiestPct)
            .ToList();

        // resolve labels for convenience (one-by-one to keep single context)
        var withLabels = new List<object>();
        foreach (var r in result)
        {
            var label = await _db.ApiAccounts.AsNoTracking()
                .Where(a => a.Id == r.busiestAccountId)
                .Select(a => a.AccountLabel)
                .FirstOrDefaultAsync();
            withLabels.Add(new
            {
                r.source,
                r.total,
                r.busiestAccountId,
                busiestLabel = label,
                r.busiestShare,
                r.busiestPct
            });
        }

        return Ok(withLabels);
    }

    // Per-key throughput (last N hours)
    [HttpGet("keys/usage")]
    public async Task<IActionResult> KeyUsage([FromQuery] int hours = 24)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Max(1, hours));
        var counts = await _db.RequestLogs.AsNoTracking()
            .Where(r => r.AttemptAt >= since && r.AccountId != null)
            .GroupBy(r => new { r.Source, r.AccountId })
            .Select(g => new { g.Key.Source, AccountId = g.Key.AccountId!.Value, C = g.Count() })
            .OrderByDescending(x => x.C)
            .ToListAsync();

        // hydrate labels
        var result = new List<object>();
        foreach (var c in counts)
        {
            var label = await _db.ApiAccounts.AsNoTracking()
                .Where(a => a.Id == c.AccountId)
                .Select(a => a.AccountLabel)
                .FirstOrDefaultAsync();
            result.Add(new { c.Source, c.AccountId, accountLabel = label, c.C });
        }

        return Ok(result);
    }

    // =======================================================================
    // TRAFFIC / QUALITY
    // =======================================================================

    // Requests per minute (last N minutes), grouped by source
    [HttpGet("traffic/rpm")]
    public async Task<IActionResult> RequestsPerMinute([FromQuery] int minutes = 120)
    {
        var span = Math.Clamp(minutes, 5, 360); // guard
        var since = DateTime.UtcNow.AddMinutes(-span);

        // Pull minimal fields then aggregate in-memory to avoid provider-specific translations.
        var raw = await _db.RequestLogs.AsNoTracking()
            .Where(r => r.AttemptAt >= since)
            .Select(r => new { r.Source, r.AttemptAt })
            .ToListAsync();

        var grouped = raw
            .GroupBy(r => new { r.Source, Minute = FloorToMinute(r.AttemptAt) })
            .Select(g => new { g.Key.Source, minute = g.Key.Minute, count = g.Count() })
            .OrderBy(x => x.Source).ThenBy(x => x.minute)
            .ToList();

        return Ok(grouped);
    }

    // Success rate + error mix (window)
    [HttpGet("traffic/success")]
    public async Task<IActionResult> Success([FromQuery] int hours = 24)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Max(1, hours));

        var bySource = await _db.RequestLogs.AsNoTracking()
            .Where(r => r.AttemptAt >= since)
            .GroupBy(r => r.Source)
            .Select(g => new
            {
                source = g.Key!,
                total = g.Count(),
                ok = g.Count(x => x.Outcome == "OK"),
                bad = g.Count(x => x.Outcome == "BAD"),
                s429 = g.Count(x => x.StatusCode == 429),
                e4xx = g.Count(x => x.StatusCode >= 400 && x.StatusCode < 500),
                e5xx = g.Count(x => x.StatusCode >= 500 && x.StatusCode < 600),
            })
            .OrderByDescending(x => x.total)
            .ToListAsync();

        var topReasons = await _db.RequestLogs.AsNoTracking()
            .Where(r => r.AttemptAt >= since && r.Outcome == "BAD" && r.Reason != null && r.Reason != "")
            .GroupBy(r => r.Reason!)
            .Select(g => new { reason = g.Key, c = g.Count() })
            .OrderByDescending(x => x.c)
            .Take(20)
            .ToListAsync();

        return Ok(new { bySource, topReasons });
    }

    // Top endpoints (last 24h)
    [HttpGet("traffic/endpoints")]
    public async Task<IActionResult> Endpoints([FromQuery] int hours = 24)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Max(1, hours));
        var rows = await _db.RequestLogs.AsNoTracking()
            .Where(r => r.AttemptAt >= since)
            .GroupBy(r => new { r.Source, r.Endpoint })
            .Select(g => new { g.Key.Source, endpoint = g.Key.Endpoint, c = g.Count() })
            .OrderByDescending(x => x.c)
            .Take(50)
            .ToListAsync();
        return Ok(rows);
    }

    // =======================================================================
    // RATE LIMITING
    // =======================================================================

    // Active blocks right now (rate_limit_events.until > now)
    [HttpGet("rate/active-blocks")]
    public async Task<IActionResult> ActiveBlocks()
    {
        var now = DateTime.UtcNow;
        var rows = await _db.RateLimitEvents.AsNoTracking()
            .Where(e => e.Until != null && e.Until > now)
            .OrderBy(e => e.Until)
            .Select(e => new
            {
                e.Id,
                e.Source,
                e.AccountId,
                e.Keyword,
                e.WindowStart,
                e.WindowEnd,
                e.SeenAt,
                e.Until,
                minutesRemaining = EF.Functions.DateDiffMinute(now, e.Until!.Value)
            })
            .ToListAsync();
        return Ok(rows);
    }

    // Cooldown effectiveness (durations + percentiles)
    [HttpGet("rate/stats")]
    public async Task<IActionResult> RateStats([FromQuery] int days = 7)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        var rows = await _db.RateLimitEvents.AsNoTracking()
            .Where(e => e.SeenAt >= since && e.Until != null)
            .Select(e => new { e.Source, DurMin = EF.Functions.DateDiffMinute(e.SeenAt, e.Until!.Value) })
            .ToListAsync();

        var bySrc = rows.GroupBy(x => x.Source).Select(g =>
        {
            var list = g.Select(x => (double)x.DurMin).OrderBy(x => x).ToList();
            return new
            {
                source = g.Key,
                count = list.Count,
                avg = list.Count == 0 ? 0 : list.Average(),
                p95 = Percentile(list, 95),
                p99 = Percentile(list, 99)
            };
        }).OrderByDescending(x => x.count).ToList();

        return Ok(bySrc);
    }

    // Hammering during cooldown (requests within [seen_at, until])
    [HttpGet("rate/hammering")]
    public async Task<IActionResult> Hammering([FromQuery] int hours = 24)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Max(1, hours));
        var events = await _db.RateLimitEvents.AsNoTracking()
            .Where(e => e.SeenAt >= since && e.Until != null)
            .OrderByDescending(e => e.SeenAt)
            .Take(200)
            .ToListAsync();

        var offenders = new List<object>();
        foreach (var e in events)
        {
            var cnt = await _db.RequestLogs.AsNoTracking()
                .Where(r => r.AccountId == e.AccountId &&
                            r.AttemptAt >= e.SeenAt &&
                            r.AttemptAt <= e.Until)
                .CountAsync();
            if (cnt > 0)
                offenders.Add(new { e.Source, e.AccountId, e.SeenAt, e.Until, requestsDuringBlock = cnt });
        }

        return Ok(offenders);
    }

    // =======================================================================
    // JOBS & WORKERS
    // =======================================================================

    [HttpGet("jobs/overview")]
    public async Task<IActionResult> JobsOverview()
    {
        var rows = await _db.KeywordJobs.AsNoTracking()
            .GroupBy(j => j.Status)
            .Select(g => new { status = g.Key, c = g.Count() })
            .ToListAsync();
        return Ok(rows);
    }

    [HttpGet("jobs/stuck")]
    public async Task<IActionResult> StuckJobs([FromQuery] int staleMinutes = 10)
    {
        var now = DateTime.UtcNow;
        var stale = now.AddMinutes(-Math.Max(2, staleMinutes));

        var rows = await _db.KeywordJobs.AsNoTracking()
            .Where(j => j.Status == "searching" &&
                        (j.LastProgressAt == null || j.LastProgressAt < stale ||
                         j.LeaseExpiresAt != null && j.LeaseExpiresAt < now))
            .OrderBy(j => j.LastProgressAt)
            .Take(100)
            .Select(j => new
            {
                j.Id,
                j.Keyword,
                j.StartUtc,
                j.EndUtc,
                j.Status,
                j.AssignedTo,
                j.LastProgressAt,
                j.LeaseExpiresAt,
                j.CreatedAt
            })
            .ToListAsync();

        return Ok(rows);
    }

    [HttpGet("workers/health")]
    public async Task<IActionResult> WorkerHealth()
    {
        var now = DateTime.UtcNow;
        var activeCut = now.AddMinutes(-2);
        var staleCut = now.AddMinutes(-5);

        var workers = await _db.WorkerHeartbeats.AsNoTracking()
            .OrderByDescending(w => w.HeartbeatAt)
            .Select(w => new
            {
                w.WorkerId,
                w.Hostname,
                w.Pid,
                w.StartedAt,
                w.HeartbeatAt,
                w.CurrentJobId,
                w.CurrentSource,
                w.CurrentKeyword,
                w.CurrentWindowStart,
                w.CurrentWindowEnd
            })
            .ToListAsync();

        var active = workers.Where(w => w.HeartbeatAt >= activeCut).ToList();
        var stale = workers.Where(w => w.HeartbeatAt < staleCut).ToList();

        return Ok(new { active, stale, total = workers.Count });
    }

    // =======================================================================
    // CONTENT (ARTICLES) & PRICES
    // =======================================================================

    [HttpGet("content/overview")]
    public async Task<IActionResult> ContentOverview()
    {
        var totalG = await _db.NewsArticles.AsNoTracking().CountAsync();
        var totalE = await _db.NewsArticlesExt.AsNoTracking().CountAsync();

        var latestG = await _db.NewsArticles.AsNoTracking()
            .OrderByDescending(a => a.PublishedAt).Select(a => a.PublishedAt).FirstOrDefaultAsync();
        var latestE = await _db.NewsArticlesExt.AsNoTracking()
            .OrderByDescending(a => a.PublishedAt).Select(a => a.PublishedAt).FirstOrDefaultAsync();

        return Ok(new
        {
            totalGdelt = totalG,
            totalExt = totalE,
            latestGdelt = latestG,
            latestExt = latestE
        });
    }

    [HttpGet("content/coverage")]
    public async Task<IActionResult> ContentCoverage()
    {
        var mapG = await _db.ArticleCompanyMap.AsNoTracking()
            .GroupBy(m => m.CompanyId).Select(g => new { CompanyId = g.Key, c = g.Count() }).ToListAsync();
        var mapE = await _db.ArticleCompanyMapExt.AsNoTracking()
            .GroupBy(m => m.CompanyId).Select(g => new { CompanyId = g.Key, c = g.Count() }).ToListAsync();

        var dictG = mapG.ToDictionary(x => x.CompanyId, x => x.c);
        var dictE = mapE.ToDictionary(x => x.CompanyId, x => x.c);

        var rows = await _db.Companies.AsNoTracking()
            .Select(c => new
            {
                c.Symbol,
                gdelt = dictG.ContainsKey(c.Id) ? dictG[c.Id] : 0,
                ext = dictE.ContainsKey(c.Id) ? dictE[c.Id] : 0
            }).OrderByDescending(x => x.gdelt + x.ext).ToListAsync();

        return Ok(rows);
    }

    [HttpGet("content/dupes")]
    public async Task<IActionResult> DuplicateUrlHashes([FromQuery] int days = 1)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        var g = await _db.NewsArticles.AsNoTracking()
            .Where(a => a.CreatedAt >= since)
            .GroupBy(a => a.UrlHash).Select(g => new { url_hash = g.Key, c = g.Count() })
            .Where(x => x.c > 1).ToListAsync();

        var e = await _db.NewsArticlesExt.AsNoTracking()
            .Where(a => a.CreatedAt >= since)
            .GroupBy(a => a.UrlHash).Select(g => new { url_hash = g.Key, c = g.Count() })
            .Where(x => x.c > 1).ToListAsync();

        return Ok(new { gdelt = g, ext = e });
    }

    // top source_domain x language (7d) across both tables (joined result)
    [HttpGet("content/langmix")]
    public async Task<IActionResult> LanguageMix([FromQuery] int days = 7)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, days));

        var g = await _db.NewsArticles.AsNoTracking()
            .Where(a => a.PublishedAt != null && a.PublishedAt >= since)
            .GroupBy(a => new { a.SourceDomain, a.Language })
            .Select(g => new { source_domain = g.Key.SourceDomain, language = g.Key.Language, c = g.Count(), src = "gdelt" })
            .ToListAsync();

        var e = await _db.NewsArticlesExt.AsNoTracking()
            .Where(a => a.PublishedAt != null && a.PublishedAt >= since)
            .GroupBy(a => new { a.SourceDomain, a.Language })
            .Select(g => new { source_domain = g.Key.SourceDomain, language = g.Key.Language, c = g.Count(), src = "ext" })
            .ToListAsync();

        var joined = g.Concat(e)
            .OrderByDescending(x => x.c)
            .Take(50)
            .ToList();

        return Ok(joined);
    }

    // Latest price date per company + days since
    [HttpGet("prices/freshness")]
    public async Task<IActionResult> PriceFreshness()
    {
        var prices = await _db.DailyPrices.AsNoTracking()
            .GroupBy(p => p.CompanyId)
            .Select(g => new { CompanyId = g.Key, latest = g.Max(x => x.PriceDate) })
            .ToListAsync();

        var dict = prices.ToDictionary(x => x.CompanyId, x => x.latest);
        var today = DateTime.UtcNow.Date;

        var rows = await _db.Companies.AsNoTracking()
            .Select(c => new
            {
                c.Symbol,
                latest = dict.ContainsKey(c.Id) ? dict[c.Id] : (DateTime?)null
            })
            .OrderBy(x => x.Symbol)
            .ToListAsync();

        var withLag = rows.Select(r => new
        {
            r.Symbol,
            r.latest,
            daysSince = r.latest.HasValue ? (today - r.latest.Value.Date).TotalDays : (double?)null
        }).ToList();

        return Ok(withLag);
    }

    // ETL checkpoints + GDELT probe-bad days
    [HttpGet("control")]
    public async Task<IActionResult> EtlControl()
    {
        var ck = await _db.EtlCheckpoints.AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .Take(200)
            .ToListAsync();

        var badDays = await _db.GdeltProbeBadDays.AsNoTracking()
            .OrderByDescending(x => x.BadDate).Take(200)
            .ToListAsync();

        return Ok(new { checkpoints = ck, gdeltProbeBadDays = badDays });
    }

    // =======================================================================
    // ALERTS (actionable)
    // =======================================================================
    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts()
    {
        var now = DateTime.UtcNow;
        var hourAgo = now.AddHours(-1);

        // 1) No free keys per source
        var inventory = await _db.ApiAccounts.AsNoTracking()
            .GroupBy(a => a.Source)
            .Select(g => new
            {
                source = g.Key!,
                free = g.Count(x => x.IsActive && (x.ExhaustedUntil == null || x.ExhaustedUntil <= now))
            }).ToListAsync();

        var noFree = inventory.Where(x => x.free == 0).Select(x => x.source).ToList();

        // 2) Rising 429% in last hour (>=20% and >= 10 total)
        var err = await _db.RequestLogs.AsNoTracking()
            .Where(r => r.AttemptAt >= hourAgo)
            .GroupBy(r => r.Source)
            .Select(g => new
            {
                source = g.Key!,
                total = g.Count(),
                s429 = g.Count(x => x.StatusCode == 429)
            }).ToListAsync();

        var hot429 = err.Where(x => x.total >= 10 && (double)x.s429 / x.total >= 0.20)
                        .Select(x => new { x.source, x.total, x.s429, pct = Math.Round(100.0 * x.s429 / Math.Max(1, x.total), 2) })
                        .ToList();

        // 3) Hammering during cooldown (last 6h)
        var offenders = await HammeringInternal(6);

        // 4) Stuck jobs (searching with expired lease or stale progress)
        var stuck = (await StuckJobs(10) as OkObjectResult)?.Value;

        return Ok(new
        {
            noFreeSources = noFree,
            hot429,
            hammering = offenders,
            stuckJobs = stuck
        });
    }

    // internal helper (re-usable without extra HTTP round trip)
    private async Task<List<object>> HammeringInternal(int hours)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Max(1, hours));
        var events = await _db.RateLimitEvents.AsNoTracking()
            .Where(e => e.SeenAt >= since && e.Until != null)
            .OrderByDescending(e => e.SeenAt)
            .Take(200)
            .ToListAsync();

        var offenders = new List<object>();
        foreach (var e in events)
        {
            var cnt = await _db.RequestLogs.AsNoTracking()
                .Where(r => r.AccountId == e.AccountId &&
                            r.AttemptAt >= e.SeenAt &&
                            r.AttemptAt <= e.Until)
                .CountAsync();
            if (cnt > 0)
                offenders.Add(new { e.Source, e.AccountId, e.SeenAt, e.Until, requestsDuringBlock = cnt });
        }
        return offenders;
    }
}
