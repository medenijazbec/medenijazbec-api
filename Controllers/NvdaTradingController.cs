using System;
using System.Linq;
using System.Threading.Tasks;
using honey_badger_api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Controllers;

[ApiController]
[Route("api/nvda-trading")]
[Authorize] // require logged-in user; tighten with Roles="Admin" where needed
public sealed class NvdaTradingController : ControllerBase
{
    private readonly NvdaTradingDbContext _tradingDb;

    public NvdaTradingController(NvdaTradingDbContext tradingDb)
    {
        _tradingDb = tradingDb;
    }

    // -----------------------------------------------------------------------
    // 1) SETTINGS: SYMBOL / TIMEFRAME / PROVIDER / CAPITAL / HISTORY
    // -----------------------------------------------------------------------

    public sealed record TradingSettingsDto(
        string Symbol,
        string TimeframeCode,
        int TimeframeMinutes,
        string DataProvider,
        double InitialCapitalPerWorker,
        int HistoricalCandles,
        DateTime UpdatedUtc
    );

    private async Task<NvdaTradingSettings> EnsureSettingsRowAsync()
    {
        // Single global row (Id = 1)
        var settings = await _tradingDb.TradingSettings.AsTracking().FirstOrDefaultAsync(s => s.Id == 1);
        if (settings != null) return settings;

        settings = new NvdaTradingSettings
        {
            Id = 1,
            Symbol = "NVDA",
            TimeframeCode = "1m",
            TimeframeMinutes = 1,
            DataProvider = "alpha",
            InitialCapitalPerWorker = 50.0,
            HistoricalCandles = 200,
            UpdatedUtc = DateTime.UtcNow
        };

        _tradingDb.TradingSettings.Add(settings);
        await _tradingDb.SaveChangesAsync();
        return settings;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<TradingSettingsDto>> GetSettings()
    {
        var s = await EnsureSettingsRowAsync();
        return Ok(new TradingSettingsDto(
            s.Symbol,
            s.TimeframeCode,
            s.TimeframeMinutes,
            s.DataProvider,
            s.InitialCapitalPerWorker,
            s.HistoricalCandles,
            s.UpdatedUtc
        ));
    }

    public sealed record UpdateTradingSettingsRequest(
        string Symbol,
        string TimeframeCode,
        int TimeframeMinutes,
        string DataProvider,
        double InitialCapitalPerWorker,
        int HistoricalCandles
    );

    /// <summary>
    /// Update global trading settings (e.g. change NVDA -> MSFT, 1m -> 5m, capital per worker, etc.).
    /// Frontend: call this when user changes switches; Python side can then read this table.
    /// </summary>
    [HttpPost("settings")]
    [Authorize(Roles = "Admin")] // only admin can change global switches
    public async Task<ActionResult<TradingSettingsDto>> UpdateSettings([FromBody] UpdateTradingSettingsRequest req)
    {
        var s = await EnsureSettingsRowAsync();

        // basic validation
        if (string.IsNullOrWhiteSpace(req.Symbol))
            return BadRequest("Symbol is required.");
        if (req.TimeframeMinutes <= 0)
            return BadRequest("TimeframeMinutes must be > 0.");
        if (req.InitialCapitalPerWorker <= 0)
            return BadRequest("InitialCapitalPerWorker must be > 0.");
        if (req.HistoricalCandles <= 0)
            return BadRequest("HistoricalCandles must be > 0.");

        s.Symbol = req.Symbol.Trim().ToUpperInvariant();
        s.TimeframeCode = req.TimeframeCode.Trim();
        s.TimeframeMinutes = req.TimeframeMinutes;
        s.DataProvider = req.DataProvider.Trim().ToLowerInvariant(); // "alpha" or "finnhub"
        s.InitialCapitalPerWorker = req.InitialCapitalPerWorker;
        s.HistoricalCandles = req.HistoricalCandles;
        s.UpdatedUtc = DateTime.UtcNow;

        await _tradingDb.SaveChangesAsync();

        return Ok(new TradingSettingsDto(
            s.Symbol,
            s.TimeframeCode,
            s.TimeframeMinutes,
            s.DataProvider,
            s.InitialCapitalPerWorker,
            s.HistoricalCandles,
            s.UpdatedUtc
        ));
    }

    // -----------------------------------------------------------------------
    // 2) WORKERS: OVERVIEW + PROGRESS
    // -----------------------------------------------------------------------

    public sealed record WorkerOverviewDto(
        int Id,
        string Name,
        string StrategyName,
        double InitialCapital,
        double? Equity,
        double? Cash,
        double? RealizedPnl,
        double? UnrealizedPnl,
        int? OpenPositions,
        int? TotalTrades,
        DateTime? SnapshotUtc
    );

    /// <summary>
    /// Returns all workers with their latest stats snapshot (if any).
    /// This is the main "who is winning" view for your simulation screen.
    /// </summary>
    [HttpGet("workers")]
    public async Task<ActionResult<WorkerOverviewDto[]>> GetWorkersWithLatestStats()
    {
        var workers = await _tradingDb.Workers.AsNoTracking().ToListAsync();

        // Latest stat per worker (by SnapshotUtc)
        var latestStats = await _tradingDb.WorkerStats.AsNoTracking()
            .GroupBy(s => s.WorkerId)
            .Select(g => g.OrderByDescending(x => x.SnapshotUtc).First())
            .ToListAsync();

        var joined =
            from w in workers
            join s in latestStats on w.Id equals s.WorkerId into statsJoin
            from s in statsJoin.DefaultIfEmpty()
            select new WorkerOverviewDto(
                w.Id,
                w.Name,
                w.StrategyName,
                w.InitialCapital,
                s?.Equity,
                s?.Cash,
                s?.RealizedPnl,
                s?.UnrealizedPnl,
                s?.OpenPositions,
                s?.TotalTrades,
                s?.SnapshotUtc
            );

        return Ok(joined.ToArray());
    }

    public sealed record WorkerEquityPointDto(DateTime SnapshotUtc, double Equity);

    /// <summary>
    /// Simple equity time-series for a worker (for plotting worker's curve on frontend).
    /// Example: /api/nvda-trading/workers/1/equity?hoursBack=6
    /// </summary>
    [HttpGet("workers/{workerId:int}/equity")]
    public async Task<ActionResult<WorkerEquityPointDto[]>> GetWorkerEquityHistory(
        int workerId,
        [FromQuery] int hoursBack = 24)
    {
        if (hoursBack <= 0) hoursBack = 24;
        var sinceUtc = DateTime.UtcNow.AddHours(-hoursBack);

        var series = await _tradingDb.WorkerStats.AsNoTracking()
            .Where(s => s.WorkerId == workerId && s.SnapshotUtc >= sinceUtc)
            .OrderBy(s => s.SnapshotUtc)
            .Select(s => new WorkerEquityPointDto(s.SnapshotUtc, s.Equity))
            .ToListAsync();

        return Ok(series);
    }

    // -----------------------------------------------------------------------
    // 3) MARKET CLOCK – NYSE -> Ljubljana (DST-aware)
    // -----------------------------------------------------------------------

    public sealed record MarketClockDto(
        string Exchange,
        bool IsOpenNow,
        DateTime NowUtc,
        DateTime NowLjubljana,
        DateTime CurrentSessionOpenUtc,
        DateTime CurrentSessionCloseUtc,
        DateTime CurrentSessionOpenLjubljana,
        DateTime CurrentSessionCloseLjubljana,
        DateTime NextSessionOpenUtc,
        DateTime NextSessionOpenLjubljana
    );

    private static TimeZoneInfo TryGetTimeZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // try next
            }
        }

        // Fallback: UTC if none found
        return TimeZoneInfo.Utc;
    }

    /// <summary>
    /// Market clock for an exchange (currently NYSE/NASDAQ style),
    /// mapped to Ljubljana time while respecting DST.
    /// </summary>
    [HttpGet("market-clock")]
    [AllowAnonymous] // optional: useful for public dashboards
    public ActionResult<MarketClockDto> GetMarketClock(
        [FromQuery] string exchange = "NYSE")
    {
        // Normalize name a bit
        exchange = exchange.ToUpperInvariant();
        if (exchange is "NASDAQ" or "QQQ")
            exchange = "NASDAQ";
        else
            exchange = "NYSE";

        var nowUtc = DateTime.UtcNow;

        // Timezones:
        //   - US/Eastern for NYSE/NASDAQ
        //   - Europe/Ljubljana for your local time
        var tzEastern = TryGetTimeZone("America/New_York", "Eastern Standard Time");
        var tzLj = TryGetTimeZone("Europe/Ljubljana", "Central European Standard Time");

        DateTime easternNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tzEastern);

        // Market hours: 09:30 - 16:00 US/Eastern, Mon-Fri
        bool isWeekend = easternNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        DateTime sessionOpenLocal;
        DateTime sessionCloseLocal;

        if (!isWeekend)
        {
            sessionOpenLocal = new DateTime(
                easternNow.Year, easternNow.Month, easternNow.Day,
                9, 30, 0, DateTimeKind.Unspecified);

            sessionCloseLocal = new DateTime(
                easternNow.Year, easternNow.Month, easternNow.Day,
                16, 0, 0, DateTimeKind.Unspecified);
        }
        else
        {
            // If weekend: next session is Monday 09:30
            var daysToMonday = ((int)DayOfWeek.Monday - (int)easternNow.DayOfWeek + 7) % 7;
            if (daysToMonday == 0) daysToMonday = 7;

            var monday = easternNow.Date.AddDays(daysToMonday);

            sessionOpenLocal = new DateTime(
                monday.Year, monday.Month, monday.Day,
                9, 30, 0, DateTimeKind.Unspecified);

            sessionCloseLocal = new DateTime(
                monday.Year, monday.Month, monday.Day,
                16, 0, 0, DateTimeKind.Unspecified);
        }

        // Convert session open/close to UTC
        var sessionOpenUtc = TimeZoneInfo.ConvertTimeToUtc(sessionOpenLocal, tzEastern);
        var sessionCloseUtc = TimeZoneInfo.ConvertTimeToUtc(sessionCloseLocal, tzEastern);

        bool isOpenNow = !isWeekend && nowUtc >= sessionOpenUtc && nowUtc <= sessionCloseUtc;

        // Next session open:
        DateTime nextSessionOpenLocal;
        if (isWeekend)
        {
            // Already computed above: sessionOpenLocal
            nextSessionOpenLocal = sessionOpenLocal;
        }
        else if (nowUtc < sessionOpenUtc)
        {
            nextSessionOpenLocal = sessionOpenLocal;
        }
        else
        {
            // After today's close: next business day 09:30
            var nextBiz = easternNow.Date.AddDays(1);
            while (nextBiz.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                nextBiz = nextBiz.AddDays(1);

            nextSessionOpenLocal = new DateTime(
                nextBiz.Year, nextBiz.Month, nextBiz.Day,
                9, 30, 0, DateTimeKind.Unspecified);
        }

        var nextSessionOpenUtc = TimeZoneInfo.ConvertTimeToUtc(nextSessionOpenLocal, tzEastern);

        // Map to Ljubljana time
        var nowLj = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tzLj);
        var currentOpenLj = TimeZoneInfo.ConvertTimeFromUtc(sessionOpenUtc, tzLj);
        var currentCloseLj = TimeZoneInfo.ConvertTimeFromUtc(sessionCloseUtc, tzLj);
        var nextOpenLj = TimeZoneInfo.ConvertTimeFromUtc(nextSessionOpenUtc, tzLj);

        var dto = new MarketClockDto(
            Exchange: exchange,
            IsOpenNow: isOpenNow,
            NowUtc: nowUtc,
            NowLjubljana: nowLj,
            CurrentSessionOpenUtc: sessionOpenUtc,
            CurrentSessionCloseUtc: sessionCloseUtc,
            CurrentSessionOpenLjubljana: currentOpenLj,
            CurrentSessionCloseLjubljana: currentCloseLj,
            NextSessionOpenUtc: nextSessionOpenUtc,
            NextSessionOpenLjubljana: nextOpenLj
        );

        return Ok(dto);
    }
}
