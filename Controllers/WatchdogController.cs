using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using honey_badger_api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;

namespace honey_badger_api.Controllers;

[ApiController]
[Route("api/watchdog")]
[Authorize(Roles = "Admin")]
public class WatchdogController : ControllerBase
{
    private readonly AppDbContext _appDb;
    private readonly NvdaAlphaDbContext _alphaDb;
    private readonly NvdaTradingDbContext _tradingDb;

    // crude process uptime
    private static readonly DateTime ProcessStartedUtc = DateTime.UtcNow;

    public WatchdogController(
        AppDbContext appDb,
        NvdaAlphaDbContext alphaDb,
        NvdaTradingDbContext tradingDb)
    {
        _appDb = appDb;
        _alphaDb = alphaDb;
        _tradingDb = tradingDb;
    }

    // DTO returned to frontend for DB health
    public sealed class DbHealthDto
    {
        public string Name { get; set; } = "";
        public bool Ok { get; set; }
        public double LatencyMs { get; set; }
        public DateTime? DbUtcNow { get; set; }
        public string? Error { get; set; }
    }

    // DTO returned to frontend for watchdog_cleanup_log stats
    public sealed class CleanupStatsDto
    {
        public long TotalRuns { get; set; }
        public DateTime? FirstRunStartedAt { get; set; }
        public DateTime? LastRunFinishedAt { get; set; }

        // sums across all rows
        public long HttpArticlesDeletedSum { get; set; }
        public long HttpsArticlesDeletedSum { get; set; }
        public long Status404DeletedSum { get; set; }
        public long Status410DeletedSum { get; set; }
        public long BadLinkArticlesDeletedSum { get; set; }
        public long MlArticlesDeletedSum { get; set; }
        public long NewsArticlesDeletedSum { get; set; }
        public long PageCacheDeletedSum { get; set; }
        public long TotalRowsDeletedSum { get; set; }

        // last run metrics
        public DateTime? LastRunStartedAt { get; set; }
        

        public long? LastHttpArticlesDeleted { get; set; }
        public long? LastHttpsArticlesDeleted { get; set; }
        public long? LastStatus404Deleted { get; set; }
        public long? LastStatus410Deleted { get; set; }
        public long? LastBadLinkArticlesDeleted { get; set; }
        public long? LastMlArticlesDeleted { get; set; }
        public long? LastNewsArticlesDeleted { get; set; }
        public long? LastPageCacheDeleted { get; set; }
        public long? LastTotalRowsDeleted { get; set; }
    }

    private static async Task<DbHealthDto> ProbeAsync(
        DbContext ctx,
        string logicalName,
        CancellationToken ct)
    {
        var result = new DbHealthDto { Name = logicalName };
        var sw = Stopwatch.StartNew();

        DbConnection conn = ctx.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;

        try
        {
            if (shouldClose)
                await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT UTC_TIMESTAMP()";
            var scalar = await cmd.ExecuteScalarAsync(ct);
            sw.Stop();

            result.Ok = true;
            result.LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

            if (scalar != null && scalar != DBNull.Value)
            {
                result.DbUtcNow = Convert.ToDateTime(scalar);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Ok = false;
            result.LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            result.Error = ex.Message;
        }
        finally
        {
            if (shouldClose && conn.State == ConnectionState.Open)
                await conn.CloseAsync();
        }

        return result;
    }

    private async Task<CleanupStatsDto> LoadCleanupStatsAsync(CancellationToken ct)
    {
        var dto = new CleanupStatsDto();

        DbConnection conn = _alphaDb.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;

        try
        {
            if (shouldClose)
                await conn.OpenAsync(ct);

            // 1) Aggregate stats across all runs
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT
                        COUNT(*)                        AS total_runs,
                        MIN(run_started_at)             AS first_run_started,
                        MAX(run_finished_at)            AS last_run_finished,
                        COALESCE(SUM(http_articles_deleted), 0)     AS http_sum,
                        COALESCE(SUM(https_articles_deleted), 0)    AS https_sum,
                        COALESCE(SUM(status_404_deleted), 0)        AS s404_sum,
                        COALESCE(SUM(status_410_deleted), 0)        AS s410_sum,
                        COALESCE(SUM(bad_link_articles_deleted), 0) AS bad_sum,
                        COALESCE(SUM(ml_articles_deleted), 0)       AS ml_sum,
                        COALESCE(SUM(news_articles_deleted), 0)     AS news_sum,
                        COALESCE(SUM(page_cache_deleted), 0)        AS cache_sum,
                        COALESCE(SUM(total_rows_deleted), 0)        AS total_sum
                    FROM watchdog_cleanup_log;
                ";

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    dto.TotalRuns = reader.GetFieldValue<long>(reader.GetOrdinal("total_runs"));

                    dto.FirstRunStartedAt = reader.IsDBNull(reader.GetOrdinal("first_run_started"))
                        ? null
                        : reader.GetFieldValue<DateTime>(reader.GetOrdinal("first_run_started"));

                    dto.LastRunFinishedAt = reader.IsDBNull(reader.GetOrdinal("last_run_finished"))
                        ? null
                        : reader.GetFieldValue<DateTime>(reader.GetOrdinal("last_run_finished"));

                    dto.HttpArticlesDeletedSum = reader.GetFieldValue<long>(reader.GetOrdinal("http_sum"));
                    dto.HttpsArticlesDeletedSum = reader.GetFieldValue<long>(reader.GetOrdinal("https_sum"));
                    dto.Status404DeletedSum = reader.GetFieldValue<long>(reader.GetOrdinal("s404_sum"));
                    dto.Status410DeletedSum = reader.GetFieldValue<long>(reader.GetOrdinal("s410_sum"));
                    dto.BadLinkArticlesDeletedSum = reader.GetFieldValue<long>(reader.GetOrdinal("bad_sum"));
                    dto.MlArticlesDeletedSum = reader.GetFieldValue<long>(reader.GetOrdinal("ml_sum"));
                    dto.NewsArticlesDeletedSum = reader.GetFieldValue<long>(reader.GetOrdinal("news_sum"));
                    dto.PageCacheDeletedSum = reader.GetFieldValue<long>(reader.GetOrdinal("cache_sum"));
                    dto.TotalRowsDeletedSum = reader.GetFieldValue<long>(reader.GetOrdinal("total_sum"));
                }
            }

            // 2) Last run details (all columns)
            await using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = @"
                    SELECT
                        run_started_at,
                        run_finished_at,
                        http_articles_deleted,
                        https_articles_deleted,
                        status_404_deleted,
                        status_410_deleted,
                        bad_link_articles_deleted,
                        ml_articles_deleted,
                        news_articles_deleted,
                        page_cache_deleted,
                        total_rows_deleted
                    FROM watchdog_cleanup_log
                    ORDER BY id DESC
                    LIMIT 1;
                ";

                await using var r2 = await cmd2.ExecuteReaderAsync(ct);
                if (await r2.ReadAsync(ct))
                {
                    dto.LastRunStartedAt = r2.GetFieldValue<DateTime>(r2.GetOrdinal("run_started_at"));
                    dto.LastRunFinishedAt = r2.GetFieldValue<DateTime>(r2.GetOrdinal("run_finished_at"));

                    dto.LastHttpArticlesDeleted =
                        r2.GetFieldValue<int>(r2.GetOrdinal("http_articles_deleted"));
                    dto.LastHttpsArticlesDeleted =
                        r2.GetFieldValue<int>(r2.GetOrdinal("https_articles_deleted"));
                    dto.LastStatus404Deleted =
                        r2.GetFieldValue<int>(r2.GetOrdinal("status_404_deleted"));
                    dto.LastStatus410Deleted =
                        r2.GetFieldValue<int>(r2.GetOrdinal("status_410_deleted"));
                    dto.LastBadLinkArticlesDeleted =
                        r2.GetFieldValue<int>(r2.GetOrdinal("bad_link_articles_deleted"));
                    dto.LastMlArticlesDeleted =
                        r2.GetFieldValue<int>(r2.GetOrdinal("ml_articles_deleted"));
                    dto.LastNewsArticlesDeleted =
                        r2.GetFieldValue<int>(r2.GetOrdinal("news_articles_deleted"));
                    dto.LastPageCacheDeleted =
                        r2.GetFieldValue<int>(r2.GetOrdinal("page_cache_deleted"));
                    dto.LastTotalRowsDeleted =
                        r2.GetFieldValue<int>(r2.GetOrdinal("total_rows_deleted"));
                }
            }

            return dto;
        }
        finally
        {
            if (shouldClose && conn.State == ConnectionState.Open)
                await conn.CloseAsync();
        }
    }

    // Main endpoint used by the React module
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        // sequential to avoid DbContext concurrency issues
        var appHealth = await ProbeAsync(_appDb, "AppDb", ct);
        var alphaHealth = await ProbeAsync(_alphaDb, "NvdaAlpha", ct);
        var tradingHealth = await ProbeAsync(_tradingDb, "NvdaTrading / 3313", ct);

        var uptime = nowUtc - ProcessStartedUtc;
        var payload = new
        {
            serverUtc = nowUtc,
            uptimeSeconds = Math.Round(uptime.TotalSeconds),
            uptimeHuman = $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s",
            appDb = appHealth,
            alphaDb = alphaHealth,
            tradingDb = tradingHealth
        };

        return Ok(payload);
    }

    // New endpoint: watchdog_cleanup_log stats
    [HttpGet("cleanup")]
    public async Task<IActionResult> Cleanup(CancellationToken ct)
    {
        var stats = await LoadCleanupStatsAsync(ct);
        return Ok(stats);
    }

    // Tiny ping endpoint (optionally for external probes), unauthenticated
    [AllowAnonymous]
    [HttpGet("ping")]
    public IActionResult Ping()
    {
        return Ok(new
        {
            ok = true,
            serverUtc = DateTime.UtcNow,
            startedUtc = ProcessStartedUtc
        });
    }
}
