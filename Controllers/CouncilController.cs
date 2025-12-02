// honey_badger_api/Controllers/CouncilController.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using honey_badger_api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CouncilController : ControllerBase
{
    private readonly NvdaTradingDbContext _db;
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(3);

    public CouncilController(NvdaTradingDbContext db)
    {
        _db = db;
    }

    // ---------- helpers ----------

    private static DateTime UtcNow => DateTime.UtcNow;

    private static DateTime ComputeExpiresAt(DateTime createdAtUtc) =>
        createdAtUtc.Add(PendingTtl);

    private static bool IsExpired(DateTime createdAtUtc, DateTime? validUntilUtc, DateTime nowUtc)
    {
        var hardTtlExpiry = createdAtUtc + PendingTtl;
        if (validUntilUtc.HasValue)
        {
            return nowUtc >= validUntilUtc.Value || nowUtc >= hardTtlExpiry;
        }
        return nowUtc >= hardTtlExpiry;
    }

    private IQueryable<CouncilRecommendation> FilterByOwner(
        IQueryable<CouncilRecommendation> query,
        int? ownerUserId)
    {
        if (ownerUserId.HasValue)
            return query.Where(c => c.OwnerUserId == ownerUserId.Value);
        return query.Where(c => c.OwnerUserId == null);
    }

    /// <summary>
    /// Opportunistically expire PENDING_USER rows older than 3 minutes.
    /// </summary>
    private async Task<int> ExpirePendingAsync(int? ownerUserId)
    {
        var now = UtcNow;
        var cutoff = now - PendingTtl;

        var query = _db.CouncilRecommendations
            .Where(c => c.RecommendationStatus == "PENDING_USER" && c.CreatedAt <= cutoff);

        query = FilterByOwner(query, ownerUserId);

        var expired = await query.ToListAsync();
        if (expired.Count == 0)
            return 0;

        foreach (var rec in expired)
        {
            rec.RecommendationStatus = "EXPIRED";
            rec.DecidedAt = now;
            rec.DecisionSource = "SYSTEM";
            rec.DecisionNote = "Auto-expired after 3 minutes with no user decision.";

            var signal = await _db.StrategySignals
                .FirstOrDefaultAsync(s => s.Id == rec.SignalId);

            if (signal != null && signal.Status == "PENDING_USER")
            {
                signal.Status = "EXPIRED";
                signal.DecidedAt = now;
                signal.DecisionNote = rec.DecisionNote;
            }
        }

        return await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Helper to load symbol/timeframe/worker to avoid repeated lookups.
    /// </summary>
    private async Task<(Dictionary<int, NvdaTradingSymbol> symbols,
        Dictionary<int, NvdaTradingTimeframe> timeframes,
        Dictionary<int, NvdaTradingWorker> workers)> LoadContextDictionariesAsync()
    {
        var symbols = await _db.Symbols.AsNoTracking().ToDictionaryAsync(s => s.Id);
        var timeframes = await _db.Timeframes.AsNoTracking().ToDictionaryAsync(tf => tf.Id);
        var workers = await _db.Workers.AsNoTracking().ToDictionaryAsync(w => w.Id);
        return (symbols, timeframes, workers);
    }

    // ---------- DTOs ----------

    public sealed class SellSuggestionDto
    {
        public long? RecommendationId { get; init; }
        public string Side { get; init; } = "";
        public double? SuggestedPrice { get; init; }
        public double? ExpectedReturnPct { get; init; }
        public double? ExpectedProfitValue { get; init; }
        public double? Confidence { get; init; }
        public DateTime? CreatedAtUtc { get; init; }
        public string? Note { get; init; }
        public string? Scope { get; init; }
    }

    public sealed class CouncilOfferDto
    {
        public long RecommendationId { get; init; }
        public long SignalId { get; init; }
        public int WorkerId { get; init; }
        public string WorkerName { get; init; } = "";
        public string StrategyName { get; init; } = "";
        public int SymbolId { get; init; }
        public string Symbol { get; init; } = "";
        public string? SymbolName { get; init; }
        public int TimeframeId { get; init; }
        public string TimeframeCode { get; init; } = "";
        public int TimeframeMinutes { get; init; }
        public string Side { get; init; } = "";
        public double SuggestedPrice { get; init; }
        public double SizeValue { get; init; }
        public double? StopLoss { get; init; }
        public double? TakeProfit { get; init; }
        public double? ExpectedReturnPct { get; init; }
        public double? ExpectedProfitValue { get; init; }
        public double? Confidence { get; init; }
        public int? AnalysisMinutes { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        public DateTime? SignalCreatedAtUtc { get; init; }
        public DateTime? SignalValidUntilUtc { get; init; }
        public string RecommendationStatus { get; init; } = "";
        public bool IsExpired { get; init; }
        public SellSuggestionDto? SellSuggestion { get; init; }
    }

    // ---------- DTOs ----------

    public sealed class CouncilRecommendationDto
    {
        public long RecommendationId { get; init; }
        public long SignalId { get; init; }

        public int WorkerId { get; init; }
        public string WorkerName { get; init; } = "";
        public string StrategyName { get; init; } = "";

        public int SymbolId { get; init; }
        public string Symbol { get; init; } = "";
        public string? SymbolName { get; init; }

        public int TimeframeId { get; init; }
        public string TimeframeCode { get; init; } = "";
        public int TimeframeMinutes { get; init; }

        public string Side { get; init; } = "";
        public double SuggestedPrice { get; init; }
        public double SizeValue { get; init; }
        public double? StopLoss { get; init; }
        public double? TakeProfit { get; init; }

        public double? ExpectedReturnPct { get; init; }
        public double? ExpectedProfitValue { get; init; }
        public double? Confidence { get; init; }
        public int? AnalysisMinutes { get; init; }

        public DateTime? SignalCreatedAtUtc { get; init; }
        public DateTime? SignalValidUntilUtc { get; init; }
        public DateTime CreatedAtUtc { get; init; }

        public DateTime? LatestCandleOpenTimeUtc { get; init; }

        public double? UserTotalEquity { get; init; }
        public double? UserCashAvailable { get; init; }
        public double? UserCapitalInPositions { get; init; }

        public string RecommendationStatus { get; init; } = "";

        public double? WorkerSuccessRatePct { get; init; }
        public int? WorkerTradesSampleCount { get; init; }

        public double? StrategySuccessRatePct { get; init; }
        public int? StrategyTradesSampleCount { get; init; }
    }

    public sealed class CouncilDecisionRequest
    {
        /// <summary>"ACCEPT" or "REJECT"</summary>
        public string Decision { get; init; } = "";
        public string? DecisionNote { get; init; }

        public double? UserTotalEquity { get; init; }
        public double? UserCashAvailable { get; init; }
        public double? UserCapitalInPositions { get; init; }
    }

    public sealed class CouncilSoldRequest
    {
        public double? SoldPrice { get; init; }
        public DateTime? SoldAtUtc { get; init; }
        public string? DecisionNote { get; init; }
    }

    // ---------- mapping ----------

    private CouncilOfferDto MapToOfferDto(
        CouncilRecommendation rec,
        Dictionary<int, NvdaTradingSymbol> symbols,
        Dictionary<int, NvdaTradingTimeframe> timeframes,
        Dictionary<int, NvdaTradingWorker> workers,
        SellSuggestionDto? sell = null)
    {
        symbols.TryGetValue(rec.SymbolId, out var symbol);
        timeframes.TryGetValue(rec.TimeframeId, out var timeframe);
        workers.TryGetValue(rec.WorkerId, out var worker);

        int? analysisMinutes = rec.AnalysisMinutes;
        if (analysisMinutes == null && rec.SignalCreatedAt.HasValue)
        {
            var minutes = (rec.CreatedAt - rec.SignalCreatedAt.Value).TotalMinutes;
            if (minutes >= 0)
                analysisMinutes = (int)Math.Round(minutes);
        }

        var expiresAt = ComputeExpiresAt(rec.CreatedAt);
        var now = UtcNow;
        var isExpired = IsExpired(rec.CreatedAt, rec.SignalValidUntil, now);

        return new CouncilOfferDto
        {
            RecommendationId = rec.Id,
            SignalId = rec.SignalId,
            WorkerId = rec.WorkerId,
            WorkerName = worker?.Name ?? $"worker_{rec.WorkerId}",
            StrategyName = rec.StrategyName,
            SymbolId = rec.SymbolId,
            Symbol = symbol?.Symbol ?? $"symbol_{rec.SymbolId}",
            SymbolName = symbol?.Name,
            TimeframeId = rec.TimeframeId,
            TimeframeCode = timeframe?.Code ?? "",
            TimeframeMinutes = timeframe?.Minutes ?? 0,
            Side = rec.Side,
            SuggestedPrice = rec.SuggestedPrice,
            SizeValue = rec.SizeValue,
            StopLoss = rec.StopLoss,
            TakeProfit = rec.TakeProfit,
            ExpectedReturnPct = rec.ExpectedReturnPct,
            ExpectedProfitValue = rec.ExpectedProfitValue,
            Confidence = rec.Confidence,
            AnalysisMinutes = analysisMinutes,
            CreatedAtUtc = rec.CreatedAt,
            ExpiresAtUtc = expiresAt,
            SignalCreatedAtUtc = rec.SignalCreatedAt,
            SignalValidUntilUtc = rec.SignalValidUntil,
            RecommendationStatus = rec.RecommendationStatus,
            IsExpired = isExpired,
            SellSuggestion = sell
        };
    }

    // ---------- endpoints ----------

    /// <summary>
    /// Active board: PENDING_USER offers that are still inside the 3m TTL.
    /// </summary>
    [HttpGet("offers/active")]
    public async Task<ActionResult<CouncilOfferDto[]>> GetActiveOffers(
        [FromQuery] int? ownerUserId = null)
    {
        await ExpirePendingAsync(ownerUserId);

        var now = UtcNow;
        var window = now - PendingTtl;

        var baseQuery = _db.CouncilRecommendations.AsNoTracking()
            .Where(c => c.RecommendationStatus == "PENDING_USER" && c.CreatedAt >= window);

        baseQuery = FilterByOwner(baseQuery, ownerUserId);

        var rows = await baseQuery
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        var ctx = await LoadContextDictionariesAsync();
        var dtos = rows
            .Select(r => MapToOfferDto(r, ctx.symbols, ctx.timeframes, ctx.workers))
            .ToArray();

        return Ok(dtos);
    }

    /// <summary>
    /// Accepted offers still being watched by user. Includes sell suggestion flag.
    /// </summary>
    [HttpGet("offers/accepted")]
    public async Task<ActionResult<CouncilOfferDto[]>> GetAcceptedOffers(
        [FromQuery] int? ownerUserId = null)
    {
        await ExpirePendingAsync(ownerUserId);

        var baseQuery = _db.CouncilRecommendations.AsNoTracking()
            .Where(c => c.RecommendationStatus == "ACCEPTED");

        baseQuery = FilterByOwner(baseQuery, ownerUserId);

        var accepted = await baseQuery
            .OrderByDescending(c => c.DecidedAt ?? c.CreatedAt)
            .ToListAsync();

        var ctx = await LoadContextDictionariesAsync();

        // find latest exit / sell suggestions for each accepted offer
        var workerIds = accepted.Select(a => a.WorkerId).Distinct().ToArray();
        var symbolIds = accepted.Select(a => a.SymbolId).Distinct().ToArray();
        var timeframeIds = accepted.Select(a => a.TimeframeId).Distinct().ToArray();

        var exits = await _db.CouncilRecommendations.AsNoTracking()
            .Where(c =>
                c.RecommendationStatus == "READY_TO_SELL" ||
                c.Scope == "EXIT" ||
                c.Scope == "SELL")
            .Where(c => workerIds.Contains(c.WorkerId))
            .Where(c => symbolIds.Contains(c.SymbolId))
            .Where(c => timeframeIds.Contains(c.TimeframeId))
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        SellSuggestionDto? FindExit(CouncilRecommendation rec)
        {
            var exit = exits.FirstOrDefault(e =>
                e.Id != rec.Id &&
                (e.SignalId == rec.SignalId ||
                 (e.WorkerId == rec.WorkerId &&
                  e.SymbolId == rec.SymbolId &&
                  e.TimeframeId == rec.TimeframeId)));

            if (exit == null) return null;

            return new SellSuggestionDto
            {
                RecommendationId = exit.Id,
                Side = exit.Side,
                SuggestedPrice = exit.SuggestedPrice,
                ExpectedReturnPct = exit.ExpectedReturnPct,
                ExpectedProfitValue = exit.ExpectedProfitValue,
                Confidence = exit.Confidence,
                CreatedAtUtc = exit.CreatedAt,
                Note = exit.DecisionNote,
                Scope = exit.Scope
            };
        }

        var dtos = accepted
            .Select(r => MapToOfferDto(r, ctx.symbols, ctx.timeframes, ctx.workers, FindExit(r)))
            .ToArray();

        return Ok(dtos);
    }

    /// <summary>
    /// History (last 24h by default) of recommendations with any status.
    /// </summary>
    [HttpGet("offers/history")]
    public async Task<ActionResult<CouncilOfferDto[]>> GetHistory(
        [FromQuery] int? ownerUserId = null,
        [FromQuery] int hours = 24)
    {
        if (hours <= 0) hours = 24;
        if (hours > 168) hours = 168;

        await ExpirePendingAsync(ownerUserId);

        var since = UtcNow.AddHours(-hours);
        var baseQuery = _db.CouncilRecommendations.AsNoTracking()
            .Where(c => c.CreatedAt >= since);

        baseQuery = FilterByOwner(baseQuery, ownerUserId);

        var rows = await baseQuery
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        var ctx = await LoadContextDictionariesAsync();
        var dtos = rows
            .Select(r => MapToOfferDto(r, ctx.symbols, ctx.timeframes, ctx.workers))
            .ToArray();

        return Ok(dtos);
    }

    /// <summary>
    /// Accept a pending recommendation (entry).
    /// </summary>
    [HttpPost("offers/{recommendationId:long}/accept")]
    public async Task<IActionResult> AcceptOffer(
        long recommendationId,
        [FromBody] CouncilDecisionRequest req)
    {
        return await DecideInternal(recommendationId, "ACCEPT", req);
    }

    /// <summary>
    /// Reject a pending recommendation.
    /// </summary>
    [HttpPost("offers/{recommendationId:long}/reject")]
    public async Task<IActionResult> RejectOffer(
        long recommendationId,
        [FromBody] CouncilDecisionRequest req)
    {
        return await DecideInternal(recommendationId, "REJECT", req);
    }

    /// <summary>
    /// Mark an accepted offer as sold/closed by user.
    /// Creates a synthetic trade row for auditing if none exists.
    /// </summary>
    [HttpPost("offers/{recommendationId:long}/sold")]
    public async Task<IActionResult> MarkOfferSold(
        long recommendationId,
        [FromBody] CouncilSoldRequest req)
    {
        var rec = await _db.CouncilRecommendations
            .FirstOrDefaultAsync(c => c.Id == recommendationId);

        if (rec == null)
            return NotFound("Recommendation not found.");

        if (rec.RecommendationStatus != "ACCEPTED")
            return BadRequest("Only ACCEPTED offers can be marked as sold.");

        var soldAt = req.SoldAtUtc ?? UtcNow;
        rec.RecommendationStatus = "SOLD";
        rec.DecidedAt = soldAt;
        rec.DecisionSource = "USER_SOLD";
        rec.DecisionNote = req.DecisionNote ?? "User marked as sold via dashboard.";

        var signal = await _db.StrategySignals.FirstOrDefaultAsync(s => s.Id == rec.SignalId);
        if (signal != null)
        {
            signal.Status = "SOLD";
            signal.DecidedAt = soldAt;
            signal.DecisionNote = rec.DecisionNote;
        }

        // Create a synthetic trade record if none exists for this signal.
        var existingTrade = await _db.Trades.AsNoTracking()
            .FirstOrDefaultAsync(t => t.SignalId == rec.SignalId);

        if (existingTrade == null && req.SoldPrice.HasValue && rec.SuggestedPrice > 0)
        {
            var qty = Math.Abs(rec.SizeValue / rec.SuggestedPrice);
            var exitSide = rec.Side == "BUY" ? "SELL" : "BUY";
            var realizedPnl = (req.SoldPrice.Value - rec.SuggestedPrice) * qty;
            if (rec.Side == "SELL")
            {
                realizedPnl = -realizedPnl; // reverse for shorts
            }

            var trade = new NvdaTradingTrade
            {
                WorkerId = rec.WorkerId,
                SymbolId = rec.SymbolId,
                TimeframeId = rec.TimeframeId,
                SignalId = rec.SignalId,
                Side = exitSide,
                Quantity = qty,
                Price = req.SoldPrice.Value,
                StopLoss = rec.StopLoss,
                TakeProfit = rec.TakeProfit,
                TradeTimeUtc = soldAt,
                RealizedPnl = realizedPnl,
                Notes = "User-marked sold via Council dashboard",
                CreatedAt = soldAt
            };
            _db.Trades.Add(trade);
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<IActionResult> DecideInternal(
        long recommendationId,
        string decisionUpper,
        CouncilDecisionRequest req)
    {
        decisionUpper = (decisionUpper ?? "").Trim().ToUpperInvariant();
        if (decisionUpper != "ACCEPT" && decisionUpper != "REJECT")
            return BadRequest("Decision must be 'ACCEPT' or 'REJECT'.");

        var rec = await _db.CouncilRecommendations
            .FirstOrDefaultAsync(c => c.Id == recommendationId);

        if (rec == null)
            return NotFound("Recommendation not found.");

        if (rec.RecommendationStatus == "PENDING_USER")
        {
            var now = UtcNow;
            if (IsExpired(rec.CreatedAt, rec.SignalValidUntil, now))
            {
                rec.RecommendationStatus = "EXPIRED";
                rec.DecidedAt = now;
                rec.DecisionSource = "SYSTEM";
                rec.DecisionNote = "Auto-expired before decision.";
                await _db.SaveChangesAsync();
                return BadRequest("Recommendation expired before decision.");
            }
        }

        if (rec.RecommendationStatus != "PENDING_USER")
            return BadRequest("Recommendation is not PENDING_USER anymore.");

        var decisionNote = req.DecisionNote;
        var nowUtc = UtcNow;

        rec.RecommendationStatus = decisionUpper == "ACCEPT" ? "ACCEPTED" : "REJECTED";
        rec.DecidedAt = nowUtc;
        rec.DecisionSource = "USER";
        rec.DecisionNote = decisionNote;

        rec.UserTotalEquity = req.UserTotalEquity ?? rec.UserTotalEquity;
        rec.UserCashAvailable = req.UserCashAvailable ?? rec.UserCashAvailable;
        rec.UserCapitalInPositions = req.UserCapitalInPositions ?? rec.UserCapitalInPositions;

        var signal = await _db.StrategySignals
            .FirstOrDefaultAsync(s => s.Id == rec.SignalId);

        if (signal != null)
        {
            signal.Status = rec.RecommendationStatus;
            signal.DecidedAt = nowUtc;
            signal.DecisionNote = decisionNote;
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Latest PENDING_USER recommendation (optionally filtered by owner_user_id).
    /// </summary>
    [HttpGet("recommendation")]
    public async Task<ActionResult<CouncilRecommendationDto>> GetLatestRecommendation(
        [FromQuery] int? ownerUserId = null)
    {
        await ExpirePendingAsync(ownerUserId);

        var query = _db.CouncilRecommendations
            .Where(c => c.RecommendationStatus == "PENDING_USER");

        if (ownerUserId.HasValue)
            query = query.Where(c => c.OwnerUserId == ownerUserId.Value);
        else
            query = query.Where(c => c.OwnerUserId == null);

        var rec = await query
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        if (rec == null)
            return NotFound("No pending council recommendation found.");

        var symbol = await _db.Symbols.FirstOrDefaultAsync(s => s.Id == rec.SymbolId);
        var timeframe = await _db.Timeframes.FirstOrDefaultAsync(tf => tf.Id == rec.TimeframeId);
        var worker = await _db.Workers.FirstOrDefaultAsync(w => w.Id == rec.WorkerId);

        // latest candle for this (symbol, timeframe)
        DateTime? latestCandleOpenTimeUtc = null;
        try
        {
            latestCandleOpenTimeUtc = await _db.Candles
                .Where(c => c.SymbolId == rec.SymbolId && c.TimeframeId == rec.TimeframeId)
                .OrderByDescending(c => c.OpenTime)
                .Select(c => (DateTime?)c.OpenTime)
                .FirstOrDefaultAsync();
        }
        catch
        {
            // best-effort only
        }

        // ---------- success metrics ----------

        var since = DateTime.UtcNow.AddDays(-90);

        // worker-level win rate
        var workerAgg = await _db.Trades
            .Where(t =>
                t.WorkerId == rec.WorkerId &&
                t.TradeTimeUtc >= since &&
                t.RealizedPnl != null)
            .GroupBy(t => t.WorkerId)
            .Select(g => new
            {
                Total = g.Count(),
                Wins = g.Count(t => t.RealizedPnl! > 0)
            })
            .FirstOrDefaultAsync();

        double? workerSuccessPct = null;
        int? workerSampleCount = null;
        if (workerAgg != null && workerAgg.Total > 0)
        {
            workerSampleCount = workerAgg.Total;
            workerSuccessPct = 100.0 * workerAgg.Wins / workerAgg.Total;
        }

        // strategy-wide win rate
        double? strategySuccessPct = null;
        int? strategySampleCount = null;

        if (!string.IsNullOrWhiteSpace(rec.StrategyName))
        {
            var strategyAgg = await (
                from t in _db.Trades
                join w in _db.Workers on t.WorkerId equals w.Id
                where w.StrategyName == rec.StrategyName
                      && t.TradeTimeUtc >= since
                      && t.RealizedPnl != null
                select t)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Total = g.Count(),
                    Wins = g.Count(t => t.RealizedPnl! > 0)
                })
                .FirstOrDefaultAsync();

            if (strategyAgg != null && strategyAgg.Total > 0)
            {
                strategySampleCount = strategyAgg.Total;
                strategySuccessPct = 100.0 * strategyAgg.Wins / strategyAgg.Total;
            }
        }

        var dto = new CouncilRecommendationDto
        {
            RecommendationId = rec.Id,
            SignalId = rec.SignalId,
            WorkerId = rec.WorkerId,
            WorkerName = worker?.Name ?? $"worker_{rec.WorkerId}",
            StrategyName = rec.StrategyName,

            SymbolId = rec.SymbolId,
            Symbol = symbol?.Symbol ?? $"symbol_{rec.SymbolId}",
            SymbolName = symbol?.Name,

            TimeframeId = rec.TimeframeId,
            TimeframeCode = timeframe?.Code ?? "",
            TimeframeMinutes = timeframe?.Minutes ?? 0,

            Side = rec.Side,
            SuggestedPrice = rec.SuggestedPrice,
            SizeValue = rec.SizeValue,
            StopLoss = rec.StopLoss,
            TakeProfit = rec.TakeProfit,
            ExpectedReturnPct = rec.ExpectedReturnPct,
            ExpectedProfitValue = rec.ExpectedProfitValue,
            Confidence = rec.Confidence,
            AnalysisMinutes = rec.AnalysisMinutes,

            SignalCreatedAtUtc = rec.SignalCreatedAt,
            SignalValidUntilUtc = rec.SignalValidUntil,
            CreatedAtUtc = rec.CreatedAt,
            LatestCandleOpenTimeUtc = latestCandleOpenTimeUtc,

            UserTotalEquity = rec.UserTotalEquity,
            UserCashAvailable = rec.UserCashAvailable,
            UserCapitalInPositions = rec.UserCapitalInPositions,

            RecommendationStatus = rec.RecommendationStatus,
            WorkerSuccessRatePct = workerSuccessPct,
            WorkerTradesSampleCount = workerSampleCount,
            StrategySuccessRatePct = strategySuccessPct,
            StrategyTradesSampleCount = strategySampleCount
        };

        return Ok(dto);
    }

    /// <summary>
    /// User decides on a recommendation: ACCEPT or REJECT.
    /// This updates both council_recommendations and strategy_signals.
    /// </summary>
    [HttpPost("recommendation/{recommendationId:long}/decision")]
    public async Task<IActionResult> DecideOnRecommendation(
        long recommendationId,
        [FromBody] CouncilDecisionRequest req)
    {
        return await DecideInternal(recommendationId, req.Decision, req);
    }
}
