using Microsoft.EntityFrameworkCore;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace honey_badger_api.Data;

// =============== ENTITIES (snake_case mappings) ===============

#region Core market / candles / workers

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

    /// <summary>Strategy identifier, e.g. "trend_following"</summary>
    [Column("strategy_name")] public string StrategyName { get; set; } = "";

    [Column("description")] public string? Description { get; set; }

    /// <summary>Initial capital allocated to this worker, e.g. 50 EUR</summary>
    [Column("initial_capital")] public double InitialCapital { get; set; }

    /// <summary>
    /// Optional link to an app user (string ID). Null => system/global worker.
    /// </summary>
    [Column("owner_user_id")] public string? OwnerUserId { get; set; }

    [Column("mode")] public string Mode { get; set; } = "PAPER";

    [Column("max_risk_per_trade_pct")] public double MaxRiskPerTradePct { get; set; }

    [Column("max_daily_loss_pct")] public double MaxDailyLossPct { get; set; }

    [Column("max_total_drawdown_pct")] public double MaxTotalDrawdownPct { get; set; }

    [Column("max_position_size_pct")] public double MaxPositionSizePct { get; set; }

    [Column("max_open_positions")] public int MaxOpenPositions { get; set; }

    [Column("max_trades_per_day")] public int MaxTradesPerDay { get; set; }

    [Column("circuit_breaker_loss_pct")] public double CircuitBreakerLossPct { get; set; }

    [Column("is_trading_paused")] public bool IsTradingPaused { get; set; }

    [Column("pause_reason")] public string? PauseReason { get; set; }

    [Column("last_pause_at")] public DateTime? LastPauseAt { get; set; }

    [Column("runtime_instance_id")] public string? RuntimeInstanceId { get; set; }

    [Column("last_heartbeat_at")] public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>JSON config blob based on strategy; stored as JSON in MySQL.</summary>
    [Column("strategy_config")] public string? StrategyConfigJson { get; set; }

    [Column("is_active")] public bool IsActive { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("candles")]
public sealed class NvdaTradingCandle
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("symbol_id")] public int SymbolId { get; set; }
    [Column("timeframe_id")] public int TimeframeId { get; set; }

    /// <summary>Candle open time (UTC, stored as naive in MySQL)</summary>
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

    // Link to strategy signal (nullable)
    [Column("signal_id")] public long? SignalId { get; set; }

    /// <summary>"BUY" / "SELL"</summary>
    [Column("side")] public string Side { get; set; } = "";

    [Column("quantity")] public double Quantity { get; set; }
    [Column("price")] public double Price { get; set; }

    // Optional per-trade stop/take-profit
    [Column("stop_loss")] public double? StopLoss { get; set; }

    [Column("take_profit")] public double? TakeProfit { get; set; }

    /// <summary>Trade timestamp in DB (DATETIME, treated as UTC in code).</summary>
    [Column("trade_time")] public DateTime TradeTimeUtc { get; set; }

    /// <summary>Optional per-trade realized PnL.</summary>
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

    /// <summary>Number of simultaneously open positions.</summary>
    [Column("open_positions")] public int OpenPositions { get; set; }

    [Column("total_trades")] public int TotalTrades { get; set; }

    [Column("gross_exposure")] public double GrossExposure { get; set; }
    [Column("net_exposure")] public double NetExposure { get; set; }
    [Column("long_exposure")] public double LongExposure { get; set; }
    [Column("short_exposure")] public double ShortExposure { get; set; }
    [Column("drawdown_pct")] public double DrawdownPct { get; set; }
    [Column("max_drawdown_pct")] public double MaxDrawdownPct { get; set; }
    [Column("daily_realized_pnl")] public double DailyRealizedPnl { get; set; }
    [Column("rolling_sharpe_30d")] public double? RollingSharpe30d { get; set; }
    [Column("rolling_sortino_30d")] public double? RollingSortino30d { get; set; }

    /// <summary>JSON blob of risk flags (e.g. ["DRAWDOWN", "TRADES_LIMIT_NEAR"]).</summary>
    [Column("risk_flags")] public string? RiskFlagsJson { get; set; }
}

[Table("trading_settings")]
public sealed class NvdaTradingSettings
{
    [Key, Column("id")] public int Id { get; set; }

    [Column("symbol")] public string Symbol { get; set; } = "NVDA";
    [Column("timeframe_code")] public string TimeframeCode { get; set; } = "1m";
    [Column("timeframe_minutes")] public int TimeframeMinutes { get; set; } = 1;

    /// <summary>"twelvedata", "alpha", "yahoo"</summary>
    [Column("data_provider")] public string DataProvider { get; set; } = "twelvedata";

    [Column("initial_capital_per_worker")] public double InitialCapitalPerWorker { get; set; } = 50.0;
    [Column("historical_candles")] public int HistoricalCandles { get; set; } = 200;
    [Column("updated_utc")] public DateTime UpdatedUtc { get; set; }
}

#endregion

#region API providers / keys / rate limits

/// <summary>
/// API data providers: Alpha Vantage, Twelve Data, etc.
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

    // IMPORTANT: keep this name exactly as used elsewhere (apiKey, not ApiKey)
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

[Table("api_key_ip_history")]
public sealed class ApiKeyIpHistory
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("api_key_id")] public int ApiKeyId { get; set; }

    [Column("ip_address")] public string IpAddress { get; set; } = "";

    [Column("first_seen_at")] public DateTime FirstSeenAt { get; set; }

    [Column("last_seen_at")] public DateTime LastSeenAt { get; set; }
}

[Table("api_rate_limit_events")]
public sealed class ApiRateLimitEvent
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("api_key_id")] public int ApiKeyId { get; set; }

    [Column("scope")] public string Scope { get; set; } = "";

    [Column("reason")] public string? Reason { get; set; }

    [Column("status_code")] public int StatusCode { get; set; }

    [Column("cooldown_seconds")] public int CooldownSeconds { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

#endregion

#region Fetching / markets / TOR

[Table("candle_fetch_leases")]
public sealed class CandleFetchLease
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("trading_setting_id")] public int TradingSettingId { get; set; }

    [Column("worker_name")] public string WorkerName { get; set; } = "";

    [Column("lease_expires_at")] public DateTime LeaseExpiresAt { get; set; }

    [Column("last_heartbeat_at")] public DateTime? LastHeartbeatAt { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }

    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}

[Table("fetch_events")]
public sealed class FetchEvent
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("symbol_id")] public int SymbolId { get; set; }

    [Column("timeframe_id")] public int TimeframeId { get; set; }

    [Column("provider")] public string Provider { get; set; } = "";

    [Column("event_type")] public string EventType { get; set; } = "";

    [Column("worker_name")] public string? WorkerName { get; set; }

    [Column("started_at")] public DateTime? StartedAt { get; set; }

    [Column("finished_at")] public DateTime? FinishedAt { get; set; }

    [Column("result_status")] public string? ResultStatus { get; set; }

    [Column("http_status")] public int? HttpStatus { get; set; }

    [Column("rows_inserted")] public int? RowsInserted { get; set; }

    [Column("error_message")] public string? ErrorMessage { get; set; }

    [Column("last_open_time")] public DateTime? LastOpenTime { get; set; }

    [Column("last_insert_created_at")] public DateTime? LastInsertCreatedAt { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("fetch_status")]
public sealed class FetchStatus
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("symbol_id")] public int SymbolId { get; set; }

    [Column("timeframe_id")] public int TimeframeId { get; set; }

    [Column("provider_current")] public string ProviderCurrent { get; set; } = "";

    [Column("last_provider_change_at")] public DateTime? LastProviderChangeAt { get; set; }

    [Column("worker_name")] public string? WorkerName { get; set; }

    [Column("last_fetch_started_at")] public DateTime? LastFetchStartedAt { get; set; }

    [Column("last_fetch_finished_at")] public DateTime? LastFetchFinishedAt { get; set; }

    [Column("previous_fetch_finished_at")] public DateTime? PreviousFetchFinishedAt { get; set; }

    [Column("last_fetch_status")] public string? LastFetchStatus { get; set; }

    [Column("last_http_status")] public int? LastHttpStatus { get; set; }

    [Column("last_rows_inserted")] public int? LastRowsInserted { get; set; }

    [Column("last_error")] public string? LastError { get; set; }

    [Column("last_open_time")] public DateTime? LastOpenTime { get; set; }

    [Column("last_insert_created_at")] public DateTime? LastInsertCreatedAt { get; set; }

    [Column("next_insert_due_at")] public DateTime? NextInsertDueAt { get; set; }

    [Column("computed_poll_seconds")] public int? ComputedPollSeconds { get; set; }

    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}

[Table("markets")]
public sealed class Market
{
    [Key, Column("id")] public int Id { get; set; }

    [Column("code")] public string Code { get; set; } = "";

    [Column("name")] public string Name { get; set; } = "";

    [Column("country")] public string Country { get; set; } = "";

    [Column("timezone")] public string? Timezone { get; set; }

    [Column("mic")] public string? Mic { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("market_constituents")]
public sealed class MarketConstituent
{
    [Key, Column("id")] public int Id { get; set; }

    [Column("market_id")] public int MarketId { get; set; }

    [Column("symbol_id")] public int SymbolId { get; set; }

    [Column("is_current")] public bool IsCurrent { get; set; }

    [Column("weight")] public double? Weight { get; set; }

    [Column("added_at")] public DateTime? AddedAt { get; set; }

    [Column("removed_at")] public DateTime? RemovedAt { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("tor_workers")]
public sealed class TorWorker
{
    [Key, Column("id")] public int Id { get; set; }

    [Column("name")] public string Name { get; set; } = "";

    [Column("api_key_id")] public int ApiKeyId { get; set; }

    [Column("status")] public string Status { get; set; } = "";

    [Column("current_ip")] public string? CurrentIp { get; set; }

    [Column("last_ip_change_at")] public DateTime? LastIpChangeAt { get; set; }

    [Column("last_heartbeat_at")] public DateTime? LastHeartbeatAt { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }

    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}

#endregion

#region Convertible bonds

[Table("convertible_bonds")]
public sealed class ConvertibleBond
{
    [Key, Column("id")] public int Id { get; set; }

    [Column("convertible_symbol_id")] public int ConvertibleSymbolId { get; set; }

    [Column("equity_symbol_id")] public int EquitySymbolId { get; set; }

    [Column("conversion_ratio")] public double ConversionRatio { get; set; }

    [Column("coupon_rate")] public double CouponRate { get; set; }

    [Column("issue_date")] public DateTime IssueDate { get; set; }

    [Column("maturity_date")] public DateTime MaturityDate { get; set; }

    [Column("credit_spread_bps")] public double? CreditSpreadBps { get; set; }

    [Column("status")] public string Status { get; set; } = "";

    [Column("last_updated")] public DateTime LastUpdated { get; set; }
}

#endregion

#region Signals / council

/// <summary>
/// Strategy-level signals for the "council" (NEW / PENDING_USER / ACCEPTED / ...).
/// </summary>
[Table("strategy_signals")]
public sealed class StrategySignal
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("worker_id")] public int WorkerId { get; set; }

    [Column("strategy_name")] public string StrategyName { get; set; } = "";

    [Column("symbol_id")] public int SymbolId { get; set; }

    [Column("timeframe_id")] public int TimeframeId { get; set; }

    /// <summary>"BUY" / "SELL"</summary>
    [Column("side")] public string Side { get; set; } = "";

    /// <summary>Suggested entry price.</summary>
    [Column("suggested_price")] public double SuggestedPrice { get; set; }

    /// <summary>Notional size in account currency.</summary>
    [Column("size_value")] public double SizeValue { get; set; }

    [Column("stop_loss")] public double? StopLoss { get; set; }

    [Column("take_profit")] public double? TakeProfit { get; set; }

    [Column("expected_return_pct")] public double? ExpectedReturnPct { get; set; }

    [Column("confidence")] public double? Confidence { get; set; }

    [Column("analysis_minutes")] public int? AnalysisMinutes { get; set; }

    /// <summary>Status string: "NEW", "PENDING_USER", "ACCEPTED", "REJECTED", "EXPIRED", "CANCELLED".</summary>
    [Column("status")] public string Status { get; set; } = "";

    [Column("reason")] public string? Reason { get; set; }

    [Column("created_at")] public DateTime CreatedAt { get; set; }

    [Column("valid_until")] public DateTime? ValidUntil { get; set; }

    [Column("decided_at")] public DateTime? DecidedAt { get; set; }

    [Column("decision_note")] public string? DecisionNote { get; set; }
}

[Table("council_recommendations")]
public sealed class CouncilRecommendation
{
    [Key, Column("id")] public long Id { get; set; }

    [Column("signal_id")] public long SignalId { get; set; }

    [Column("worker_id")] public int WorkerId { get; set; }

    [Column("strategy_name")] public string StrategyName { get; set; } = "";

    [Column("symbol_id")] public int SymbolId { get; set; }

    [Column("timeframe_id")] public int TimeframeId { get; set; }

    [Column("side")] public string Side { get; set; } = "";

    [Column("suggested_price")] public double SuggestedPrice { get; set; }

    [Column("size_value")] public double SizeValue { get; set; }

    [Column("stop_loss")] public double? StopLoss { get; set; }

    [Column("take_profit")] public double? TakeProfit { get; set; }

    [Column("expected_return_pct")] public double? ExpectedReturnPct { get; set; }

    [Column("expected_profit_value")] public double? ExpectedProfitValue { get; set; }

    [Column("confidence")] public double? Confidence { get; set; }

    [Column("analysis_minutes")] public int? AnalysisMinutes { get; set; }

    [Column("signal_created_at")] public DateTime? SignalCreatedAt { get; set; }

    [Column("signal_valid_until")] public DateTime? SignalValidUntil { get; set; }

    [Column("owner_user_id")] public int? OwnerUserId { get; set; }

    /// <summary>"WORKER" / "USER"</summary>
    [Column("scope")] public string? Scope { get; set; }

    [Column("user_total_equity")] public double? UserTotalEquity { get; set; }

    [Column("user_cash_available")] public double? UserCashAvailable { get; set; }

    [Column("user_capital_in_positions")] public double? UserCapitalInPositions { get; set; }

    /// <summary>"PENDING_USER", "ACCEPTED", "REJECTED", "EXPIRED"</summary>
    [Column("recommendation_status")] public string RecommendationStatus { get; set; } = "";

    [Column("created_at")] public DateTime CreatedAt { get; set; }

    [Column("decided_at")] public DateTime? DecidedAt { get; set; }

    /// <summary>"USER" / "SYSTEM"</summary>
    [Column("decision_source")] public string? DecisionSource { get; set; }

    [Column("decision_note")] public string? DecisionNote { get; set; }
}

#endregion

// DTOs

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

// =============== DB CONTEXT ===============

/// <summary>
/// Candle-level trading lab (trading_candles MySQL).
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

    public DbSet<ApiProvider> ApiProviders => Set<ApiProvider>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<ApiKeyIpHistory> ApiKeyIpHistories => Set<ApiKeyIpHistory>();
    public DbSet<ApiRateLimitEvent> ApiRateLimitEvents => Set<ApiRateLimitEvent>();

    public DbSet<CandleFetchLease> CandleFetchLeases => Set<CandleFetchLease>();
    public DbSet<FetchEvent> FetchEvents => Set<FetchEvent>();
    public DbSet<FetchStatus> FetchStatuses => Set<FetchStatus>();

    public DbSet<Market> Markets => Set<Market>();
    public DbSet<MarketConstituent> MarketConstituents => Set<MarketConstituent>();

    public DbSet<TorWorker> TorWorkers => Set<TorWorker>();

    public DbSet<ConvertibleBond> ConvertibleBonds => Set<ConvertibleBond>();

    public DbSet<StrategySignal> StrategySignals => Set<StrategySignal>();
    public DbSet<CouncilRecommendation> CouncilRecommendations => Set<CouncilRecommendation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Symbols / timeframes / workers
        b.Entity<NvdaTradingSymbol>()
            .HasIndex(x => x.Symbol)
            .IsUnique();

        b.Entity<NvdaTradingTimeframe>()
            .HasIndex(x => x.Code)
            .IsUnique();

        b.Entity<NvdaTradingWorker>()
            .HasIndex(x => x.Name)
            .IsUnique();

        b.Entity<NvdaTradingWorker>()
            .HasIndex(x => x.StrategyName)
            .HasDatabaseName("ix_workers_strategy");

        b.Entity<NvdaTradingWorker>()
            .HasIndex(x => x.RuntimeInstanceId)
            .HasDatabaseName("ix_workers_runtime");

        // Candles / features / trades / worker_stats
        b.Entity<NvdaTradingCandle>()
            .HasIndex(x => new { x.SymbolId, x.TimeframeId, x.OpenTime })
            .IsUnique();

        b.Entity<NvdaTradingCandleFeatures>()
            .HasIndex(x => x.CandleId)
            .IsUnique();

        b.Entity<NvdaTradingTrade>()
            .HasIndex(x => new { x.WorkerId, x.TradeTimeUtc });

        b.Entity<NvdaTradingTrade>()
            .HasIndex(x => x.SignalId)
            .HasDatabaseName("ix_trades_signal");

        b.Entity<NvdaTradingWorkerStats>()
            .HasIndex(x => new { x.WorkerId, x.SnapshotUtc });

        // Trading settings
        b.Entity<NvdaTradingSettings>()
            .HasIndex(x => x.Id)
            .IsUnique();

        // Strategy signals
        b.Entity<StrategySignal>()
            .HasIndex(x => new { x.Status, x.WorkerId });

        b.Entity<StrategySignal>()
            .HasIndex(x => x.WorkerId);

        b.Entity<StrategySignal>()
            .HasIndex(x => x.SymbolId);

        b.Entity<StrategySignal>()
            .HasIndex(x => new { x.Status, x.CreatedAt })
            .HasDatabaseName("ix_signals_status_time");

        // Council recommendations
        b.Entity<CouncilRecommendation>()
            .HasIndex(x => new { x.RecommendationStatus, x.CreatedAt })
            .HasDatabaseName("ix_council_status_time");

        // API providers / keys
        b.Entity<ApiProvider>()
            .HasIndex(x => x.Code)
            .IsUnique();

        b.Entity<ApiKey>()
            .HasIndex(x => new { x.ProviderId, x.apiKey })
            .IsUnique();

        b.Entity<ApiKey>()
            .HasIndex(x => x.ProviderId);

        b.Entity<ApiKey>()
            .HasIndex(x => x.NextAvailableAt);

        b.Entity<ApiKey>()
            .HasIndex(x => x.IpNextAvailableAt);

        b.Entity<ApiKey>()
            .HasOne<ApiProvider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId);

        // API IP history, rate limit events
        b.Entity<ApiKeyIpHistory>()
            .HasIndex(x => new { x.ApiKeyId, x.IpAddress });

        b.Entity<ApiRateLimitEvent>()
            .HasIndex(x => x.ApiKeyId);

        // Fetching status/events
        b.Entity<CandleFetchLease>()
            .HasIndex(x => new { x.TradingSettingId, x.WorkerName });

        b.Entity<FetchStatus>()
            .HasIndex(x => new { x.SymbolId, x.TimeframeId });

        b.Entity<FetchEvent>()
            .HasIndex(x => new { x.SymbolId, x.TimeframeId, x.CreatedAt });

        // Markets
        b.Entity<Market>()
            .HasIndex(x => x.Code)
            .IsUnique();

        b.Entity<MarketConstituent>()
            .HasIndex(x => new { x.MarketId, x.SymbolId });

        // TOR workers
        b.Entity<TorWorker>()
            .HasIndex(x => x.ApiKeyId);
    }
}
