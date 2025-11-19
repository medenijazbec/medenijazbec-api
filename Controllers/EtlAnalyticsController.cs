using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
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

    // helper to read stale tor worker IDs directly from tor_workers
    // this fixes the compile errors:
    //  - DbContext has no TorWorkers DbSet
    //  - no UpdatedAt property on EF entity
    private async Task<List<string>> GetStaleTorIds(DateTime cutoffUtc)
    {
        var result = new List<string>();

        var conn = _db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose)
            await conn.OpenAsync();

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT worker_id
FROM tor_workers
WHERE updated_at < @cutoff;
";
            var p = cmd.CreateParameter();
            p.ParameterName = "@cutoff";
            p.Value = cutoffUtc;
            cmd.Parameters.Add(p);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    var wid = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(wid))
                        result.Add(wid);
                }
            }
        }
        finally
        {
            if (shouldClose)
                await conn.CloseAsync();
        }

        return result;
    }

    // ---------------------------
    // OVERVIEW
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

        // EF-translatable avg without DefaultIfEmpty
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
    // COUNTS BY COMPANY
    // ---------------------------
    [HttpGet("counts")]
    public async Task<IActionResult> CountsByCompany()
    {
        // 1. Build counts per company from ArticleCompanyMap
        var counts = await _db.ArticleCompanyMap.AsNoTracking()
            .GroupBy(x => x.CompanyId)
            .Select(g => new { CompanyId = g.Key, Total = g.Count() })
            .ToListAsync();

        // dict[companyId] = total articles
        var dict = counts.ToDictionary(x => x.CompanyId, x => x.Total);

        // 2. Pull companies into memory first (Id + Symbol only, minimal projection)
        var companies = await _db.Companies.AsNoTracking()
            .Select(c => new { c.Id, c.Symbol })
            .ToListAsync();

        // 3. Do the merge / ordering in-memory, so EF never has to translate dictionary lookups
        var rows = companies
            .Select(c => new
            {
                symbol = c.Symbol,
                total = dict.TryGetValue(c.Id, out var t) ? t : 0
            })
            .OrderByDescending(x => x.total)
            .ThenBy(x => x.symbol)
            .ToList();

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

        // "free" = active AND (exhausted_until is null OR <= now)
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

    // Next key to pick per source (LRU then UsageCount)
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

    // Fairness per source (share of busiest key in last N hours)
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

        // resolve labels (one by one, safe for MySQL translation)
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

        // Pull minimal fields then aggregate in-memory
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

    // Top endpoints (last N hours)
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

    // internal helper for Alerts()
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

    // top source_domain x language (7d) across both tables
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

        // 2) Rising 429% in last hour (>=20% and >=10 total)
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

        // 4) Stuck jobs
        var stuck = (await StuckJobs(10) as OkObjectResult)?.Value;

        return Ok(new
        {
            noFreeSources = noFree,
            hot429,
            hammering = offenders,
            stuckJobs = stuck
        });
    }

    // =======================================================================
    // DEAD-WORKER CLEANSE / GLOBAL RESET BUTTON
    // =======================================================================
    //
    // Runs full maintenance block:
    //  - finds dead/orphan workers
    //  - frees stuck keyword_jobs / job_progress
    //  - clears tor/proxy claims etc.
    //  - resets keyword_jobs back to 'unsearched'
    //
    // FIXED:
    //   we no longer access _db.TorWorkers or .UpdatedAt
    //   we pull stale tor worker ids via GetStaleTorIds()
    //
    [HttpPost("maintenance/dead-worker-cleanse")]
    public async Task<IActionResult> DeadWorkerCleanse()
    {
        var now = DateTime.UtcNow;
        var graceMinutes = 1;
        var cutoff = now.AddMinutes(-graceMinutes);

        // live_workers: heartbeats newer than grace window
        var liveWorkerIds = await _db.WorkerHeartbeats.AsNoTracking()
            .Where(w => w.HeartbeatAt >= cutoff)
            .Select(w => w.WorkerId)
            .Distinct()
            .ToListAsync();

        // stale_heartbeat_workers
        var staleHeartbeatIds = await _db.WorkerHeartbeats.AsNoTracking()
            .Where(w => w.HeartbeatAt == null || w.HeartbeatAt < cutoff)
            .Select(w => w.WorkerId)
            .Distinct()
            .ToListAsync();

        // stale_tor_workers (via raw SQL)
        var staleTorIds = await GetStaleTorIds(cutoff);

        // orphan_job_assignees: assigned_to not null but not in live_workers
        var orphanJobAssigneeIds = await _db.KeywordJobs.AsNoTracking()
            .Where(kj => kj.AssignedTo != null && !liveWorkerIds.Contains(kj.AssignedTo))
            .Select(kj => kj.AssignedTo!)
            .Distinct()
            .ToListAsync();

        // dead_workers = stale heartbeat ∪ stale tor ∪ orphan assignees
        var deadWorkersDistinct = staleHeartbeatIds
            .Concat(staleTorIds)
            .Concat(orphanJobAssigneeIds)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .ToList();

        // run MySQL maintenance block
        var maintenanceSql = @"
SET @grace_minutes := 1;
SET @now := UTC_TIMESTAMP();
SET @clear_tor   := 1;
SET @clear_proxy := 1;
SET @clear_gdelt_hold := 0;

DROP TEMPORARY TABLE IF EXISTS live_workers;
CREATE TEMPORARY TABLE live_workers AS
SELECT w.worker_id
FROM worker_heartbeats w
WHERE w.heartbeat_at >= @now - INTERVAL @grace_minutes MINUTE;

DROP TEMPORARY TABLE IF EXISTS stale_heartbeat_workers;
CREATE TEMPORARY TABLE stale_heartbeat_workers AS
SELECT w.worker_id
FROM worker_heartbeats w
WHERE w.heartbeat_at IS NULL
   OR w.heartbeat_at < @now - INTERVAL @grace_minutes MINUTE;

DROP TEMPORARY TABLE IF EXISTS stale_tor_workers;
CREATE TEMPORARY TABLE stale_tor_workers AS
SELECT t.worker_id
FROM tor_workers t
WHERE t.updated_at < @now - INTERVAL @grace_minutes MINUTE;

DROP TEMPORARY TABLE IF EXISTS orphan_job_assignees;
CREATE TEMPORARY TABLE orphan_job_assignees AS
SELECT DISTINCT kj.assigned_to AS worker_id
FROM keyword_jobs kj
LEFT JOIN live_workers lw ON lw.worker_id = kj.assigned_to
WHERE kj.assigned_to IS NOT NULL
  AND lw.worker_id IS NULL;

DROP TEMPORARY TABLE IF EXISTS dead_workers;
CREATE TEMPORARY TABLE dead_workers AS
SELECT worker_id FROM stale_heartbeat_workers
UNION
SELECT worker_id FROM stale_tor_workers
UNION
SELECT worker_id FROM orphan_job_assignees;

START TRANSACTION;

UPDATE keyword_jobs kj
JOIN (
  SELECT id
  FROM keyword_jobs
  WHERE status='currently-claimed'
    AND (
         assigned_to IN (SELECT worker_id FROM dead_workers)
      OR lease_expires_at IS NULL
      OR lease_expires_at <= @now
      OR last_progress_at IS NULL
      OR last_progress_at < @now - INTERVAL @grace_minutes MINUTE
    )
) x ON x.id = kj.id
SET kj.status='searching-undone',
    kj.assigned_to=NULL,
    kj.lease_expires_at=NULL
WHERE kj.status='currently-claimed'
  AND kj.status <> 'completed';

UPDATE keyword_jobs
SET assigned_to=NULL
WHERE status='searching-undone'
  AND assigned_to IN (SELECT worker_id FROM dead_workers);

UPDATE keyword_jobs
SET assigned_to=NULL
WHERE status <> 'completed'
  AND assigned_to IS NOT NULL
  AND (last_progress_at IS NULL OR last_progress_at < @now - INTERVAL @grace_minutes MINUTE);

UPDATE job_progress
SET assigned_to=NULL,
    lease_expires_at=NULL,
    status = CASE WHEN status='running' THEN 'pending' ELSE status END
WHERE (assigned_to IN (SELECT worker_id FROM dead_workers))
   OR (lease_expires_at IS NOT NULL AND lease_expires_at <= @now);

COMMIT;

SET @dummy := IF(@clear_tor=1, 1, 0);
DELETE FROM tor_ip_claims
WHERE (@clear_tor=1) AND (worker_id IN (SELECT worker_id FROM dead_workers) OR expires_at <= @now);

DELETE FROM tor_workers
WHERE (@clear_tor=1) AND updated_at < @now - INTERVAL @grace_minutes MINUTE;

SET @dummy := IF(@clear_proxy=1, 1, 0);
DELETE FROM proxy_claims
WHERE (@clear_proxy=1) AND (worker_id IN (SELECT worker_id FROM dead_workers) OR expires_at <= @now);

UPDATE workers
SET proxy_id=NULL, updated_at=@now
WHERE (@clear_proxy=1) AND worker_id IN (SELECT worker_id FROM dead_workers);

DELETE FROM etl_checkpoints
WHERE (@clear_gdelt_hold=1) AND etl_key LIKE 'ratelimit:gdelt:%';

DELETE FROM worker_heartbeats
WHERE heartbeat_at < @now - INTERVAL (2 * @grace_minutes) MINUTE;

UPDATE keyword_jobs
SET
    status = CASE
        WHEN status IN ('currently-claimed', 'searching-undone')
            THEN 'unsearched'
        ELSE status
    END;

DELETE FROM tor_workers;
DELETE FROM tor_ip_claims;
DELETE FROM tor_rate_limited_ips;
DELETE FROM etl_checkpoints
WHERE etl_key LIKE 'tor:exit_ip:%';
DELETE FROM tor_rate_limited_ips;
";

        await _db.Database.ExecuteSqlRawAsync(maintenanceSql);

        // After cleanup: snapshot state

        // jobCounts
        var jobCounts = await _db.KeywordJobs.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                unsearched = g.Count(j => j.Status == "unsearched"),
                searching_undone = g.Count(j => j.Status == "searching-undone"),
                currently_claimed = g.Count(j => j.Status == "currently-claimed"),
                completed = g.Count(j => j.Status == "completed")
            })
            .FirstOrDefaultAsync();

        // claimable jobs (top 50)
        var claimableSample = await _db.KeywordJobs.AsNoTracking()
            .Where(j =>
                j.Status == "unsearched" ||
                (j.Status == "searching-undone" && j.AssignedTo == null)
            )
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.LastProgressAt ?? j.CreatedAt)
            .ThenBy(j => j.Id)
            .Select(j => new
            {
                j.Id,
                j.Keyword,
                j.Status,
                j.Priority,
                j.AssignedTo,
                j.LastProgressAt,
                j.LeaseExpiresAt
            })
            .Take(50)
            .ToListAsync();

        // gdelt progress (50)
        var gdeltProgressSample =
            await (from kj in _db.KeywordJobs.AsNoTracking()
                   join jp in _db.JobProgress.AsNoTracking().Where(x => x.Source == "gdelt")
                        on kj.Id equals jp.JobId into jpgroup
                   from jp in jpgroup.DefaultIfEmpty()
                   orderby kj.Priority descending, kj.CreatedAt descending
                   select new
                   {
                       kj.Id,
                       kj.Keyword,
                       kj.Status,
                       gdelt_progress = jp != null ? jp.Message : null,
                       kj.AssignedTo,
                       kj.LastProgressAt
                   })
            .Take(50)
            .ToListAsync();

        return Ok(new
        {
            deadWorkers = new
            {
                count = deadWorkersDistinct.Count,
                ids = deadWorkersDistinct
            },
            jobCounts,
            claimableSample,
            gdeltProgressSample
        });
    }

    // =======================================================================
    // JOB COMPLETION COUNTER
    // =======================================================================
    //
    // MySQL precedence:
    // (id >= 35584 AND status='completed') OR status='done'
    /// (kj.Id >= 35584 && kj.Status == "completed") ||
    [HttpGet("jobs/completed-counter")]
    public async Task<IActionResult> CompletedCounter()
    {
        var completedCount = await _db.KeywordJobs.AsNoTracking()
            .Where(kj =>
                (kj.Status == "completed") ||
                kj.Status == "done")
            .CountAsync();

        return Ok(new { completed_count = completedCount });
    }

    // =======================================================================
    // TOR / EXIT-IP STATUS SNAPSHOT
    // =======================================================================
    [HttpGet("network/tor-status")]
    public async Task<IActionResult> TorStatus()
    {
        var blockedIpsRows = new List<object>();
        var workersPerIpRows = new List<object>();
        int totalBlocked = 0;

        var conn = _db.Database.GetDbConnection();
        await using (conn)
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            // tor_rate_limited_ips rows
            await using (var cmd1 = conn.CreateCommand())
            {
                cmd1.CommandText = @"
SELECT ip, source, until, reason, hits, created_at
FROM tor_rate_limited_ips
ORDER BY until DESC, ip ASC;
";
                await using (var reader = await cmd1.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var ip = reader.IsDBNull(0) ? null : reader.GetString(0);
                        var source = reader.IsDBNull(1) ? null : reader.GetString(1);
                        var until = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);
                        var reason = reader.IsDBNull(3) ? null : reader.GetString(3);
                        var hits = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                        var createdAt = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);

                        blockedIpsRows.Add(new
                        {
                            ip,
                            source,
                            until,
                            reason,
                            hits,
                            created_at = createdAt
                        });
                    }
                }
            }

            totalBlocked = blockedIpsRows.Count;

            // workers per tor exit ip
            await using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = @"
SELECT
    tw.exit_ip,
    COUNT(*) AS worker_count,
    GROUP_CONCAT(tw.worker_id ORDER BY tw.worker_id) AS workers
FROM tor_workers tw
WHERE tw.exit_ip IS NOT NULL
GROUP BY tw.exit_ip
ORDER BY worker_count DESC, tw.exit_ip;
";
                await using (var reader2 = await cmd2.ExecuteReaderAsync())
                {
                    while (await reader2.ReadAsync())
                    {
                        var exitIp = reader2.IsDBNull(0) ? null : reader2.GetString(0);
                        var workerCount = reader2.IsDBNull(1) ? 0 : reader2.GetInt64(1);
                        var workers = reader2.IsDBNull(2) ? null : reader2.GetString(2);

                        workersPerIpRows.Add(new
                        {
                            exit_ip = exitIp,
                            worker_count = workerCount,
                            workers
                        });
                    }
                }
            }
        }

        return Ok(new
        {
            totalBlocked,
            blockedIps = blockedIpsRows,
            workersPerIp = workersPerIpRows
        });
    }

    // =======================================================================
    // CONTENT MINING STATS
    // =======================================================================
    //
    // "mined"  = article body present in ml_page_cache
    // "clean"  = ml_articles.clean_text populated
    //
    [HttpGet("content/mining-stats")]
    public async Task<IActionResult> MiningStats()
    {
        // We'll run three separate COUNT(*) queries instead of one giant multi-subquery.
        // This lets us:
        //   - give each command a higher timeout
        //   - return partial data instead of blowing up the whole request on timeout
        //
        // Also, MySqlConnector default command timeout is usually ~30s.
        // These tables can be large, so we explicitly bump CommandTimeout.

        const int COMMAND_TIMEOUT_SECONDS = 300; // tune as needed

        long totalArticles = 0;
        long minedArticles = 0;
        long cleanArticles = 0;

        string? totalErr = null;
        string? minedErr = null;
        string? cleanErr = null;

        var conn = _db.Database.GetDbConnection();
        await using (conn)
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            // helper to run a scalar COUNT(*) safely
            async Task<(long count, string? err)> RunCountAsync(string sql)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = COMMAND_TIMEOUT_SECONDS; // critical: avoid 30s default timeout

                try
                {
                    var scalar = await cmd.ExecuteScalarAsync();
                    if (scalar == null || scalar == DBNull.Value)
                        return (0L, null);

                    // Convert.ToInt64 handles both int64 and decimal(…) COUNT(*) returns
                    return (Convert.ToInt64(scalar), null);
                }
                catch (Exception ex)
                {
                    // swallow the exception for resiliency and report it in the payload
                    return (0L, ex.Message);
                }
            }

            // 1) total_articles
            {
                var (cnt, err) = await RunCountAsync(@"
                SELECT COUNT(*) 
                FROM ml_articles;
            ");
                totalArticles = cnt;
                totalErr = err;
            }

            // 2) mined_articles
            // definition: article body present in ml_page_cache
            // We consider "mined" an article that has non-empty cached text for its url_hash.
            // We only count rows that actually exist in ml_articles (EXISTS subquery).
            {
                var (cnt, err) = await RunCountAsync(@"
                SELECT COUNT(*)
                FROM ml_page_cache c
                WHERE c.text IS NOT NULL
                  AND c.text <> ''
                  AND EXISTS (
                        SELECT 1
                        FROM ml_articles a
                        WHERE a.url_hash = c.url_hash
                  );
            ");
                minedArticles = cnt;
                minedErr = err;
            }

            // 3) clean_articles
            // definition: ml_articles.clean_text populated (non-empty)
            {
                var (cnt, err) = await RunCountAsync(@"
                SELECT COUNT(*)
                FROM ml_articles a
                WHERE a.clean_text IS NOT NULL
                  AND a.clean_text <> '';
            ");
                cleanArticles = cnt;
                cleanErr = err;
            }
        }

        // derived percentages
        double minedPct = 0.0;
        double cleanPct = 0.0;
        if (totalArticles > 0)
        {
            minedPct = (double)minedArticles / totalArticles * 100.0;
            cleanPct = (double)cleanArticles / totalArticles * 100.0;
        }

        var response = new
        {
            total_articles = totalArticles,
            mined_articles = minedArticles,
            clean_articles = cleanArticles,
            mined_pct = Math.Round(minedPct, 2),
            clean_pct = Math.Round(cleanPct, 2),

            // expose any partial failures so the UI can surface "data stale / timed out"
            debug = new
            {
                total_err = totalErr,
                mined_err = minedErr,
                clean_err = cleanErr,
                command_timeout_seconds = COMMAND_TIMEOUT_SECONDS
            }
        };

        return Ok(response);
    }


    // =======================================================================
    // CLEANER DASHBOARD (NEW)
    // =======================================================================
    //
    // 1. cleaner/overview
    //    - queue depth (queued / cleaning / error / done from ml_clean_jobs.status)
    //    - throughput last 1h/24h (status='done' and claimed_at in window)
    //    - success vs error (request_log where source='cleaner' in last hour)
    //    - avg attempts per job (ml_clean_jobs.attempt)
    //
    [HttpGet("cleaner/overview")]
    public async Task<IActionResult> CleanerOverview()
    {
        long queued = 0, cleaning = 0, err = 0, done = 0;
        long done1h = 0, done24h = 0;
        long okCnt = 0, badCnt = 0, totalCnt = 0;
        double avgAttempts = 0.0;
        long maxAttempts = 0;
        long totalJobs = 0;

        var conn = _db.Database.GetDbConnection();
        await using (conn)
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
-- Queue depth
SELECT
 SUM(CASE WHEN status='queued'   THEN 1 ELSE 0 END) AS queued,
 SUM(CASE WHEN status='cleaning' THEN 1 ELSE 0 END) AS cleaning,
 SUM(CASE WHEN status='error'    THEN 1 ELSE 0 END) AS err,
 SUM(CASE WHEN status='done'     THEN 1 ELSE 0 END) AS done
FROM ml_clean_jobs;

-- Throughput in last 1h / 24h
SELECT
 SUM(CASE WHEN status='done' AND claimed_at >= UTC_TIMESTAMP() - INTERVAL 1 HOUR  THEN 1 ELSE 0 END) AS done1h,
 SUM(CASE WHEN status='done' AND claimed_at >= UTC_TIMESTAMP() - INTERVAL 24 HOUR THEN 1 ELSE 0 END) AS done24h
FROM ml_clean_jobs;

-- Cleaner success vs error in last hour
SELECT
 SUM(CASE WHEN outcome='OK'  THEN 1 ELSE 0 END) AS ok_cnt,
 SUM(CASE WHEN outcome='BAD' THEN 1 ELSE 0 END) AS bad_cnt,
 COUNT(*)                                         AS total_cnt
FROM request_log
WHERE source='cleaner'
  AND attempt_at >= UTC_TIMESTAMP() - INTERVAL 1 HOUR;

-- Attempts stats
SELECT
 AVG(attempt) AS avg_attempt,
 MAX(attempt) AS max_attempt,
 COUNT(*)     AS total_jobs
FROM ml_clean_jobs;
";

            await using (cmd)
            {
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    // result 1: queue depth
                    if (await reader.ReadAsync())
                    {
                        queued = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                        cleaning = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                        err = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                        done = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                    }

                    // result 2: throughput
                    await reader.NextResultAsync();
                    if (await reader.ReadAsync())
                    {
                        done1h = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                        done24h = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    }

                    // result 3: success/error
                    await reader.NextResultAsync();
                    if (await reader.ReadAsync())
                    {
                        okCnt = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                        badCnt = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                        totalCnt = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                    }

                    // result 4: attempts stats
                    await reader.NextResultAsync();
                    if (await reader.ReadAsync())
                    {
                        avgAttempts = reader.IsDBNull(0) ? 0.0 : Convert.ToDouble(reader.GetValue(0));
                        maxAttempts = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                        totalJobs = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                    }
                }
            }
        }

        double successPct = 0.0;
        double errPct = 0.0;
        if (totalCnt > 0)
        {
            successPct = (double)okCnt / totalCnt * 100.0;
            errPct = (double)badCnt / totalCnt * 100.0;
        }

        var payload = new
        {
            queueDepth = new
            {
                queued,
                cleaning,
                error = err,
                done
            },
            throughput = new
            {
                jobsDoneLastHour = done1h,
                jobsDoneLast24h = done24h
            },
            successRate = new
            {
                ok = okCnt,
                bad = badCnt,
                total = totalCnt,
                successRatePct = Math.Round(successPct, 2),
                errorRatePct = Math.Round(errPct, 2)
            },
            attempts = new
            {
                avgAttempts = Math.Round(avgAttempts, 2),
                maxAttempts,
                totalJobs
            }
        };

        return Ok(payload);
    }

    // 2. cleaner/workers
    //    - per worker_id: last activity, last event, success/error in last 10m
    //    - health color (green/yellow/red)
    //    - capacity summary (containersObserved * WORKERS_PER_CONTAINER)
    [HttpGet("cleaner/workers")]
    public async Task<IActionResult> CleanerWorkers()
    {
        const int WORKERS_PER_CONTAINER_DEFAULT = 3;
        var now = DateTime.UtcNow;

        var workerIds = new List<string>();

        // grab distinct worker_ids from ml_clean_jobs.claimed_by
        var conn0 = _db.Database.GetDbConnection();
        await using (conn0)
        {
            if (conn0.State != ConnectionState.Open)
                await conn0.OpenAsync();

            // We UNION ml_clean_jobs.claimed_by and request_log.response_meta.worker_id.
            // (request_log JSON is easier to pull in SQL here than EF.)
            await using (var cmd0 = conn0.CreateCommand())
            {
                cmd0.CommandText = @"
SELECT DISTINCT claimed_by AS worker_id
FROM ml_clean_jobs
WHERE claimed_by IS NOT NULL
UNION
SELECT DISTINCT JSON_UNQUOTE(JSON_EXTRACT(response_meta,'$.worker_id')) AS worker_id
FROM request_log
WHERE source='cleaner'
  AND JSON_EXTRACT(response_meta,'$.worker_id') IS NOT NULL;
";
                await using (var r0 = await cmd0.ExecuteReaderAsync())
                {
                    while (await r0.ReadAsync())
                    {
                        if (!r0.IsDBNull(0))
                        {
                            var wid = r0.GetString(0);
                            if (!string.IsNullOrWhiteSpace(wid))
                                workerIds.Add(wid);
                        }
                    }
                }
            }
        }

        workerIds = workerIds.Distinct().ToList();

        var workerInfos = new List<object>();
        var activeRecentlyCount = 0;
        var containerIds = new HashSet<string>();

        foreach (var wid in workerIds)
        {
            DateTime? lastActivity = null;
            string? lastEventMeta = null;
            long success10m = 0;
            long total10m = 0;

            var conn = _db.Database.GetDbConnection();
            await using (conn)
            {
                if (conn.State != ConnectionState.Open)
                    await conn.OpenAsync();

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT
  (SELECT MAX(cj.claimed_at)
   FROM ml_clean_jobs cj
   WHERE cj.claimed_by = @wid) AS last_activity,

  (SELECT rl.response_meta
   FROM request_log rl
   WHERE rl.source='cleaner'
     AND JSON_UNQUOTE(JSON_EXTRACT(rl.response_meta,'$.worker_id')) = @wid
   ORDER BY rl.attempt_at DESC
   LIMIT 1) AS last_event_meta,

  (SELECT COUNT(*)
   FROM request_log rl
   WHERE rl.source='cleaner'
     AND rl.attempt_at >= UTC_TIMESTAMP() - INTERVAL 10 MINUTE
     AND JSON_UNQUOTE(JSON_EXTRACT(rl.response_meta,'$.worker_id')) = @wid
     AND rl.outcome='OK') AS success_10m,

  (SELECT COUNT(*)
   FROM request_log rl
   WHERE rl.source='cleaner'
     AND rl.attempt_at >= UTC_TIMESTAMP() - INTERVAL 10 MINUTE
     AND JSON_UNQUOTE(JSON_EXTRACT(rl.response_meta,'$.worker_id')) = @wid) AS total_10m;
";

                    var p = cmd.CreateParameter();
                    p.ParameterName = "@wid";
                    p.Value = wid;
                    cmd.Parameters.Add(p);

                    await using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            lastActivity = reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0);
                            lastEventMeta = reader.IsDBNull(1) ? null : reader.GetString(1);
                            success10m = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                            total10m = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                        }
                    }
                }
            }

            // status color
            string health;
            var inactiveTooLong = true;
            if (lastActivity.HasValue)
            {
                inactiveTooLong = (now - lastActivity.Value).TotalMinutes > 10.0;
            }

            if (success10m > 0)
            {
                health = "green";
            }
            else if (success10m == 0 && total10m > 0)
            {
                health = "yellow";
            }
            else
            {
                health = inactiveTooLong ? "red" : "yellow";
            }

            if (lastActivity.HasValue && (now - lastActivity.Value).TotalMinutes <= 10.0)
            {
                activeRecentlyCount++;
            }

            // guess container hostname from "<container_hostname>:clean:<idx>"
            string containerGuess = wid;
            var idx = wid.IndexOf(":clean:", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                containerGuess = wid.Substring(0, idx);
            }
            containerIds.Add(containerGuess);

            workerInfos.Add(new
            {
                worker_id = wid,
                last_activity = lastActivity,
                last_event_meta = lastEventMeta,
                success_10m = success10m,
                total_10m = total10m,
                error_rate_10m = total10m > 0
                    ? Math.Round(((double)(total10m - success10m) / total10m) * 100.0, 2)
                    : 0.0,
                health
            });
        }

        var containersObserved = containerIds.Count;
        var expectedWorkers = containersObserved * WORKERS_PER_CONTAINER_DEFAULT;

        var payload = new
        {
            workers = workerInfos,
            summary = new
            {
                totalWorkersObserved = workerIds.Count,
                activeRecently = activeRecentlyCount,
                idleOrDead = workerIds.Count - activeRecentlyCount
            },
            capacity = new
            {
                workersPerContainerDefault = WORKERS_PER_CONTAINER_DEFAULT,
                containersObserved,
                expectedWorkers,
                activeRecently = activeRecentlyCount
            }
        };

        return Ok(payload);
    }

    // 3. cleaner/queue-inspector
    //    - problem jobs:
    //         stuck in cleaning with expired lease
    //         maxed retries (status='error' AND attempt >= MAX_ATTEMPTS)
    [HttpGet("cleaner/queue-inspector")]
    public async Task<IActionResult> CleanerQueueInspector()
    {
        const int MAX_ATTEMPTS_DEFAULT = 5;

        var stuckCleaning = new List<object>();
        var maxedRetries = new List<object>();

        var conn = _db.Database.GetDbConnection();
        await using (conn)
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            // stuck in cleaning
            await using (var cmd1 = conn.CreateCommand())
            {
                cmd1.CommandText = @"
SELECT id,
       article_id,
       status,
       attempt,
       claimed_by,
       claimed_at,
       lease_expires_at,
       last_error
FROM ml_clean_jobs
WHERE status='cleaning'
  AND lease_expires_at IS NOT NULL
  AND lease_expires_at < UTC_TIMESTAMP()
ORDER BY lease_expires_at ASC
LIMIT 200;
";
                await using (var r1 = await cmd1.ExecuteReaderAsync())
                {
                    while (await r1.ReadAsync())
                    {
                        stuckCleaning.Add(new
                        {
                            id = r1.IsDBNull(0) ? (long?)null : r1.GetInt64(0),
                            article_id = r1.IsDBNull(1) ? (long?)null : r1.GetInt64(1),
                            status = r1.IsDBNull(2) ? null : r1.GetString(2),
                            attempt = r1.IsDBNull(3) ? (int?)null : Convert.ToInt32(r1.GetValue(3)),
                            claimed_by = r1.IsDBNull(4) ? null : r1.GetString(4),
                            claimed_at = r1.IsDBNull(5) ? (DateTime?)null : r1.GetDateTime(5),
                            lease_expires_at = r1.IsDBNull(6) ? (DateTime?)null : r1.GetDateTime(6),
                            last_error = r1.IsDBNull(7) ? null : r1.GetString(7)
                        });
                    }
                }
            }

            // maxed retries
            await using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = @"
SELECT id,
       article_id,
       status,
       attempt,
       claimed_by,
       claimed_at,
       lease_expires_at,
       last_error
FROM ml_clean_jobs
WHERE status='error'
  AND attempt >= @maxAttempts
ORDER BY attempt DESC, id DESC
LIMIT 200;
";
                var p2 = cmd2.CreateParameter();
                p2.ParameterName = "@maxAttempts";
                p2.Value = MAX_ATTEMPTS_DEFAULT;
                cmd2.Parameters.Add(p2);

                await using (var r2 = await cmd2.ExecuteReaderAsync())
                {
                    while (await r2.ReadAsync())
                    {
                        maxedRetries.Add(new
                        {
                            id = r2.IsDBNull(0) ? (long?)null : r2.GetInt64(0),
                            article_id = r2.IsDBNull(1) ? (long?)null : r2.GetInt64(1),
                            status = r2.IsDBNull(2) ? null : r2.GetString(2),
                            attempt = r2.IsDBNull(3) ? (int?)null : Convert.ToInt32(r2.GetValue(3)),
                            claimed_by = r2.IsDBNull(4) ? null : r2.GetString(4),
                            claimed_at = r2.IsDBNull(5) ? (DateTime?)null : r2.GetDateTime(5),
                            lease_expires_at = r2.IsDBNull(6) ? (DateTime?)null : r2.GetDateTime(6),
                            last_error = r2.IsDBNull(7) ? null : r2.GetString(7)
                        });
                    }
                }
            }
        }

        return Ok(new
        {
            stuckCleaning,
            maxedRetries,
            maxAttemptsDefault = MAX_ATTEMPTS_DEFAULT
        });
    }
    // =======================================================================
    // TOR: CLEAR RATE-LIMITED IP LIST
    // =======================================================================
    //
    // Nukes the tor_rate_limited_ips table (used by the "Clear blocked list" button in the UI).
    // Returns { cleared: <rows_affected> }.
    //
    [HttpPost("network/tor-clear-rate-limited")]
    public async Task<IActionResult> TorClearRateLimited()
    {
        var conn = _db.Database.GetDbConnection();
        await using (conn)
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"DELETE FROM tor_rate_limited_ips;";
                var affected = await cmd.ExecuteNonQueryAsync();
                return Ok(new { cleared = affected });
            }
        }
    }

    // 4. cleaner/recent-cleaned
    //    - spot check output quality
    //    - includes preview of clean_text/headline_norm and source body
    [HttpGet("cleaner/recent-cleaned")]
    public async Task<IActionResult> CleanerRecentCleaned()
    {
        var rows = new List<object>();

        var conn = _db.Database.GetDbConnection();
        await using (conn)
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT
 j.article_id,
 a.title,
 LENGTH(a.clean_text)       AS clean_text_len,
 LENGTH(a.headline_norm)    AS headline_norm_len,
 j.claimed_at,
 SUBSTRING(a.clean_text,1,2000)          AS clean_text_preview,
 SUBSTRING(a.headline_norm,1,500)        AS headline_norm_preview,
 SUBSTRING(a.text,1,2000)                AS raw_article_text_preview,
 SUBSTRING(pc.text,1,2000)               AS cached_text_preview,
 (
   SELECT JSON_UNQUOTE(JSON_EXTRACT(rl.response_meta,'$.body_source'))
   FROM request_log rl
   WHERE rl.source='cleaner'
     AND rl.outcome='OK'
     AND JSON_UNQUOTE(JSON_EXTRACT(rl.response_meta,'$.article_id')) = CAST(j.article_id AS CHAR)
   ORDER BY rl.attempt_at DESC
   LIMIT 1
 ) AS body_source
FROM ml_clean_jobs j
JOIN ml_articles a ON a.id = j.article_id
LEFT JOIN ml_page_cache pc ON pc.url_hash = a.url_hash
WHERE j.status='done'
ORDER BY j.claimed_at DESC
LIMIT 50;
";
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        rows.Add(new
                        {
                            article_id = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0),
                            title = reader.IsDBNull(1) ? null : reader.GetString(1),
                            clean_text_len = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2),
                            headline_norm_len = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3),
                            claimed_at = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4),
                            clean_text_preview = reader.IsDBNull(5) ? null : reader.GetString(5),
                            headline_norm_preview = reader.IsDBNull(6) ? null : reader.GetString(6),
                            raw_article_text_preview = reader.IsDBNull(7) ? null : reader.GetString(7),
                            cached_text_preview = reader.IsDBNull(8) ? null : reader.GetString(8),
                            body_source = reader.IsDBNull(9) ? null : reader.GetString(9)
                        });
                    }
                }
            }
        }

        return Ok(rows);
    }

    // 5. cleaner/error-intel
    //    - groups BAD cleaner runs by reason (last 1h)
    //    - counts unique articles, avg attempt, sample article_ids + last_error
    [HttpGet("cleaner/error-intel")]
    public async Task<IActionResult> CleanerErrorIntel()
    {
        var reasons = new List<(string reason, long totalErrors, long uniqueArticles, double avgAttempt)>();
        var detailsPerReason = new Dictionary<string, List<object>>();

        var conn = _db.Database.GetDbConnection();
        await using (conn)
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            // Top 5 reasons
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT
  rl.reason,
  COUNT(*) AS total_errors,
  COUNT(DISTINCT CAST(JSON_UNQUOTE(JSON_EXTRACT(rl.response_meta,'$.article_id')) AS UNSIGNED)) AS unique_articles,
  AVG(j.attempt) AS avg_attempt
FROM request_log rl
LEFT JOIN ml_clean_jobs j
  ON j.article_id = CAST(JSON_UNQUOTE(JSON_EXTRACT(rl.response_meta,'$.article_id')) AS UNSIGNED)
WHERE rl.source='cleaner'
  AND rl.outcome='BAD'
  AND rl.attempt_at >= UTC_TIMESTAMP() - INTERVAL 1 HOUR
GROUP BY rl.reason
ORDER BY total_errors DESC
LIMIT 5;
";
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var reason = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        var totalErrors = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                        var uniqueArticles = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                        var avgAttempt = reader.IsDBNull(3) ? 0.0 : Convert.ToDouble(reader.GetValue(3));

                        reasons.Add((reason, totalErrors, uniqueArticles, avgAttempt));
                    }
                }
            }

            // For each reason: sample affected articles
            foreach (var rinfo in reasons)
            {
                var items = new List<object>();

                await using (var cmd2 = conn.CreateCommand())
                {
                    cmd2.CommandText = @"
SELECT
  CAST(JSON_UNQUOTE(JSON_EXTRACT(rl.response_meta,'$.article_id')) AS UNSIGNED) AS article_id,
  MAX(j.last_error) AS last_error
FROM request_log rl
LEFT JOIN ml_clean_jobs j
  ON j.article_id = CAST(JSON_UNQUOTE(JSON_EXTRACT(rl.response_meta,'$.article_id')) AS UNSIGNED)
WHERE rl.source='cleaner'
  AND rl.outcome='BAD'
  AND rl.attempt_at >= UTC_TIMESTAMP() - INTERVAL 1 HOUR
  AND rl.reason = @reason
GROUP BY article_id
ORDER BY article_id DESC
LIMIT 10;
";

                    var pReason = cmd2.CreateParameter();
                    pReason.ParameterName = "@reason";
                    pReason.Value = rinfo.reason ?? "";
                    cmd2.Parameters.Add(pReason);

                    await using (var r2 = await cmd2.ExecuteReaderAsync())
                    {
                        while (await r2.ReadAsync())
                        {
                            var aid = r2.IsDBNull(0) ? (long?)null : r2.GetInt64(0);
                            var lastErr = r2.IsDBNull(1) ? null : r2.GetString(1);
                            items.Add(new
                            {
                                article_id = aid,
                                last_error = lastErr
                            });
                        }
                    }
                }

                detailsPerReason[rinfo.reason] = items;
            }
        }

        var response = reasons.Select(r => new
        {
            reason = r.reason,
            total_errors = r.totalErrors,
            unique_articles = r.uniqueArticles,
            avg_attempt = Math.Round(r.avgAttempt, 2),
            samples = detailsPerReason.ContainsKey(r.reason) ? detailsPerReason[r.reason] : new List<object>()
        }).ToList();

        return Ok(response);
    }

    // 6. cleaner/capacity
    //    - reports config knobs + observed capacity
    //    - WORKERS_PER_CONTAINER default 3
    //    - LEASE_SECONDS default 300
    //    - MAX_ATTEMPTS default 5
    //    - SLEEP_IDLE default 2.0s
    [HttpGet("cleaner/capacity")]
    public async Task<IActionResult> CleanerCapacity()
    {
        const int WORKERS_PER_CONTAINER_DEFAULT = 3;
        const int LEASE_SECONDS_DEFAULT = 300;
        const int MAX_ATTEMPTS_DEFAULT = 5;
        const double SLEEP_IDLE_DEFAULT = 2.0;

        var now = DateTime.UtcNow;

        var workerIds = new List<string>();
        var lastActivityByWorker = new Dictionary<string, DateTime?>();

        var conn0 = _db.Database.GetDbConnection();
        await using (conn0)
        {
            if (conn0.State != ConnectionState.Open)
                await conn0.OpenAsync();

            // collect worker_ids same way as cleaner/workers
            await using (var cmd0 = conn0.CreateCommand())
            {
                cmd0.CommandText = @"
SELECT DISTINCT claimed_by AS worker_id
FROM ml_clean_jobs
WHERE claimed_by IS NOT NULL
UNION
SELECT DISTINCT JSON_UNQUOTE(JSON_EXTRACT(response_meta,'$.worker_id')) AS worker_id
FROM request_log
WHERE source='cleaner'
  AND JSON_EXTRACT(response_meta,'$.worker_id') IS NOT NULL;
";
                await using (var r0 = await cmd0.ExecuteReaderAsync())
                {
                    while (await r0.ReadAsync())
                    {
                        if (!r0.IsDBNull(0))
                        {
                            var wid = r0.GetString(0);
                            if (!string.IsNullOrWhiteSpace(wid))
                                workerIds.Add(wid);
                        }
                    }
                }
            }

            workerIds = workerIds.Distinct().ToList();

            // last activity per worker (max claimed_at)
            foreach (var wid in workerIds)
            {
                await using (var cmd1 = conn0.CreateCommand())
                {
                    cmd1.CommandText = @"
SELECT MAX(cj.claimed_at)
FROM ml_clean_jobs cj
WHERE cj.claimed_by = @wid;
";
                    var p1 = cmd1.CreateParameter();
                    p1.ParameterName = "@wid";
                    p1.Value = wid;
                    cmd1.Parameters.Add(p1);

                    object? scalar = await cmd1.ExecuteScalarAsync();
                    DateTime? lastAct = null;
                    if (scalar != null && scalar != DBNull.Value)
                    {
                        lastAct = Convert.ToDateTime(scalar);
                    }
                    lastActivityByWorker[wid] = lastAct;
                }
            }
        }

        // infer containers from the prefix "<container_hostname>:clean:<idx>"
        var containerIds = new HashSet<string>();
        int activeRecentlyCount = 0;

        foreach (var wid in workerIds)
        {
            string containerGuess = wid;
            var idx = wid.IndexOf(":clean:", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                containerGuess = wid.Substring(0, idx);
            }
            containerIds.Add(containerGuess);

            if (lastActivityByWorker.TryGetValue(wid, out var lastAct) && lastAct.HasValue)
            {
                if ((now - lastAct.Value).TotalMinutes <= 10.0)
                {
                    activeRecentlyCount++;
                }
            }
        }

        var containersObserved = containerIds.Count;
        var expectedWorkers = containersObserved * WORKERS_PER_CONTAINER_DEFAULT;

        return Ok(new
        {
            config = new
            {
                leaseSecondsDefault = LEASE_SECONDS_DEFAULT,
                maxAttemptsDefault = MAX_ATTEMPTS_DEFAULT,
                sleepIdleSecondsDefault = SLEEP_IDLE_DEFAULT,
                workersPerContainerDefault = WORKERS_PER_CONTAINER_DEFAULT
            },
            capacity = new
            {
                containersObserved,
                expectedWorkers,
                activeRecently = activeRecentlyCount,
                totalWorkersObserved = workerIds.Count
            }
        });
    }
}
