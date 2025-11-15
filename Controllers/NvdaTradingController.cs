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
        string symbol,
        string timeframeCode,
        int timeframeMinutes,
        string dataProvider,
        double initialCapitalPerWorker,
        int historicalCandles,
        DateTime updatedUtc
    );

    private async Task<NvdaTradingSettings> EnsureSettingsRowAsync()
    {
        // Single global row (Id = 1)
        var settings = await _tradingDb.TradingSettings
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == 1);

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
        string symbol,
        string timeframeCode,
        int timeframeMinutes,
        string dataProvider,
        double initialCapitalPerWorker,
        int historicalCandles
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
        if (string.IsNullOrWhiteSpace(req.symbol))
            return BadRequest("Symbol is required.");
        if (req.timeframeMinutes <= 0)
            return BadRequest("timeframeMinutes must be > 0.");
        if (req.initialCapitalPerWorker <= 0)
            return BadRequest("initialCapitalPerWorker must be > 0.");
        if (req.historicalCandles <= 0)
            return BadRequest("historicalCandles must be > 0.");

        var provider = (req.dataProvider ?? string.Empty).Trim().ToLowerInvariant();
        if (provider is not ("alpha" or "finnhub"))
            return BadRequest("dataProvider must be 'alpha' or 'finnhub'.");

        s.Symbol = req.symbol.Trim().ToUpperInvariant();
        s.TimeframeCode = req.timeframeCode.Trim();
        s.TimeframeMinutes = req.timeframeMinutes;
        s.DataProvider = provider;
        s.InitialCapitalPerWorker = req.initialCapitalPerWorker;
        s.HistoricalCandles = req.historicalCandles;
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
        int id,
        string name,
        string strategyName,
        double initialCapital,
        double? equity,
        double? cash,
        double? realizedPnl,
        double? unrealizedPnl,
        int? openPositions,
        int? totalTrades,
        DateTime? snapshotUtc,
        string? ownerUserId
    );

    /// <summary>
    /// Returns all workers with their latest stats snapshot (if any).
    /// This is the main "who is winning" view for your simulation screen.
    /// </summary>
    [HttpGet("workers")]
    public async Task<ActionResult<WorkerOverviewDto[]>> GetWorkersWithLatestStats()
    {
        var workers = await _tradingDb.Workers.AsNoTracking().ToListAsync();

        // Fetch stats from DB, then group in memory to avoid EF Core GroupBy translation issues.
        var stats = await _tradingDb.WorkerStats
            .AsNoTracking()
            .OrderBy(s => s.WorkerId)
            .ThenByDescending(s => s.SnapshotUtc)
            .ToListAsync();

        var latestByWorkerId = stats
            .GroupBy(s => s.WorkerId)
            .ToDictionary(
                g => g.Key,
                g => g.First()
            );

        var joined =
            from w in workers
            let stat = latestByWorkerId.GetValueOrDefault(w.Id)
            select new WorkerOverviewDto(
                w.Id,
                w.Name,
                w.StrategyName,
                w.InitialCapital,
                stat?.Equity,
                stat?.Cash,
                stat?.RealizedPnl,
                stat?.UnrealizedPnl,
                stat?.OpenPositions,
                stat?.TotalTrades,
                stat?.SnapshotUtc,
                w.OwnerUserId
            );

        return Ok(joined.ToArray());
    }

    public sealed record WorkerEquityPointDto(DateTime snapshotUtc, double equity);

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
        string exchange,
        bool isOpenNow,
        DateTime nowUtc,
        DateTime nowLjubljana,
        DateTime currentSessionOpenUtc,
        DateTime currentSessionCloseUtc,
        DateTime currentSessionOpenLjubljana,
        DateTime currentSessionCloseLjubljana,
        DateTime nextSessionOpenUtc,
        DateTime nextSessionOpenLjubljana
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
            exchange,
            isOpenNow,
            nowUtc,
            nowLj,
            sessionOpenUtc,
            sessionCloseUtc,
            currentOpenLj,
            currentCloseLj,
            nextSessionOpenUtc,
            nextOpenLj
        );

        return Ok(dto);
    }

    // -----------------------------------------------------------------------
    // 4) CANDLES + FEATURES (for charts)
    // -----------------------------------------------------------------------

    public sealed record CandleWithFeaturesDto(
        DateTime openTimeUtc,
        double open,
        double high,
        double low,
        double close,
        double? volume,
        double? range,
        double? body,
        double? upperWick,
        double? lowerWick,
        double? bodyRatio,
        double? bodyPos,
        double? pos20,
        double? pos50,
        bool bullish,
        bool doji,
        bool hammer,
        bool shootingStar
    );

    /// <summary>
    /// Recent candles (OHLCV) plus (optionally) a subset of features from `candle_features`
    /// for the currently configured SYMBOL + TIMEFRAME.
    /// Right now we just return basic OHLCV + a crude bullish flag.
    /// </summary>
    [HttpGet("candles")]
    public async Task<ActionResult<CandleWithFeaturesDto[]>> GetRecentCandles(
        [FromQuery] int limit = 200)
    {
        if (limit <= 0) limit = 200;
        if (limit > 1000) limit = 1000;

        var settings = await EnsureSettingsRowAsync();

        var symbol = await _tradingDb.Symbols
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Symbol == settings.Symbol);

        if (symbol == null)
            return Ok(Array.Empty<CandleWithFeaturesDto>());

        var timeframe = await _tradingDb.Timeframes
            .AsNoTracking()
            .FirstOrDefaultAsync(tf => tf.Code == settings.TimeframeCode);

        if (timeframe == null)
            return Ok(Array.Empty<CandleWithFeaturesDto>());

        // Use the candle entity directly; no navs are configured here.
        var rawCandles = await _tradingDb.Candles
            .AsNoTracking()
            .Where(c => c.SymbolId == symbol.Id && c.TimeframeId == timeframe.Id)
            .OrderByDescending(c => c.OpenTime)
            .Take(limit)
            .ToListAsync();

        rawCandles.Reverse(); // oldest -> newest

        var dtos = rawCandles
            .Select(c =>
            {
                bool bullish = c.Close >= c.Open;

                return new CandleWithFeaturesDto(
                    c.OpenTime,   // openTimeUtc in DTO
                    c.Open,
                    c.High,
                    c.Low,
                    c.Close,
                    c.Volume,
                    null, // range
                    null, // body
                    null, // upperWick
                    null, // lowerWick
                    null, // bodyRatio
                    null, // bodyPos
                    null, // pos20
                    null, // pos50
                    bullish,
                    false,
                    false,
                    false
                );
            })
            .ToArray();

        return Ok(dtos);
    }

    // -----------------------------------------------------------------------
    // 5) TRADES (recent fills / log)
    // -----------------------------------------------------------------------

    public sealed record TradeDto(
        long id,
        int workerId,
        string workerName,
        string symbol,
        string timeframeCode,
        DateTime tradeTimeUtc,
        string side,
        double quantity,
        double price,
        double? realizedPnl,
        string? notes
    );

    /// <summary>
    /// Recent trades, optionally filtered by worker.
    /// Example:
    ///   /api/nvda-trading/trades?limit=50
    ///   /api/nvda-trading/trades?workerId=3&limit=20
    /// </summary>
    [HttpGet("trades")]
    public async Task<ActionResult<TradeDto[]>> GetRecentTrades(
        [FromQuery] int? workerId = null,
        [FromQuery] int limit = 100)
    {
        if (limit <= 0) limit = 100;
        if (limit > 500) limit = 500;

        var baseQuery = _tradingDb.Trades
            .AsNoTracking()
            .AsQueryable();

        if (workerId.HasValue)
        {
            baseQuery = baseQuery.Where(t => t.WorkerId == workerId.Value);
        }

        // Join via FKs to avoid any nav assumptions
        var joined =
            from t in baseQuery
            join w in _tradingDb.Workers.AsNoTracking() on t.WorkerId equals w.Id
            join s in _tradingDb.Symbols.AsNoTracking() on t.SymbolId equals s.Id
            join tf in _tradingDb.Timeframes.AsNoTracking() on t.TimeframeId equals tf.Id into tfJoin
            from tf in tfJoin.DefaultIfEmpty()
            select new { t, w, s, tf };

        var trades = await joined
            .OrderByDescending(x => x.t.TradeTimeUtc)
            .Take(limit)
            .Select(x => new TradeDto(
                x.t.Id,
                x.t.WorkerId,
                x.w.Name,
                x.s.Symbol,
                x.tf != null ? x.tf.Code : "",
                x.t.TradeTimeUtc,
                x.t.Side,
                x.t.Quantity,
                x.t.Price,
                x.t.RealizedPnl,
                x.t.Notes
            ))
            .ToListAsync();

        return Ok(trades);
    }
}
