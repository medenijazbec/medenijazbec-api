using honey_badger_api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace honey_badger_api.Controllers;

[ApiController]
[Route("api/trading-dashboard")]
[Authorize] // tighten with Roles="Admin" for admin-only endpoints below
public sealed class TradingDashboardController : ControllerBase
{
    private readonly NvdaTradingDbContext _db;

    public TradingDashboardController(NvdaTradingDbContext db)
    {
        _db = db;
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------
    private static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return "PAPER";
        mode = mode.Trim().ToUpperInvariant();
        return (mode is "LIVE" or "PAPER") ? mode : "PAPER";
    }

    private async Task<Dictionary<int, NvdaTradingWorkerStats>> GetLatestWorkerStatsAsync(
        string? modeFilter = null)
    {
        var workerQuery = _db.Workers.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(modeFilter))
        {
            var m = NormalizeMode(modeFilter);
            workerQuery = workerQuery.Where(w => w.Mode == m);
        }

        var workers = await workerQuery.ToListAsync();
        var workerIds = workers.Select(w => w.Id).ToArray();

        if (workerIds.Length == 0)
            return new();

        var stats = await _db.WorkerStats.AsNoTracking()
            .Where(ws => workerIds.Contains(ws.WorkerId))
            .OrderBy(ws => ws.WorkerId)
            .ThenByDescending(ws => ws.SnapshotUtc)
            .ToListAsync();

        return stats
            .GroupBy(s => s.WorkerId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    // ---------------------------------------------------------------------
    // 2. COUNCIL: Current best trade + history + performance
    // ---------------------------------------------------------------------

    public sealed record CouncilCurrentSignalDto(
        long signalId,
        DateTime createdAt,
        DateTime? decidedAt,
        string status,
        string symbol,
        string side,
        double? suggestedPrice,
        double? sizeValue,
        double? stopLoss,
        double? takeProfit,
        double? expectedReturnPct,
        double? confidence,
        int workerId,
        string workerName,
        string strategyName,
        string freshness,        // Fresh / Stale
        string fillStatus,       // NotFilled / Partial / Filled
        double? totalRealizedPnl,
        int tradesCount
    );

    /// <summary>
    /// Latest ACCEPTED council signal (the "best trade" now).
    /// Supports mode=PAPER/LIVE by filtering worker.mode.
    /// </summary>
    [HttpGet("council/current")]
    public async Task<ActionResult<CouncilCurrentSignalDto?>> GetCurrentCouncilSignal(
        [FromQuery] string? mode = null)
    {
        var normalizedMode = NormalizeMode(mode);

        var query =
            from ss in _db.StrategySignals.AsNoTracking()
            join w in _db.Workers.AsNoTracking() on ss.WorkerId equals w.Id
            join s in _db.Symbols.AsNoTracking() on ss.SymbolId equals s.Id
            where ss.Status == "ACCEPTED" && w.Mode == normalizedMode
            orderby ss.DecidedAt ?? ss.CreatedAt descending
            select new { ss, w, s };

        var row = await query.FirstOrDefaultAsync();
        if (row == null) return Ok(null);

        var trades = await _db.Trades.AsNoTracking()
            .Where(t => t.SignalId == row.ss.Id)
            .ToListAsync();

        var totalPnl = trades.Sum(t => t.RealizedPnl ?? 0.0);
        var tradesCount = trades.Count;

        string fillStatus;
        if (tradesCount == 0)
        {
            fillStatus = "NotFilled";
        }
        else
        {
            // SizeValue and SuggestedPrice are non-nullable doubles in StrategySignal.
            // We approximate fill status by comparing executed notional vs target notional.
            var notional = trades.Sum(t => t.Quantity * t.Price);
            var targetNotional = row.ss.SizeValue;

            var frac = Math.Abs(targetNotional) < 1e-6
                ? 1.0
                : Math.Min(1.0, Math.Abs(notional / targetNotional));

            fillStatus = frac > 0.9 ? "Filled" : "Partial";
        }

        var decidedAt = row.ss.DecidedAt ?? row.ss.CreatedAt;
        var ageMinutes = (DateTime.UtcNow - decidedAt).TotalMinutes;
        var freshness = ageMinutes <= 5 ? "Fresh" : "Stale";

        var dto = new CouncilCurrentSignalDto(
            signalId: row.ss.Id,
            createdAt: row.ss.CreatedAt,
            decidedAt: row.ss.DecidedAt,
            status: row.ss.Status,
            symbol: row.s.Symbol,
            side: row.ss.Side,
            suggestedPrice: row.ss.SuggestedPrice,
            sizeValue: row.ss.SizeValue,
            stopLoss: row.ss.StopLoss,
            takeProfit: row.ss.TakeProfit,
            expectedReturnPct: row.ss.ExpectedReturnPct,
            confidence: row.ss.Confidence,
            workerId: row.w.Id,
            workerName: row.w.Name,
            strategyName: row.ss.StrategyName,
            freshness: freshness,
            fillStatus: fillStatus,
            totalRealizedPnl: totalPnl,
            tradesCount: tradesCount
        );

        return Ok(dto);
    }

    public sealed record CouncilDecisionRowDto(
        long signalId,
        DateTime createdAt,
        DateTime? decidedAt,
        string status,
        string symbol,
        string side,
        double? expectedReturnPct,
        double? confidence,
        string workerName,
        string strategyName,
        string? decisionNote
    );

    [HttpGet("council/history")]
    public async Task<ActionResult<CouncilDecisionRowDto[]>> GetCouncilHistory(
        [FromQuery] string? mode = null,
        [FromQuery] string? status = null,
        [FromQuery] string? symbol = null,
        [FromQuery] int? workerId = null,
        [FromQuery] int limit = 100)
    {
        if (limit <= 0) limit = 100;
        if (limit > 500) limit = 500;

        var normalizedMode = NormalizeMode(mode);

        var query =
            from ss in _db.StrategySignals.AsNoTracking()
            join w in _db.Workers.AsNoTracking() on ss.WorkerId equals w.Id
            join s in _db.Symbols.AsNoTracking() on ss.SymbolId equals s.Id
            where w.Mode == normalizedMode
            select new { ss, w, s };

        if (!string.IsNullOrWhiteSpace(status))
        {
            var st = status.Trim().ToUpperInvariant();
            query = query.Where(x => x.ss.Status == st);
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var sym = symbol.Trim().ToUpperInvariant();
            query = query.Where(x => x.s.Symbol == sym);
        }

        if (workerId.HasValue)
        {
            query = query.Where(x => x.w.Id == workerId.Value);
        }

        var rows = await query
            .OrderByDescending(x => x.ss.DecidedAt ?? x.ss.CreatedAt)
            .Take(limit)
            .Select(x => new CouncilDecisionRowDto(
                x.ss.Id,
                x.ss.CreatedAt,
                x.ss.DecidedAt,
                x.ss.Status,
                x.s.Symbol,
                x.ss.Side,
                x.ss.ExpectedReturnPct,
                x.ss.Confidence,
                x.w.Name,
                x.ss.StrategyName,
                x.ss.DecisionNote
            ))
            .ToListAsync();

        return Ok(rows.ToArray());
    }

    public sealed record CouncilStatusStatsDto(
        string status,
        int signals,
        double? avgExpectedReturnPct,
        double? avgConfidence
    );

    [HttpGet("council/stats")]
    public async Task<ActionResult<CouncilStatusStatsDto[]>> GetCouncilStatusStats(
        [FromQuery] string? mode = null)
    {
        var normalizedMode = NormalizeMode(mode);

        var query =
            from ss in _db.StrategySignals.AsNoTracking()
            join w in _db.Workers.AsNoTracking() on ss.WorkerId equals w.Id
            where w.Mode == normalizedMode
            select ss;

        var list = await query.ToListAsync();

        var grouped = list
            .GroupBy(ss => ss.Status)
            .Select(g => new CouncilStatusStatsDto(
                g.Key,
                g.Count(),
                g.Where(x => x.ExpectedReturnPct.HasValue)
                    .Select(x => x.ExpectedReturnPct!.Value)
                    .DefaultIfEmpty()
                    .Average(),
                g.Where(x => x.Confidence.HasValue)
                    .Select(x => x.Confidence!.Value)
                    .DefaultIfEmpty()
                    .Average()
            ))
            .ToArray();

        return Ok(grouped);
    }

    public sealed record CouncilSignalOutcomeDto(
        long signalId,
        DateTime decidedAt,
        string symbol,
        string side,
        string workerName,
        double? expectedReturnPct,
        double? confidence,
        int tradesFromSignal,
        double totalRealizedPnl,
        double avgRealizedPnl,
        bool isWinner
    );

    public sealed record CouncilPerformanceSummaryDto(
        double hitRate,
        double avgPnlPerSignal,
        CouncilSignalOutcomeDto[] signals
    );

    [HttpGet("council/performance")]
    public async Task<ActionResult<CouncilPerformanceSummaryDto>> GetCouncilPerformance(
        [FromQuery] string? mode = null,
        [FromQuery] int limitSignals = 200)
    {
        if (limitSignals <= 0) limitSignals = 200;
        if (limitSignals > 500) limitSignals = 500;

        var normalizedMode = NormalizeMode(mode);

        var acceptedQuery =
            from ss in _db.StrategySignals.AsNoTracking()
            join w in _db.Workers.AsNoTracking() on ss.WorkerId equals w.Id
            join s in _db.Symbols.AsNoTracking() on ss.SymbolId equals s.Id
            where ss.Status == "ACCEPTED" && w.Mode == normalizedMode
            orderby ss.DecidedAt ?? ss.CreatedAt descending
            select new { ss, w, s };

        var accepted = await acceptedQuery
            .Take(limitSignals)
            .ToListAsync();

        var signalIds = accepted.Select(x => x.ss.Id).ToArray();
        var trades = await _db.Trades.AsNoTracking()
            .Where(t => t.SignalId.HasValue && signalIds.Contains(t.SignalId.Value))
            .ToListAsync();

        var tradesBySignal = trades
            .GroupBy(t => t.SignalId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var outcomes = new List<CouncilSignalOutcomeDto>();

        foreach (var row in accepted)
        {
            var id = row.ss.Id;
            tradesBySignal.TryGetValue(id, out var tlist);
            tlist ??= new List<NvdaTradingTrade>();

            var totalPnl = tlist.Sum(t => t.RealizedPnl ?? 0.0);
            var avgPnl = tlist.Count == 0 ? 0.0 : totalPnl / tlist.Count;

            var decidedAt = row.ss.DecidedAt ?? row.ss.CreatedAt;

            outcomes.Add(new CouncilSignalOutcomeDto(
                signalId: id,
                decidedAt: decidedAt,
                symbol: row.s.Symbol,
                side: row.ss.Side,
                workerName: row.w.Name,
                expectedReturnPct: row.ss.ExpectedReturnPct,
                confidence: row.ss.Confidence,
                tradesFromSignal: tlist.Count,
                totalRealizedPnl: totalPnl,
                avgRealizedPnl: avgPnl,
                isWinner: totalPnl > 0
            ));
        }

        var winners = outcomes.Count(x => x.isWinner);
        var hitRate = outcomes.Count == 0 ? 0.0 : (double)winners / outcomes.Count;
        var avgPnlPerSignal = outcomes.Count == 0 ? 0.0 : outcomes.Average(x => x.totalRealizedPnl);

        return Ok(new CouncilPerformanceSummaryDto(hitRate, avgPnlPerSignal, outcomes.ToArray()));
    }

    // ---------------------------------------------------------------------
    // 3. WORKERS: overview + detail + risk editing
    // ---------------------------------------------------------------------

    public sealed record WorkerOverviewRichDto(
        int id,
        string name,
        string strategyName,
        string mode,
        string? ownerUserId,
        bool isActive,
        bool isTradingPaused,
        string? pauseReason,
        double initialCapital,
        double maxRiskPerTradePct,
        double maxDailyLossPct,
        double maxTotalDrawdownPct,
        double maxPositionSizePct,
        int maxOpenPositions,
        int maxTradesPerDay,
        double circuitBreakerLossPct,
        DateTime createdAt,
        DateTime? lastHeartbeatAt,
        DateTime? snapshotUtc,
        double? equity,
        double? cash,
        double? realizedPnl,
        double? unrealizedPnl,
        int? openPositions,
        int? totalTrades,
        double? grossExposure,
        double? netExposure,
        double? longExposure,
        double? shortExposure,
        double? drawdownPct,
        double? maxDrawdownPct,
        double? dailyRealizedPnl,
        string? riskFlagsJson
    );

    [HttpGet("workers")]
    public async Task<ActionResult<WorkerOverviewRichDto[]>> GetWorkersOverview(
        [FromQuery] string? mode = null)
    {
        var normalizedMode = NormalizeMode(mode);

        var workers = await _db.Workers.AsNoTracking()
            .Where(w => w.Mode == normalizedMode)
            .OrderBy(w => w.Id)
            .ToListAsync();

        var latestStats = await GetLatestWorkerStatsAsync(normalizedMode);

        var dtos = workers.Select(w =>
        {
            latestStats.TryGetValue(w.Id, out var ws);

            return new WorkerOverviewRichDto(
                id: w.Id,
                name: w.Name,
                strategyName: w.StrategyName,
                mode: w.Mode,
                ownerUserId: w.OwnerUserId,
                isActive: w.IsActive,
                isTradingPaused: w.IsTradingPaused,
                pauseReason: w.PauseReason,
                initialCapital: w.InitialCapital,
                maxRiskPerTradePct: w.MaxRiskPerTradePct,
                maxDailyLossPct: w.MaxDailyLossPct,
                maxTotalDrawdownPct: w.MaxTotalDrawdownPct,
                maxPositionSizePct: w.MaxPositionSizePct,
                maxOpenPositions: w.MaxOpenPositions,
                maxTradesPerDay: w.MaxTradesPerDay,
                circuitBreakerLossPct: w.CircuitBreakerLossPct,
                createdAt: w.CreatedAt,
                lastHeartbeatAt: w.LastHeartbeatAt,
                snapshotUtc: ws?.SnapshotUtc,
                equity: ws?.Equity,
                cash: ws?.Cash,
                realizedPnl: ws?.RealizedPnl,
                unrealizedPnl: ws?.UnrealizedPnl,
                openPositions: ws?.OpenPositions,
                totalTrades: ws?.TotalTrades,
                grossExposure: ws?.GrossExposure,
                netExposure: ws?.NetExposure,
                longExposure: ws?.LongExposure,
                shortExposure: ws?.ShortExposure,
                drawdownPct: ws?.DrawdownPct,
                maxDrawdownPct: ws?.MaxDrawdownPct,
                dailyRealizedPnl: ws?.DailyRealizedPnl,
                riskFlagsJson: ws?.RiskFlagsJson
            );
        }).ToArray();

        return Ok(dtos);
    }

    // *** RENAMED DTOS TO AVOID SCHEMA COLLISION ***

    public sealed record TradingDashboardWorkerEquityPointDto(
        DateTime snapshotUtc,
        double equity
    );

    public sealed record TradingDashboardWorkerDetailDto(
        WorkerOverviewRichDto overview,
        TradingDashboardWorkerEquityPointDto[] equityHistory
    );

    /// <summary>
    /// Worker detail: overview + equity curve.
    /// </summary>
    [HttpGet("workers/{workerId:int}")]
    public async Task<ActionResult<TradingDashboardWorkerDetailDto>> GetWorkerDetail(
        int workerId,
        [FromQuery] int daysBack = 7)
    {
        if (daysBack <= 0) daysBack = 7;

        var w = await _db.Workers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == workerId);

        if (w == null) return NotFound();

        var latestStatsDict = await GetLatestWorkerStatsAsync(w.Mode);
        latestStatsDict.TryGetValue(w.Id, out var ws);

        var overview = new WorkerOverviewRichDto(
            id: w.Id,
            name: w.Name,
            strategyName: w.StrategyName,
            mode: w.Mode,
            ownerUserId: w.OwnerUserId,
            isActive: w.IsActive,
            isTradingPaused: w.IsTradingPaused,
            pauseReason: w.PauseReason,
            initialCapital: w.InitialCapital,
            maxRiskPerTradePct: w.MaxRiskPerTradePct,
            maxDailyLossPct: w.MaxDailyLossPct,
            maxTotalDrawdownPct: w.MaxTotalDrawdownPct,
            maxPositionSizePct: w.MaxPositionSizePct,
            maxOpenPositions: w.MaxOpenPositions,
            maxTradesPerDay: w.MaxTradesPerDay,
            circuitBreakerLossPct: w.CircuitBreakerLossPct,
            createdAt: w.CreatedAt,
            lastHeartbeatAt: w.LastHeartbeatAt,
            snapshotUtc: ws?.SnapshotUtc,
            equity: ws?.Equity,
            cash: ws?.Cash,
            realizedPnl: ws?.RealizedPnl,
            unrealizedPnl: ws?.UnrealizedPnl,
            openPositions: ws?.OpenPositions,
            totalTrades: ws?.TotalTrades,
            grossExposure: ws?.GrossExposure,
            netExposure: ws?.NetExposure,
            longExposure: ws?.LongExposure,
            shortExposure: ws?.ShortExposure,
            drawdownPct: ws?.DrawdownPct,
            maxDrawdownPct: ws?.MaxDrawdownPct,
            dailyRealizedPnl: ws?.DailyRealizedPnl,
            riskFlagsJson: ws?.RiskFlagsJson
        );

        var since = DateTime.UtcNow.Date.AddDays(-daysBack);

        var equity = await _db.WorkerStats.AsNoTracking()
            .Where(s => s.WorkerId == workerId && s.SnapshotUtc >= since)
            .OrderBy(s => s.SnapshotUtc)
            .Select(s => new TradingDashboardWorkerEquityPointDto(s.SnapshotUtc, s.Equity))
            .ToListAsync();

        return Ok(new TradingDashboardWorkerDetailDto(overview, equity.ToArray()));
    }

    public sealed record UpdateWorkerConfigRequest(
        double? initialCapital,
        double? maxRiskPerTradePct,
        double? maxDailyLossPct,
        double? maxTotalDrawdownPct,
        double? maxPositionSizePct,
        int? maxOpenPositions,
        int? maxTradesPerDay,
        double? circuitBreakerLossPct,
        bool? isTradingPaused,
        string? pauseReason
    );

    [HttpPost("workers/{workerId:int}/config")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<WorkerOverviewRichDto>> UpdateWorkerConfig(
        int workerId,
        [FromBody] UpdateWorkerConfigRequest req)
    {
        var w = await _db.Workers.FirstOrDefaultAsync(x => x.Id == workerId);
        if (w == null) return NotFound();

        if (req.initialCapital.HasValue && req.initialCapital.Value > 0)
            w.InitialCapital = req.initialCapital.Value;

        if (req.maxRiskPerTradePct.HasValue && req.maxRiskPerTradePct.Value > 0)
            w.MaxRiskPerTradePct = req.maxRiskPerTradePct.Value;

        if (req.maxDailyLossPct.HasValue && req.maxDailyLossPct.Value > 0)
            w.MaxDailyLossPct = req.maxDailyLossPct.Value;

        if (req.maxTotalDrawdownPct.HasValue && req.maxTotalDrawdownPct.Value > 0)
            w.MaxTotalDrawdownPct = req.maxTotalDrawdownPct.Value;

        if (req.maxPositionSizePct.HasValue && req.maxPositionSizePct.Value > 0)
            w.MaxPositionSizePct = req.maxPositionSizePct.Value;

        if (req.maxOpenPositions.HasValue && req.maxOpenPositions.Value > 0)
            w.MaxOpenPositions = req.maxOpenPositions.Value;

        if (req.maxTradesPerDay.HasValue && req.maxTradesPerDay.Value > 0)
            w.MaxTradesPerDay = req.maxTradesPerDay.Value;

        if (req.circuitBreakerLossPct.HasValue && req.circuitBreakerLossPct.Value > 0)
            w.CircuitBreakerLossPct = req.circuitBreakerLossPct.Value;

        if (req.isTradingPaused.HasValue)
        {
            w.IsTradingPaused = req.isTradingPaused.Value;
            w.PauseReason = req.isTradingPaused.Value
                ? (req.pauseReason ?? "Manually paused")
                : null;
            w.LastPauseAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        var detail = await GetWorkerDetail(workerId, daysBack: 1);
        if (detail.Result is NotFoundResult)
            return NotFound();

        return Ok(detail.Value!.overview);
    }

    // ---------------------------------------------------------------------
    // 4. TRADES: blotter + PnL stats
    // ---------------------------------------------------------------------

    public sealed record TradeWithContextDto(
        long id,
        DateTime tradeTimeUtc,
        string side,
        double quantity,
        double price,
        double? realizedPnl,
        double? stopLoss,
        double? takeProfit,
        long? signalId,
        int workerId,
        string workerName,
        string strategyName,
        string symbol,
        string timeframeCode
    );

    [HttpGet("trades")]
    public async Task<ActionResult<TradeWithContextDto[]>> GetTrades(
        [FromQuery] string? mode = null,
        [FromQuery] int? workerId = null,
        [FromQuery] string? symbol = null,
        [FromQuery] string? side = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] bool? hasSignal = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 100;
        if (pageSize > 500) pageSize = 500;

        var normalizedMode = NormalizeMode(mode);

        var q =
            from t in _db.Trades.AsNoTracking()
            join w in _db.Workers.AsNoTracking() on t.WorkerId equals w.Id
            join s in _db.Symbols.AsNoTracking() on t.SymbolId equals s.Id
            join tf in _db.Timeframes.AsNoTracking() on t.TimeframeId equals tf.Id into tfJoin
            from tf in tfJoin.DefaultIfEmpty()
            where w.Mode == normalizedMode
            select new { t, w, s, tf };

        if (workerId.HasValue)
            q = q.Where(x => x.w.Id == workerId.Value);

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var sym = symbol.Trim().ToUpperInvariant();
            q = q.Where(x => x.s.Symbol == sym);
        }

        if (!string.IsNullOrWhiteSpace(side))
        {
            var sd = side.Trim().ToUpperInvariant();
            q = q.Where(x => x.t.Side == sd);
        }

        if (fromUtc.HasValue)
            q = q.Where(x => x.t.TradeTimeUtc >= fromUtc.Value);

        if (toUtc.HasValue)
            q = q.Where(x => x.t.TradeTimeUtc <= toUtc.Value);

        if (hasSignal.HasValue)
        {
            q = hasSignal.Value
                ? q.Where(x => x.t.SignalId != null)
                : q.Where(x => x.t.SignalId == null);
        }

        var skip = (page - 1) * pageSize;

        var rows = await q
            .OrderByDescending(x => x.t.TradeTimeUtc)
            .Skip(skip)
            .Take(pageSize)
            .Select(x => new TradeWithContextDto(
                x.t.Id,
                x.t.TradeTimeUtc,
                x.t.Side,
                x.t.Quantity,
                x.t.Price,
                x.t.RealizedPnl,
                x.t.StopLoss,
                x.t.TakeProfit,
                x.t.SignalId,
                x.w.Id,
                x.w.Name,
                x.w.StrategyName,
                x.s.Symbol,
                x.tf != null ? x.tf.Code : ""
            ))
            .ToListAsync();

        return Ok(rows.ToArray());
    }

    public sealed record WorkerPnlSummaryDto(
        int workerId,
        string workerName,
        string strategyName,
        int trades,
        double totalRealizedPnl,
        double avgPnlPerTrade,
        double grossProfit,
        double grossLoss,
        int winningTrades,
        int losingTrades,
        double? avgWin,
        double? avgLoss,
        double? profitFactor
    );

    [HttpGet("trades/pnl-per-worker")]
    public async Task<ActionResult<WorkerPnlSummaryDto[]>> GetPnlPerWorker(
        [FromQuery] string? mode = null)
    {
        var normalizedMode = NormalizeMode(mode);

        var q =
            from t in _db.Trades.AsNoTracking()
            join w in _db.Workers.AsNoTracking() on t.WorkerId equals w.Id
            where w.Mode == normalizedMode && t.RealizedPnl != null
            select new { t, w };

        var list = await q.ToListAsync();

        var grouped = list
            .GroupBy(x => new { x.w.Id, x.w.Name, x.w.StrategyName })
            .Select(g =>
            {
                var trades = g.Select(x => x.t).ToList();
                var n = trades.Count;
                var total = trades.Sum(t => t.RealizedPnl ?? 0.0);
                var avg = n == 0 ? 0.0 : total / n;

                var grossProfit = trades.Where(t => t.RealizedPnl > 0).Sum(t => t.RealizedPnl ?? 0.0);
                var grossLoss = trades.Where(t => t.RealizedPnl < 0).Sum(t => t.RealizedPnl ?? 0.0);

                var winningTrades = trades.Count(t => t.RealizedPnl > 0);
                var losingTrades = trades.Count(t => t.RealizedPnl < 0);

                var avgWin = winningTrades == 0
                    ? (double?)null
                    : trades.Where(t => t.RealizedPnl > 0).Average(t => t.RealizedPnl ?? 0.0);

                var avgLoss = losingTrades == 0
                    ? (double?)null
                    : trades.Where(t => t.RealizedPnl < 0).Average(t => t.RealizedPnl ?? 0.0);

                double? profitFactor = null;
                if (grossLoss < 0)
                {
                    profitFactor = grossProfit / Math.Abs(grossLoss);
                }

                return new WorkerPnlSummaryDto(
                    g.Key.Id,
                    g.Key.Name,
                    g.Key.StrategyName,
                    n,
                    total,
                    avg,
                    grossProfit,
                    grossLoss,
                    winningTrades,
                    losingTrades,
                    avgWin,
                    avgLoss,
                    profitFactor
                );
            })
            .OrderByDescending(x => x.totalRealizedPnl)
            .ToArray();

        return Ok(grouped);
    }

    public sealed record WorkerSymbolPnlDto(
        int workerId,
        string workerName,
        string symbol,
        int trades,
        double totalRealizedPnl,
        double avgPnlPerTrade
    );

    [HttpGet("trades/pnl-per-worker-symbol")]
    public async Task<ActionResult<WorkerSymbolPnlDto[]>> GetPnlPerWorkerPerSymbol(
        [FromQuery] string? mode = null)
    {
        var normalizedMode = NormalizeMode(mode);

        var q =
            from t in _db.Trades.AsNoTracking()
            join w in _db.Workers.AsNoTracking() on t.WorkerId equals w.Id
            join s in _db.Symbols.AsNoTracking() on t.SymbolId equals s.Id
            where w.Mode == normalizedMode && t.RealizedPnl != null
            select new { t, w, s };

        var list = await q.ToListAsync();

        var grouped = list
            .GroupBy(x => new { x.w.Id, x.w.Name, x.s.Symbol })
            .Select(g =>
            {
                var trades = g.Select(x => x.t).ToList();
                var n = trades.Count;
                var total = trades.Sum(t => t.RealizedPnl ?? 0.0);
                var avg = n == 0 ? 0.0 : total / n;

                return new WorkerSymbolPnlDto(
                    g.Key.Id,
                    g.Key.Name,
                    g.Key.Symbol,
                    n,
                    total,
                    avg
                );
            })
            .OrderBy(x => x.workerId)
            .ThenByDescending(x => x.totalRealizedPnl)
            .ToArray();

        return Ok(grouped);
    }

    // ---------------------------------------------------------------------
    // 5. RISK & LIMITS DASHBOARD
    // ---------------------------------------------------------------------

    public sealed record RiskSummaryDto(
        double totalEquity,
        double totalGrossExposure,
        double totalNetExposure,
        int totalWorkers,
        int pausedWorkers
    );

    [HttpGet("risk/summary")]
    public async Task<ActionResult<RiskSummaryDto>> GetRiskSummary(
        [FromQuery] string? mode = null)
    {
        var normalizedMode = NormalizeMode(mode);

        var workers = await _db.Workers.AsNoTracking()
            .Where(w => w.Mode == normalizedMode)
            .ToListAsync();

        var latestStats = await GetLatestWorkerStatsAsync(normalizedMode);

        double totalEquity = 0;
        double totalGross = 0;
        double totalNet = 0;

        foreach (var w in workers)
        {
            if (latestStats.TryGetValue(w.Id, out var ws))
            {
                totalEquity += ws.Equity;
                totalGross += ws.GrossExposure;
                totalNet += ws.NetExposure;
            }
        }

        var pausedWorkers = workers.Count(w => w.IsTradingPaused);

        return Ok(new RiskSummaryDto(
            totalEquity,
            totalGross,
            totalNet,
            workers.Count,
            pausedWorkers
        ));
    }

    public sealed record RiskNearLimitRowDto(
        int workerId,
        string workerName,
        string mode,
        double drawdownPct,
        double maxTotalDrawdownPct,
        int tradesToday,
        int maxTradesPerDay,
        bool nearDrawdownLimit,
        bool nearTradesLimit
    );

    [HttpGet("risk/near-limits")]
    public async Task<ActionResult<RiskNearLimitRowDto[]>> GetNearLimits(
        [FromQuery] string? mode = null)
    {
        var normalizedMode = NormalizeMode(mode);

        var workers = await _db.Workers.AsNoTracking()
            .Where(w => w.Mode == normalizedMode)
            .ToListAsync();

        var latestStats = await GetLatestWorkerStatsAsync(normalizedMode);

        var today = DateTime.UtcNow.Date;
        var tomorrow = today.AddDays(1);

        var tradesTodayQuery =
            from t in _db.Trades.AsNoTracking()
            join w in _db.Workers.AsNoTracking() on t.WorkerId equals w.Id
            where w.Mode == normalizedMode
                  && t.TradeTimeUtc >= today
                  && t.TradeTimeUtc < tomorrow
            select new { t.WorkerId, t.Id };

        var tradesTodayList = await tradesTodayQuery.ToListAsync();
        var tradesTodayCounts = tradesTodayList
            .GroupBy(x => x.WorkerId)
            .ToDictionary(g => g.Key, g => g.Count());

        var rows = new List<RiskNearLimitRowDto>();

        foreach (var w in workers)
        {
            latestStats.TryGetValue(w.Id, out var ws);
            if (ws == null) continue;

            var drawdown = ws.DrawdownPct;
            var limit = w.MaxTotalDrawdownPct;
            var tradesToday = tradesTodayCounts.GetValueOrDefault(w.Id, 0);
            var maxTrades = w.MaxTradesPerDay;

            bool nearD = limit > 0 && drawdown >= 0.7 * limit;
            bool nearT = maxTrades > 0 && tradesToday >= 0.7 * maxTrades;

            if (!nearD && !nearT) continue;

            rows.Add(new RiskNearLimitRowDto(
                w.Id,
                w.Name,
                w.Mode,
                drawdown,
                limit,
                tradesToday,
                maxTrades,
                nearD,
                nearT
            ));
        }

        return Ok(rows.ToArray());
    }

    public sealed record DailyWorkerPnlDto(
        int workerId,
        string workerName,
        DateTime dateUtc,
        double dailyRealizedPnl
    );

    [HttpGet("risk/daily-pnl")]
    public async Task<ActionResult<DailyWorkerPnlDto[]>> GetDailyPnl(
        [FromQuery] string? mode = null,
        [FromQuery] int daysBack = 30)
    {
        if (daysBack <= 0) daysBack = 30;

        var normalizedMode = NormalizeMode(mode);

        var workers = await _db.Workers.AsNoTracking()
            .Where(w => w.Mode == normalizedMode)
            .ToListAsync();
        var workerIds = workers.Select(w => w.Id).ToArray();

        if (workerIds.Length == 0)
            return Ok(Array.Empty<DailyWorkerPnlDto>());

        var since = DateTime.UtcNow.Date.AddDays(-daysBack);

        var stats = await _db.WorkerStats.AsNoTracking()
            .Where(ws => workerIds.Contains(ws.WorkerId) && ws.SnapshotUtc >= since)
            .ToListAsync();

        var rows = stats
            .GroupBy(ws => new { ws.WorkerId, Date = ws.SnapshotUtc.Date })
            .Select(g =>
            {
                var worker = workers.First(w => w.Id == g.Key.WorkerId);
                var dailyPnl = g.Max(x => x.DailyRealizedPnl);
                return new DailyWorkerPnlDto(
                    worker.Id,
                    worker.Name,
                    g.Key.Date,
                    dailyPnl
                );
            })
            .OrderBy(x => x.dateUtc)
            .ThenBy(x => x.workerId)
            .ToArray();

        return Ok(rows);
    }

    public sealed record UniverseRowDto(
        int id,
        string symbol,
        string timeframeCode,
        int timeframeMinutes,
        string dataProvider,
        double initialCapitalPerWorker,
        int historicalCandles,
        DateTime updatedUtc,
        int activeWorkers
    );

    [HttpGet("universe")]
    public async Task<ActionResult<UniverseRowDto[]>> GetUniverse()
    {
        var settings = await _db.TradingSettings.AsNoTracking()
            .OrderBy(s => s.Id)
            .ToListAsync();

        var symbols = settings.Select(s => s.Symbol).Distinct().ToArray();

        var activeWorkersBySymbol = await _db.Workers.AsNoTracking()
            .Where(w => w.IsActive && symbols.Contains(w.StrategyName) == false)
            .ToListAsync();

        int activeWorkers = activeWorkersBySymbol.Count;

        var dtos = settings
            .Select(s => new UniverseRowDto(
                s.Id,
                s.Symbol,
                s.TimeframeCode,
                s.TimeframeMinutes,
                s.DataProvider,
                s.InitialCapitalPerWorker,
                s.HistoricalCandles,
                s.UpdatedUtc,
                activeWorkers
            ))
            .ToArray();

        return Ok(dtos);
    }
}
