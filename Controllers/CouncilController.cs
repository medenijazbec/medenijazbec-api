// honey_badger_api/Controllers/CouncilController.cs
using System;
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

    public CouncilController(NvdaTradingDbContext db)
    {
        _db = db;
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

    // ---------- endpoints ----------

    /// <summary>
    /// Latest PENDING_USER recommendation (optionally filtered by owner_user_id).
    /// </summary>
    [HttpGet("recommendation")]
    public async Task<ActionResult<CouncilRecommendationDto>> GetLatestRecommendation(
        [FromQuery] int? ownerUserId = null)
    {
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
        var decisionUpper = (req.Decision ?? "").Trim().ToUpperInvariant();
        if (decisionUpper != "ACCEPT" && decisionUpper != "REJECT")
            return BadRequest("Decision must be 'ACCEPT' or 'REJECT'.");

        var rec = await _db.CouncilRecommendations
            .FirstOrDefaultAsync(c => c.Id == recommendationId);

        if (rec == null)
            return NotFound("Recommendation not found.");

        if (rec.RecommendationStatus != "PENDING_USER")
            return BadRequest("Recommendation is not PENDING_USER anymore.");

        var now = DateTime.UtcNow;

        rec.RecommendationStatus = decisionUpper == "ACCEPT" ? "ACCEPTED" : "REJECTED";
        rec.DecidedAt = now;
        rec.DecisionSource = "USER";
        rec.DecisionNote = req.DecisionNote;

        // overwrite capital snapshot if frontend sends updated numbers
        rec.UserTotalEquity = req.UserTotalEquity ?? rec.UserTotalEquity;
        rec.UserCashAvailable = req.UserCashAvailable ?? rec.UserCashAvailable;
        rec.UserCapitalInPositions = req.UserCapitalInPositions ?? rec.UserCapitalInPositions;

        var signal = await _db.StrategySignals
            .FirstOrDefaultAsync(s => s.Id == rec.SignalId);

        if (signal != null)
        {
            signal.Status = rec.RecommendationStatus;
            signal.DecidedAt = now;
            signal.DecisionNote = rec.DecisionNote;
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }
}
