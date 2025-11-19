using Microsoft.EntityFrameworkCore;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace honey_badger_api.Data;

/// <summary>
/// Third DB: candle-level trading lab (trading_candles MySQL).
/// Mirrors the Python NVDA-candle-trading schema:
///   symbols, timeframes, workers, candles, candle_features, trades, worker_stats, trading_settings
/// plus api_providers + api_keys for rate-limited data providers.
/// </summary>
public sealed class NvdaTradingDbContext : DbContext
{
    public NvdaTradingDbContext(DbContextOptions<NvdaTradingDbContext> options) : base(options) { }

    public DbSet<NvdaTradingSymbol> Symbols => Set<NvdaTradingSymbol>();
    public DbSet<NvdaTradingTimeframe> Timeframes => Set<NvdaTradingTimeframe>();
    public DbSet<NvdaTradingWorker> Workers => Set<NvdaTradingWorker>();
    public DbSet<NvdaTradingCandle> Candles => Set<NvdaTradingCandle>();
    public DbSet<NvdaTradingCandleFeatures> CandleFeatures => Set<NvdaTradingCandleFeatures>();
    public DbSet<NvdaTradingTrade> Trades => Set<NvdaTradingTrade>();
    public DbSet<NvdaTradingWorkerStats> WorkerStats => Set<NvdaTradingWorkerStats>();
    public DbSet<NvdaTradingSettings> TradingSettings => Set<NvdaTradingSettings>();

    // NEW: API providers + keys for the admin trading infra panel
    public DbSet<ApiProvider> ApiProviders => Set<ApiProvider>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Unique symbol ticker
        b.Entity<NvdaTradingSymbol>()
            .HasIndex(x => x.Symbol)
            .IsUnique();

        // Unique timeframe code (e.g. "1m", "5m")
        b.Entity<NvdaTradingTimeframe>()
            .HasIndex(x => x.Code)
            .IsUnique();

        // Unique worker names
        b.Entity<NvdaTradingWorker>()
            .HasIndex(x => x.Name)
            .IsUnique();

        // Candle uniqueness: (symbol_id, timeframe_id, open_time)
        b.Entity<NvdaTradingCandle>()
            .HasIndex(x => new { x.SymbolId, x.TimeframeId, x.OpenTime })
            .IsUnique();

        // Fast lookup: features by candle
        b.Entity<NvdaTradingCandleFeatures>()
            .HasIndex(x => x.CandleId)
            .IsUnique();

        // Trades: filter by worker + time  (matches ix_trades_worker_time on worker_id + trade_time)
        b.Entity<NvdaTradingTrade>()
            .HasIndex(x => new { x.WorkerId, x.TradeTimeUtc });

        // Worker stats: latest snapshot per worker (worker_id + timestamp)
        b.Entity<NvdaTradingWorkerStats>()
            .HasIndex(x => new { x.WorkerId, x.SnapshotUtc });

        // Trading settings: single global row for now
        b.Entity<NvdaTradingSettings>()
            .HasIndex(x => x.Id)
            .IsUnique();

        // ---------- NEW: api_providers / api_keys indexes ----------

        // Unique provider code (e.g. "alpha_vantage", "twelvedata")
        b.Entity<ApiProvider>()
            .HasIndex(x => x.Code)
            .IsUnique();

        // Keys: unique per (provider_id, api_key)
        b.Entity<ApiKey>()
            .HasIndex(x => new { x.ProviderId, x.apiKey })
            .IsUnique();

        b.Entity<ApiKey>()
            .HasIndex(x => x.ProviderId);

        // Helpful for finding currently rate-limited keys
        b.Entity<ApiKey>()
            .HasIndex(x => x.NextAvailableAt);

        b.Entity<ApiKey>()
            .HasIndex(x => x.IpNextAvailableAt);

        // FK provider_id -> api_providers.id (no nav properties needed)
        b.Entity<ApiKey>()
            .HasOne<ApiProvider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId);
    }
}
public sealed record ApiKeyIpHistoryDto(
    int HistoryId,
    int ApiKeyId,
    string ProviderCode,
    string? KeyLabel,
    string ApiKey,
    bool IsActive,
    string IpAddress,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    bool IpBurned,
    DateTime? IpRateLimitedAt,
    DateTime? IpNextAvailableAt,
    int? DailyQuota,
    int? PerMinuteQuota,
    int CallsToday
);

// =============== ENTITIES (snake_case mappings) ===============

[Table("symbols")]
public sealed class NvdaTradingSymbol
{
    [Key, Column("id")] public int Id { get; set; }

    /// <summary>e.g. "NVDA", "MSFT", "AMD"</summary>
    [Column("symbol")] public string Symbol { get; set; } = "";

    [Column("name")] public string? Name { get; set; }

    /// <summary>Exchange short code, e.g. "NASDAQ", "NYSE", "XNAS"</summary>
    [Column("exchange")] public string? Exchange { get; set; }

    [Column("is_active")] public bool IsActive { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("timeframes")]
public sealed class NvdaTradingTimeframe
{
    [Key, Column("id")] public int Id { get; set; }

    /// <summary>Human code, e.g. "1m", "5m", "15m", "1h"</summary>
    [Column("code")] public string Code { get; set; } = "";

    /// <summary>Number of minutes for this timeframe (1, 5, 15, 60, ...)</summary>
    [Column("minutes")] public int Minutes { get; set; }

    // NOTE: DDL doesn't have created_at on timeframes, so no property here.
}

[Table("workers")]
public sealed class NvdaTradingWorker
{
    [Key, Column("id")] public int Id { get; set; }

    /// <summary>Human-friendly worker name, e.g. "Worker_1"</summary>
    [Column("name")] public string Name { get; set; } = "";

    /// <summary>Strategy identifier, e.g. "TrendFollower"</summary>
    [Column("strategy_name")] public string StrategyName { get; set; } = "";

    [Column("description")] public string? Description { get; set; }

    /// <summary>Initial capital allocated to this worker (per run), e.g. 50 EUR</summary>
    [Column("initial_capital")] public double InitialCapital { get; set; }

    /// <summary>
    /// Optional link to ASP.NET Identity user (AppUser.Id as string).
    /// Null => system/global worker.
    /// </summary>
    [Column("owner_user_id")] public string? OwnerUserId { get; set; }

    [Column("is_active")] public bool IsActive { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("candles")]
public sealed class NvdaTradingCandle
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("symbol_id")] public int SymbolId { get; set; }
    [Column("timeframe_id")] public int TimeframeId { get; set; }

    /// <summary>Candle open time (UTC, but stored as naive in MySQL)</summary>
    [Column("open_time")] public DateTime OpenTime { get; set; }

    [Column("close_time")] public DateTime? CloseTime { get; set; }

    [Column("open")] public double Open { get; set; }
    [Column("high")] public double High { get; set; }
    [Column("low")] public double Low { get; set; }
    [Column("close")] public double Close { get; set; }
    [Column("volume")] public double? Volume { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("candle_features")]
public sealed class NvdaTradingCandleFeatures
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("candle_id")] public long CandleId { get; set; }

    [Column("return")] public double? Return { get; set; }
    [Column("range")] public double? Range { get; set; }
    [Column("body")] public double? Body { get; set; }
    [Column("upper_wick")] public double? UpperWick { get; set; }
    [Column("lower_wick")] public double? LowerWick { get; set; }
    [Column("upper_wick_ratio")] public double? UpperWickRatio { get; set; }
    [Column("lower_wick_ratio")] public double? LowerWickRatio { get; set; }
    [Column("body_ratio")] public double? BodyRatio { get; set; }
    [Column("body_pos")] public double? BodyPos { get; set; }
    [Column("vol_rel")] public double? VolRel { get; set; }
    [Column("pos_20")] public double? Pos20 { get; set; }
    [Column("pos_50")] public double? Pos50 { get; set; }
    [Column("d_ma20")] public double? DMa20 { get; set; }
    [Column("d_ma50")] public double? DMa50 { get; set; }
    [Column("atr")] public double? Atr { get; set; }
    [Column("range_over_atr")] public double? RangeOverAtr { get; set; }

    [Column("bullish_flag")] public bool BullishFlag { get; set; }
    [Column("doji_flag")] public bool DojiFlag { get; set; }
    [Column("hammer_flag")] public bool HammerFlag { get; set; }
    [Column("shooting_star_flag")] public bool ShootingStarFlag { get; set; }

    [Column("sin_t")] public double? SinT { get; set; }
    [Column("cos_t")] public double? CosT { get; set; }
    [Column("timeframe_minutes")] public int? TimeframeMinutes { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("trades")]
public sealed class NvdaTradingTrade
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("worker_id")] public int WorkerId { get; set; }
    [Column("symbol_id")] public int SymbolId { get; set; }
    [Column("timeframe_id")] public int? TimeframeId { get; set; }
    [Column("candle_id")] public long? CandleId { get; set; }

    /// <summary>"BUY" / "SELL"</summary>
    [Column("side")] public string Side { get; set; } = "";

    [Column("quantity")] public double Quantity { get; set; }
    [Column("price")] public double Price { get; set; }

    /// <summary>Trade timestamp in DB (DATETIME, but treated as UTC in code).</summary>
    [Column("trade_time")] public DateTime TradeTimeUtc { get; set; }

    /// <summary>Optional per-trade realized PnL. Can be null if only tracked at worker level.</summary>
    [Column("realized_pnl")] public double? RealizedPnl { get; set; }

    [Column("notes")] public string? Notes { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("worker_stats")]
public sealed class NvdaTradingWorkerStats
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("worker_id")] public int WorkerId { get; set; }

    /// <summary>Snapshot timestamp (DB column `timestamp`). Treat as UTC.</summary>
    [Column("timestamp")] public DateTime SnapshotUtc { get; set; }

    [Column("equity")] public double Equity { get; set; }
    [Column("cash")] public double Cash { get; set; }
    [Column("unrealized_pnl")] public double UnrealizedPnl { get; set; }
    [Column("realized_pnl")] public double RealizedPnl { get; set; }

    /// <summary>Number of simultaneously open positions (0 or 1 in your current Python engine)</summary>
    [Column("open_positions")] public int OpenPositions { get; set; }

    [Column("total_trades")] public int TotalTrades { get; set; }
}

[Table("trading_settings")]
public sealed class NvdaTradingSettings
{
    /// <summary>Single global row (Id = 1) for now.</summary>
    [Key, Column("id")] public int Id { get; set; }

    [Column("symbol")] public string Symbol { get; set; } = "NVDA";

    [Column("timeframe_code")] public string TimeframeCode { get; set; } = "1m";

    [Column("timeframe_minutes")] public int TimeframeMinutes { get; set; } = 1;

    /// <summary>"alpha" or "finnhub"</summary>
    [Column("data_provider")] public string DataProvider { get; set; } = "alpha";

    [Column("initial_capital_per_worker")] public double InitialCapitalPerWorker { get; set; } = 50.0;

    [Column("historical_candles")] public int HistoricalCandles { get; set; } = 200;

    [Column("updated_utc")] public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// API data providers: Alpha Vantage, Twelve Data, Finnhub, etc.
/// </summary>
[Table("api_providers")]
public sealed class ApiProvider
{
    [Key, Column("id")] public int Id { get; set; }

    /// <summary>Short code, e.g. "alpha_vantage", "twelvedata".</summary>
    [Column("code")] public string Code { get; set; } = "";

    /// <summary>Human-readable provider name.</summary>
    [Column("name")] public string Name { get; set; } = "";

    /// <summary>Base URL, e.g. https://api.twelvedata.com</summary>
    [Column("base_url")] public string? BaseUrl { get; set; }

    /// <summary>Timezone identifier, e.g. "America/New_York".</summary>
    [Column("timezone")] public string? Timezone { get; set; }

    /// <summary>Default daily quota for new keys of this provider.</summary>
    [Column("daily_quota_default")] public int? DailyQuotaDefault { get; set; }

    /// <summary>Default per-minute quota for new keys of this provider.</summary>
    [Column("per_minute_quota_default")] public int? PerMinuteQuotaDefault { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

/// <summary>
/// API keys per provider with quota / rate-limit tracking.
/// </summary>
[Table("api_keys")]
public sealed class ApiKey
{
    [Key, Column("id")] public int Id { get; set; }

    [Column("provider_id")] public int ProviderId { get; set; }

    [Column("api_key")] public string apiKey { get; set; } = "";

    [Column("label")] public string? Label { get; set; }

    [Column("is_active")] public bool IsActive { get; set; }

    [Column("daily_quota")] public int? DailyQuota { get; set; }

    [Column("per_minute_quota")] public int? PerMinuteQuota { get; set; }

    [Column("calls_today")] public int CallsToday { get; set; }

    /// <summary>Date for which calls_today applies (UTC date).</summary>
    [Column("quota_date")] public DateTime? QuotaDate { get; set; }

    /// <summary>Start of the current per-minute window.</summary>
    [Column("window_started_at")] public DateTime? WindowStartedAt { get; set; }

    [Column("window_calls")] public int WindowCalls { get; set; }

    /// <summary>Key-level rate limiting timestamp.</summary>
    [Column("rate_limited_at")] public DateTime? RateLimitedAt { get; set; }

    /// <summary>When this key is allowed to make a new call again.</summary>
    [Column("next_available_at")] public DateTime? NextAvailableAt { get; set; }

    /// <summary>IP assigned to this key (enforced 1 key ↔ 1 IP).</summary>
    [Column("ip_address")] public string? IpAddress { get; set; }

    /// <summary>If true, this IP is burned and should not be reused.</summary>
    [Column("ip_burned")] public bool IpBurned { get; set; }

    [Column("ip_rate_limited_at")] public DateTime? IpRateLimitedAt { get; set; }

    [Column("ip_next_available_at")] public DateTime? IpNextAvailableAt { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }

    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}
