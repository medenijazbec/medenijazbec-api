using honey_badger_api.Data;
using Mailjet.Client.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

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
    //    (single global row, Id=1)
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
        if (provider is not ("alpha" or "finnhub" or "twelvedata"))
            return BadRequest("dataProvider must be 'alpha', 'finnhub' or 'twelvedata'.");

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
    // 1b) ADMIN: MULTI-ROW TRADING_SETTINGS (symbols / TFs / providers)
    //      Routes are rooted as `/api/trading/...` to match the React admin
    //      panel, but implemented in this controller.
    // -----------------------------------------------------------------------

    public sealed record TradingSettingRowDto(
        int id,
        string symbol,
        string timeframeCode,
        int timeframeMinutes,
        string dataProvider,
        double initialCapitalPerWorker,
        int historicalCandles,
        DateTime? updatedUtc
    );

    public sealed record CreateTradingSettingRequest(
        string symbol,
        string timeframeCode,
        int timeframeMinutes,
        string dataProvider,
        double initialCapitalPerWorker,
        int historicalCandles
    );

    /// <summary>
    /// List all rows in trading_settings (admin view).
    /// React: GET /api/trading/settings
    /// </summary>
    [HttpGet("~/api/trading/settings")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TradingSettingRowDto[]>> GetTradingSettingsRows()
    {
        var rows = await _tradingDb.TradingSettings
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .ToListAsync();

        var dtos = rows
            .Select(s => new TradingSettingRowDto(
                s.Id,
                s.Symbol,
                s.TimeframeCode,
                s.TimeframeMinutes,
                s.DataProvider,
                s.InitialCapitalPerWorker,
                s.HistoricalCandles,
                s.UpdatedUtc
            ))
            .ToArray();

        return Ok(dtos);
    }

    /// <summary>
    /// Create a new trading_settings row (symbol / timeframe / provider).
    /// React: POST /api/trading/settings
    /// </summary>
    [HttpPost("~/api/trading/settings")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TradingSettingRowDto>> CreateTradingSetting(
        [FromBody] CreateTradingSettingRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.symbol))
            return BadRequest("Symbol is required.");
        if (string.IsNullOrWhiteSpace(req.timeframeCode))
            return BadRequest("timeframeCode is required.");
        if (req.timeframeMinutes <= 0)
            return BadRequest("timeframeMinutes must be > 0.");
        if (req.historicalCandles <= 0)
            return BadRequest("historicalCandles must be > 0.");

        var provider = (req.dataProvider ?? string.Empty).Trim().ToLowerInvariant();
        if (provider is not ("alpha" or "finnhub" or "twelvedata"))
            return BadRequest("dataProvider must be 'alpha', 'finnhub' or 'twelvedata'.");

        var symbol = req.symbol.Trim().ToUpperInvariant();
        var tfCode = req.timeframeCode.Trim();

        // Optional: avoid exact duplicates for (symbol, timeframeCode)
        var exists = await _tradingDb.TradingSettings.AsNoTracking()
            .AnyAsync(s =>
                s.Symbol == symbol &&
                s.TimeframeCode == tfCode &&
                s.TimeframeMinutes == req.timeframeMinutes &&
                s.DataProvider == provider);

        if (exists)
            return Conflict("A trading_settings row with the same symbol/timeframe/provider already exists.");

        var now = DateTime.UtcNow;

        var entity = new NvdaTradingSettings
        {
            Symbol = symbol,
            TimeframeCode = tfCode,
            TimeframeMinutes = req.timeframeMinutes,
            DataProvider = provider,
            InitialCapitalPerWorker = req.initialCapitalPerWorker,
            HistoricalCandles = req.historicalCandles,
            UpdatedUtc = now
        };

        _tradingDb.TradingSettings.Add(entity);
        await _tradingDb.SaveChangesAsync();

        var dto = new TradingSettingRowDto(
            entity.Id,
            entity.Symbol,
            entity.TimeframeCode,
            entity.TimeframeMinutes,
            entity.DataProvider,
            entity.InitialCapitalPerWorker,
            entity.HistoricalCandles,
            entity.UpdatedUtc
        );

        return Ok(dto);
    }

    /// <summary>
    /// Delete a trading_settings row by Id.
    /// React: DELETE /api/trading/settings/{id}
    /// </summary>
    [HttpDelete("~/api/trading/settings/{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> DeleteTradingSetting(int id)
    {
        var row = await _tradingDb.TradingSettings
            .FirstOrDefaultAsync(s => s.Id == id);

        if (row == null)
            return NotFound();

        _tradingDb.TradingSettings.Remove(row);
        await _tradingDb.SaveChangesAsync();

        return NoContent();
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
    /// Recent candles (OHLCV) plus (optionally) a subset of features from `candle_features`.
    ///
    /// Supports:
    ///   - /api/nvda-trading/candles?limit=200                 (uses global settings row)
    ///   - /api/nvda-trading/candles?symbol=NVDA&timeframeCode=1m&limit=300
    /// </summary>
    [HttpGet("candles")]
    public async Task<ActionResult<CandleWithFeaturesDto[]>> GetRecentCandles(
        [FromQuery] string? symbol = null,
        [FromQuery] string? timeframeCode = null,
        [FromQuery] int limit = 200)
    {
        if (limit <= 0) limit = 200;
        if (limit > 1000) limit = 1000;

        string effectiveSymbol;
        string effectiveTimeframeCode;

        if (!string.IsNullOrWhiteSpace(symbol) && !string.IsNullOrWhiteSpace(timeframeCode))
        {
            effectiveSymbol = symbol.Trim().ToUpperInvariant();
            effectiveTimeframeCode = timeframeCode.Trim();
        }
        else
        {
            var settings = await EnsureSettingsRowAsync();
            effectiveSymbol = settings.Symbol;
            effectiveTimeframeCode = settings.TimeframeCode;
        }

        var symbolEntity = await _tradingDb.Symbols
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Symbol == effectiveSymbol);

        if (symbolEntity == null)
            return Ok(Array.Empty<CandleWithFeaturesDto>());

        var timeframe = await _tradingDb.Timeframes
            .AsNoTracking()
            .FirstOrDefaultAsync(tf => tf.Code == effectiveTimeframeCode);

        if (timeframe == null)
            return Ok(Array.Empty<CandleWithFeaturesDto>());

        // Use the candle entity directly; no navs are configured here.
        var rawCandles = await _tradingDb.Candles
            .AsNoTracking()
            .Where(c => c.SymbolId == symbolEntity.Id && c.TimeframeId == timeframe.Id)
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

    // -----------------------------------------------------------------------
    // 6) ADMIN: API PROVIDERS + API KEYS (api_providers / api_keys tables)
    //      Routes are rooted at `/api/trading/...` for the React admin panel.
    // -----------------------------------------------------------------------

    public sealed record ApiProviderDto(
        int id,
        string code,
        string name,
        string? baseUrl,
        string? timezone,
        int? dailyQuotaDefault,
        int? perMinuteQuotaDefault,
        DateTime createdAt
    );

    public sealed record CreateApiProviderRequest(
        string code,
        string name,
        string? baseUrl,
        string? timezone,
        int? dailyQuotaDefault,
        int? perMinuteQuotaDefault
    );

    /// <summary>
    /// List all api_providers.
    /// React: GET /api/trading/api-providers
    /// </summary>
    [HttpGet("~/api/trading/api-providers")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiProviderDto[]>> GetApiProviders()
    {
        var providers = await _tradingDb.ApiProviders
            .AsNoTracking()
            .OrderBy(p => p.Id)
            .ToListAsync();

        var dtos = providers
            .Select(p => new ApiProviderDto(
                p.Id,
                p.Code,
                p.Name,
                p.BaseUrl,
                p.Timezone,
                p.DailyQuotaDefault,
                p.PerMinuteQuotaDefault,
                p.CreatedAt
            ))
            .ToArray();

        return Ok(dtos);
    }

    /// <summary>
    /// Create a new api_providers row.
    /// React: POST /api/trading/api-providers
    /// </summary>
    [HttpPost("~/api/trading/api-providers")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiProviderDto>> CreateApiProvider(
        [FromBody] CreateApiProviderRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.code))
            return BadRequest("code is required.");
        if (string.IsNullOrWhiteSpace(req.name))
            return BadRequest("name is required.");

        var code = req.code.Trim().ToLowerInvariant();
        var name = req.name.Trim();

        var exists = await _tradingDb.ApiProviders.AsNoTracking()
            .AnyAsync(p => p.Code == code);

        if (exists)
            return Conflict("An API provider with this code already exists.");

        var now = DateTime.UtcNow;

        var entity = new ApiProvider
        {
            Code = code,
            Name = name,
            BaseUrl = string.IsNullOrWhiteSpace(req.baseUrl) ? null : req.baseUrl.Trim(),
            Timezone = string.IsNullOrWhiteSpace(req.timezone) ? "UTC" : req.timezone.Trim(),
            DailyQuotaDefault = req.dailyQuotaDefault,
            PerMinuteQuotaDefault = req.perMinuteQuotaDefault,
            CreatedAt = now
        };

        _tradingDb.ApiProviders.Add(entity);
        await _tradingDb.SaveChangesAsync();

        var dto = new ApiProviderDto(
            entity.Id,
            entity.Code,
            entity.Name,
            entity.BaseUrl,
            entity.Timezone,
            entity.DailyQuotaDefault,
            entity.PerMinuteQuotaDefault,
            entity.CreatedAt
        );

        return Ok(dto);
    }

    public sealed record ApiKeyDto(
        int id,
        int providerId,
        string? providerCode,
        string apiKey,
        string? label,
        bool isActive,
        int? dailyQuota,
        int? perMinuteQuota,
        int callsToday,
        DateTime? quotaDate,
        DateTime? windowStartedAt,
        int windowCalls,
        DateTime? rateLimitedAt,
        DateTime? nextAvailableAt,
        string? ipAddress,
        bool ipBurned,
        DateTime? ipRateLimitedAt,
        DateTime? ipNextAvailableAt,
        DateTime createdAt,
        DateTime updatedAt
    );

    public sealed record CreateApiKeyRequest(
        int providerId,
        string apiKey,
        string? label,
        bool isActive,
        int? dailyQuota,
        int? perMinuteQuota
    );

    /// <summary>
    /// List all api_keys with provider code attached.
    /// React: GET /api/trading/api-keys
    /// </summary>
    [HttpGet("~/api/trading/api-keys")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiKeyDto[]>> GetApiKeys()
    {
        var query =
            from k in _tradingDb.ApiKeys.AsNoTracking()
            join p in _tradingDb.ApiProviders.AsNoTracking()
                on k.ProviderId equals p.Id into pJoin
            from p in pJoin.DefaultIfEmpty()
            orderby k.Id
            select new { k, ProviderCode = p != null ? p.Code : null };

        var rows = await query.ToListAsync();

        var dtos = rows
            .Select(x => new ApiKeyDto(
                x.k.Id,
                x.k.ProviderId,
                x.ProviderCode,
                x.k.apiKey,
                x.k.Label,
                x.k.IsActive,
                x.k.DailyQuota,
                x.k.PerMinuteQuota,
                x.k.CallsToday,
                x.k.QuotaDate,
                x.k.WindowStartedAt,
                x.k.WindowCalls,
                x.k.RateLimitedAt,
                x.k.NextAvailableAt,
                x.k.IpAddress,
                x.k.IpBurned,
                x.k.IpRateLimitedAt,
                x.k.IpNextAvailableAt,
                x.k.CreatedAt,
                x.k.UpdatedAt
            ))
            .ToArray();

        return Ok(dtos);
    }

    /// <summary>
    /// Create a new api_keys row.
    /// React: POST /api/trading/api-keys
    /// </summary>
    [HttpPost("~/api/trading/api-keys")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiKeyDto>> CreateApiKey(
        [FromBody] CreateApiKeyRequest req)
    {
        if (req.providerId <= 0)
            return BadRequest("providerId is required.");
        if (string.IsNullOrWhiteSpace(req.apiKey))
            return BadRequest("apiKey is required.");

        var provider = await _tradingDb.ApiProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == req.providerId);

        if (provider == null)
            return BadRequest("Unknown providerId.");

        var keyValue = req.apiKey.Trim();

        var exists = await _tradingDb.ApiKeys.AsNoTracking()
            .AnyAsync(k => k.apiKey == keyValue && k.ProviderId == req.providerId);

        if (exists)
            return Conflict("This API key already exists for the selected provider.");

        var now = DateTime.UtcNow;

        var entity = new ApiKey
        {
            ProviderId = req.providerId,
            apiKey = keyValue,
            Label = string.IsNullOrWhiteSpace(req.label) ? null : req.label.Trim(),
            IsActive = req.isActive,
            DailyQuota = req.dailyQuota,
            PerMinuteQuota = req.perMinuteQuota,
            CallsToday = 0,
            QuotaDate = null,
            WindowStartedAt = null,
            WindowCalls = 0,
            RateLimitedAt = null,
            NextAvailableAt = null,
            IpAddress = null,
            IpBurned = false,
            IpRateLimitedAt = null,
            IpNextAvailableAt = null,
            CreatedAt = now,
            UpdatedAt = now
        };

        _tradingDb.ApiKeys.Add(entity);
        await _tradingDb.SaveChangesAsync();

        var dto = new ApiKeyDto(
            entity.Id,
            entity.ProviderId,
            provider.Code,
            entity.apiKey,
            entity.Label,
            entity.IsActive,
            entity.DailyQuota,
            entity.PerMinuteQuota,
            entity.CallsToday,
            entity.QuotaDate,
            entity.WindowStartedAt,
            entity.WindowCalls,
            entity.RateLimitedAt,
            entity.NextAvailableAt,
            entity.IpAddress,
            entity.IpBurned,
            entity.IpRateLimitedAt,
            entity.IpNextAvailableAt,
            entity.CreatedAt,
            entity.UpdatedAt
        );

        return Ok(dto);
    }

    // -----------------------------------------------------------------------
    // 7) ADMIN: TRADING INFRA STATS (symbols / keys / workers)
    // -----------------------------------------------------------------------

    public sealed record TradingInfraSymbolDto(
        int id,
        string symbol,
        string timeframeCode,
        int timeframeMinutes,
        string dataProvider,
        int historicalCandles,
        DateTime? updatedUtc
    );

    public sealed record RateLimitedKeyDto(
        int id,
        string? providerCode,
        string? label,
        DateTime? nextAvailableAt,
        DateTime? ipNextAvailableAt,
        int? secondsUntilKeyAvailable,
        int? secondsUntilIpAvailable
    );

    public sealed record TradingInfraStatsDto(
        DateTime generatedAtUtc,
        TradingInfraSymbolDto[] symbols,
        int totalWorkers,
        int activeWorkers,
        int totalApiProviders,
        int totalApiKeys,
        int activeApiKeys,
        int keysCurrentlyRateLimited,
        RateLimitedKeyDto[] rateLimitedKeys
    );

    /// <summary>
    /// Summary stats for trading infra: configured symbols, workers, API keys,
    /// and which keys are currently rate-limited (with remaining cooldown).
    ///
    /// React can hit: GET /api/trading/infra-stats
    /// </summary>
    [HttpGet("~/api/trading/infra-stats")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TradingInfraStatsDto>> GetTradingInfraStats()
    {
        var nowUtc = DateTime.UtcNow;

        // All trading_settings rows (symbols being fetched by Python/Tor workers)
        var settingsRows = await _tradingDb.TradingSettings
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .ToListAsync();

        var symbols = settingsRows
            .Select(s => new TradingInfraSymbolDto(
                s.Id,
                s.Symbol,
                s.TimeframeCode,
                s.TimeframeMinutes,
                s.DataProvider,
                s.HistoricalCandles,
                s.UpdatedUtc
            ))
            .ToArray();

        // Workers
        var workers = await _tradingDb.Workers.AsNoTracking().ToListAsync();
        int totalWorkers = workers.Count;

        // "Active" workers = any with a stats snapshot in the last 10 minutes
        var activeCutoff = nowUtc.AddMinutes(-10);
        var activeWorkerIds = await _tradingDb.WorkerStats.AsNoTracking()
            .Where(ws => ws.SnapshotUtc >= activeCutoff)
            .Select(ws => ws.WorkerId)
            .Distinct()
            .ToListAsync();
        int activeWorkers = activeWorkerIds.Count;

        // Providers
        var providers = await _tradingDb.ApiProviders.AsNoTracking().ToListAsync();
        int totalApiProviders = providers.Count;

        // Keys + provider code
        var keyQuery =
            from k in _tradingDb.ApiKeys.AsNoTracking()
            join p in _tradingDb.ApiProviders.AsNoTracking()
                on k.ProviderId equals p.Id into pJoin
            from p in pJoin.DefaultIfEmpty()
            select new { k, ProviderCode = p != null ? p.Code : null };

        var keyRows = await keyQuery.ToListAsync();
        int totalApiKeys = keyRows.Count;
        int activeApiKeys = keyRows.Count(x => x.k.IsActive);

        // Keys currently rate-limited (key-level or IP-level)
        var limitedKeys = new List<RateLimitedKeyDto>();

        foreach (var x in keyRows)
        {
            var key = x.k;
            DateTime? keyNext = key.NextAvailableAt;
            DateTime? ipNext = key.IpNextAvailableAt;

            bool isLimited =
                (keyNext.HasValue && keyNext.Value > nowUtc) ||
                (ipNext.HasValue && ipNext.Value > nowUtc);

            if (!isLimited)
                continue;

            int? keySeconds = null;
            if (keyNext.HasValue && keyNext.Value > nowUtc)
            {
                keySeconds = (int)Math.Max(0, (keyNext.Value - nowUtc).TotalSeconds);
            }

            int? ipSeconds = null;
            if (ipNext.HasValue && ipNext.Value > nowUtc)
            {
                ipSeconds = (int)Math.Max(0, (ipNext.Value - nowUtc).TotalSeconds);
            }

            limitedKeys.Add(new RateLimitedKeyDto(
                key.Id,
                x.ProviderCode,
                key.Label,
                keyNext,
                ipNext,
                keySeconds,
                ipSeconds
            ));
        }

        var dto = new TradingInfraStatsDto(
            nowUtc,
            symbols,
            totalWorkers,
            activeWorkers,
            totalApiProviders,
            totalApiKeys,
            activeApiKeys,
            limitedKeys.Count,
            limitedKeys.ToArray()
        );

        return Ok(dto);
    }

    /// <summary>
    /// IP ↔ API key history report, backed by v_api_key_ip_history.
    /// Shows which IPs each key has ever used and when they were last seen.
    /// React: GET /api/trading/api-key-ip-history
    /// </summary>
    [HttpGet("~/api/trading/api-key-ip-history")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiKeyIpHistoryDto[]>> GetApiKeyIpHistory()
    {
        const string sql = @"
        SELECT
          history_id,
          api_key_id,
          provider_code,
          key_label,
          api_key,
          is_active,
          ip_address,
          first_seen_at,
          last_seen_at,
          ip_burned,
          ip_rate_limited_at,
          ip_next_available_at,
          daily_quota,
          per_minute_quota,
          calls_today
        FROM v_api_key_ip_history
        ORDER BY api_key_id, first_seen_at;
    ";

        var results = new List<ApiKeyIpHistoryDto>();

        var conn = _tradingDb.Database.GetDbConnection();

        try
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var dto = new ApiKeyIpHistoryDto(
                    HistoryId: reader.GetInt32(0),
                    ApiKeyId: reader.GetInt32(1),
                    ProviderCode: reader.GetString(2),
                    KeyLabel: reader.IsDBNull(3) ? null : reader.GetString(3),
                    ApiKey: reader.GetString(4),
                    IsActive: reader.GetBoolean(5),
                    IpAddress: reader.GetString(6),
                    FirstSeenAt: reader.GetDateTime(7),
                    LastSeenAt: reader.GetDateTime(8),
                    IpBurned: reader.GetBoolean(9),
                    IpRateLimitedAt: reader.IsDBNull(10)
                        ? (DateTime?)null
                        : reader.GetDateTime(10),
                    IpNextAvailableAt: reader.IsDBNull(11)
                        ? (DateTime?)null
                        : reader.GetDateTime(11),
                    DailyQuota: reader.IsDBNull(12) ? (int?)null : reader.GetInt32(12),
                    PerMinuteQuota: reader.IsDBNull(13) ? (int?)null : reader.GetInt32(13),
                    CallsToday: reader.GetInt32(14)
                );

                results.Add(dto);
            }
        }
        finally
        {
            if (conn.State == ConnectionState.Open)
                await conn.CloseAsync();
        }

        return Ok(results.ToArray());
    }


}
