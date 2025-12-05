// honey_badger_api/Controllers/WorkersController.cs
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using honey_badger_api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class WorkersController : ControllerBase
{
    private readonly NvdaTradingDbContext _db;

    // All shared trading bots are "owned" by this admin Identity user.
    private const string AdminOwnerId = "6a3c8fcb-d846-4028-ad28-0df42f57b7e8";

    public WorkersController(NvdaTradingDbContext db)
    {
        _db = db;
    }

    // ---------- helpers ----------

    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? User.FindFirst("sub")?.Value;
    }

    private bool IsAdmin() =>
        string.Equals(GetCurrentUserId(), AdminOwnerId, StringComparison.OrdinalIgnoreCase);

    private IQueryable<NvdaTradingWorker> FilterWorkersForCaller(IQueryable<NvdaTradingWorker> query)
    {
        if (IsAdmin())
            return query;

        // normal users see admin-owned bots
        return query.Where(w => w.OwnerUserId == AdminOwnerId);
    }

    private async Task ResetWorkerStateAsync(int workerId, double baseCapital)
    {
        // Wipe all trading history for this worker and rebuild per-timeframe balances.
        var recs = _db.CouncilRecommendations.Where(r => r.WorkerId == workerId);
        _db.CouncilRecommendations.RemoveRange(recs);

        var signals = _db.StrategySignals.Where(s => s.WorkerId == workerId);
        _db.StrategySignals.RemoveRange(signals);

        var trades = _db.Trades.Where(t => t.WorkerId == workerId);
        _db.Trades.RemoveRange(trades);

        var stats = _db.WorkerStats.Where(s => s.WorkerId == workerId);
        _db.WorkerStats.RemoveRange(stats);

        var balances = _db.WorkerTimeframeBalances.Where(b => b.WorkerId == workerId);
        _db.WorkerTimeframeBalances.RemoveRange(balances);

        var tfs = await _db.Timeframes.AsNoTracking().ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var tf in tfs)
        {
            _db.WorkerTimeframeBalances.Add(new WorkerTimeframeBalance
            {
                WorkerId = workerId,
                TimeframeId = tf.Id,
                InitialCapital = baseCapital,
                Cash = baseCapital,
                RealizedPnl = 0,
                UnrealizedPnl = 0,
                Equity = baseCapital,
                LastPrice = null,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        // Seed a fresh stats snapshot so UI immediately reflects the reset.
        var snapshot = new NvdaTradingWorkerStats
        {
            WorkerId = workerId,
            SnapshotUtc = now,
            Equity = baseCapital,
            Cash = baseCapital,
            UnrealizedPnl = 0,
            RealizedPnl = 0,
            OpenPositions = 0,
            TotalTrades = 0,
            GrossExposure = 0,
            NetExposure = 0,
            LongExposure = 0,
            ShortExposure = 0,
            DrawdownPct = 0,
            MaxDrawdownPct = 0,
            DailyRealizedPnl = 0,
            RollingSharpe30d = null,
            RollingSortino30d = null,
            RiskFlagsJson = "[]"
        };
        await _db.WorkerStats.AddAsync(snapshot);
    }

    // ---------- DTOs ----------

    // DTOs returned by /api/nvda-trading/workers
        public sealed class WorkerSummaryDto
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public string StrategyName { get; init; } = "";
        public string Mode { get; init; } = "";

        /// <summary>
        /// 1 = worker enabled / can be used; 0 = disabled (not shown in UI).
        /// </summary>
        public bool IsActive { get; init; }

        /// <summary>
        /// true = trading paused; false = trading allowed for this worker.
        /// Backend maps isActive/isTradingPaused from DB.
        /// </summary>
        public bool IsTradingPaused { get; init; }

        public double InitialCapital { get; init; }

        /// <summary>
        /// Owner user id (string from Auth0 / identity provider) or null for admin-owned.
        /// </summary>
        public string? OwnerUserId { get; init; }

        /// <summary>
        /// Latest equity snapshot from worker_stats, if any.
        /// </summary>
        public double? LatestEquity { get; init; }

        /// <summary>
        /// Latest cash snapshot from worker_stats, if any.
        /// </summary>
        public double? LatestCash { get; init; }

        /// <summary>
        /// When the latest worker_stats row was recorded (UTC).
        /// </summary>
        public DateTime? LatestStatsAtUtc { get; init; }

        /// <summary>
        /// Win-rate percentage for this worker based on virtual trades
        /// (only realized trades considered). Null if no trades.
        /// </summary>
        public double? SuccessRatePct { get; init; }

        /// <summary>
        /// Number of trades used to compute SuccessRatePct. Null if none.
        /// </summary>
        public int? TradesSampleCount { get; init; }

        /// <summary>
        /// Which container currently owns this worker (from workers.runtime_instance_id).
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Latest heartbeat timestamp from the worker container (UTC).
        /// </summary>
        public DateTime? LastHeartbeatAtUtc { get; init; }

        /// <summary>
        /// Per-timeframe win rates (sampled from trades.timeframe_id).
        /// </summary>
        public TimeframeWinRateDto[] TimeframeStats { get; init; } = Array.Empty<TimeframeWinRateDto>();

        /// <summary>
        /// Best-performing timeframe code (by win-rate) if available.
        /// </summary>
        public string? BestTimeframeCode { get; init; }

        /// <summary>
        /// Win rate for the best-performing timeframe (pct).
        /// </summary>
        public double? BestTimeframeSuccessRatePct { get; init; }
    }

        public sealed class TimeframeWinRateDto
        {
            public int TimeframeId { get; init; }
            public string TimeframeCode { get; init; } = "";
            public int TimeframeMinutes { get; init; }
            public double? SuccessRatePct { get; init; }
            public int? TradesSampleCount { get; init; }
            public double? Equity { get; init; }
            public double? Cash { get; init; }
            public double? BaseCapital { get; init; }
        }

    public sealed class UpdateWorkerModeRequest
    {
        /// <summary>"PAPER" or "LIVE"</summary>
        public string Mode { get; init; } = "PAPER";
    }

    public sealed class UpdateWorkerActiveRequest
    {
        /// <summary>
        /// true = trading active, false = trading paused, only for workers with IsActive = 1.
        /// </summary>
        public bool IsActive { get; init; }
    }

    public sealed class AllocateWorkerDailyRequest
    {
        /// <summary>Daily budget / capital allocated to this worker.</summary>
        public double DailyCapital { get; init; }
    }

    public sealed class ResetWorkerDailyRequest
    {
        /// <summary>Optional new daily capital; if null, keep existing InitialCapital.</summary>
        public double? NewDailyCapital { get; init; }

        /// <summary>Optional note; stored in PauseReason and visible on Python side.</summary>
        public string? ResetNote { get; init; }
    }

    // ---------- endpoints ----------

    [HttpGet]
    public async Task<ActionResult<WorkerSummaryDto[]>> GetWorkers()
    {
        var baseQuery = _db.Workers.AsNoTracking();
        var filtered = FilterWorkersForCaller(baseQuery);

        var workers = await filtered.ToListAsync();
        if (workers.Count == 0)
            return Ok(Array.Empty<WorkerSummaryDto>());

        var workerIds = workers.Select(w => w.Id).ToArray();

        // Latest stats per worker from worker_stats
        var latestStats = await _db.WorkerStats
            .AsNoTracking()
            .Where(s => workerIds.Contains(s.WorkerId))
            .GroupBy(s => s.WorkerId)
            .Select(g => g.OrderByDescending(s => s.SnapshotUtc).First())
            .ToListAsync();

        var statsByWorker = latestStats.ToDictionary(x => x.WorkerId, x => x);

        // NEW: aggregate virtual PnL stats per worker from Trades.
        // Using a 90-day window; adjust if you want a longer history.
        var since = DateTime.UtcNow.AddDays(-90);

        var tradeAgg = await _db.Trades
            .AsNoTracking()
            .Where(t =>
                workerIds.Contains(t.WorkerId) &&
                t.TradeTimeUtc >= since &&
                t.RealizedPnl.HasValue)
            .GroupBy(t => t.WorkerId)
            .Select(g => new
            {
                WorkerId = g.Key,
                Total = g.Count(),
                Wins = g.Count(t => t.RealizedPnl!.Value > 0d),
            })
            .ToListAsync();

        var successByWorker = tradeAgg.ToDictionary(x => x.WorkerId, x => x);

        // Per-timeframe win rates (from trades.timeframe_id)
        var tfAgg = await _db.Trades
            .AsNoTracking()
            .Where(t =>
                workerIds.Contains(t.WorkerId) &&
                t.TradeTimeUtc >= since &&
                t.RealizedPnl.HasValue &&
                t.TimeframeId.HasValue)
            .GroupBy(t => new { t.WorkerId, tfId = t.TimeframeId!.Value })
            .Select(g => new
            {
                WorkerId = g.Key.WorkerId,
                TimeframeId = g.Key.tfId,
                Total = g.Count(),
                Wins = g.Count(t => t.RealizedPnl!.Value > 0d)
            })
            .ToListAsync();

        // Per-timeframe balances are the source of truth for isolated capital
        var tfBalances = await _db.WorkerTimeframeBalances
            .AsNoTracking()
            .Where(b => workerIds.Contains(b.WorkerId))
            .ToListAsync();

        var timeframes = await _db.Timeframes.AsNoTracking().ToDictionaryAsync(tf => tf.Id);

        // Helper lookup for per-worker per-timeframe aggregates
        var tfAggLookup = tfAgg.ToLookup(x => (x.WorkerId, x.TimeframeId));
        var tfBalLookup = tfBalances.ToLookup(x => (x.WorkerId, x.TimeframeId));

        var result = workers
            .Select(w =>
            {
                statsByWorker.TryGetValue(w.Id, out var st);
                successByWorker.TryGetValue(w.Id, out var agg);

                double? successRatePct = null;
                int? tradesSampleCount = null;

                if (agg != null && agg.Total > 0)
                {
                    tradesSampleCount = agg.Total;
                    successRatePct = 100.0 * agg.Wins / agg.Total;
                }

                var tfIdsForWorker = tfBalances
                    .Where(b => b.WorkerId == w.Id)
                    .Select(b => b.TimeframeId)
                    .Concat(timeframes.Values.Select(tf => tf.Id)) // ensure all TFs appear even if no balance yet
                    .Distinct()
                    .ToList();

                var tfStats = tfIdsForWorker
                    .Select(tfId =>
                    {
                        if (!timeframes.TryGetValue(tfId, out var tf))
                            return null;

                        var agg = tfAggLookup[(w.Id, tfId)].FirstOrDefault();
                        var bal = tfBalLookup[(w.Id, tfId)].FirstOrDefault();

                        double? sr = null;
                        int? count = null;
                        if (agg != null && agg.Total > 0)
                        {
                            sr = 100.0 * agg.Wins / agg.Total;
                            count = agg.Total;
                        }

                        return new TimeframeWinRateDto
                        {
                            TimeframeId = tf.Id,
                            TimeframeCode = tf.Code,
                            TimeframeMinutes = tf.Minutes,
                            SuccessRatePct = sr,
                            TradesSampleCount = count,
                            Equity = bal?.Equity,
                            Cash = bal?.Cash,
                            BaseCapital = bal?.InitialCapital ?? w.InitialCapital
                        };
                    })
                    .Where(x => x != null)
                    .OrderBy(x => x!.TimeframeMinutes)
                    .Cast<TimeframeWinRateDto>()
                    .ToArray();

                string? bestTfCode = null;
                double? bestTfWin = null;
                var tfWithTrades = tfStats
                    .Where(t => t.TradesSampleCount.HasValue && t.TradesSampleCount.Value > 0)
                    .ToArray();
                if (tfWithTrades.Length > 0)
                {
                    var best = tfWithTrades
                        .OrderByDescending(t => t.SuccessRatePct ?? 0)
                        .ThenByDescending(t => t.TradesSampleCount ?? 0)
                        .First();
                    bestTfCode = best.TimeframeCode;
                    bestTfWin = best.SuccessRatePct;
                }

                return new WorkerSummaryDto
                {
                    Id = w.Id,
                    Name = w.Name,
                    StrategyName = w.StrategyName,
                    Mode = w.Mode,
                    IsActive = w.IsActive,
                    IsTradingPaused = w.IsTradingPaused,
                    InitialCapital = w.InitialCapital,
                    OwnerUserId = w.OwnerUserId,
                    LatestEquity = st?.Equity ?? w.InitialCapital,
                    LatestCash = st?.Cash ?? w.InitialCapital,
                    LatestStatsAtUtc = st?.SnapshotUtc,
                    SuccessRatePct = successRatePct,
                    TradesSampleCount = tradesSampleCount,
                    RuntimeInstanceId = w.RuntimeInstanceId,
                    LastHeartbeatAtUtc = w.LastHeartbeatAt,
                    TimeframeStats = tfStats,
                    BestTimeframeCode = bestTfCode,
                    BestTimeframeSuccessRatePct = bestTfWin
                };
            })
            .ToArray();
        return Ok(result);
    }


    [HttpPut("{workerId:int}/mode")]
    public async Task<IActionResult> UpdateMode(int workerId, [FromBody] UpdateWorkerModeRequest req)
    {
        var modeUpper = (req.Mode ?? "").Trim().ToUpperInvariant();
        if (modeUpper != "PAPER" && modeUpper != "LIVE")
            return BadRequest("Mode must be 'PAPER' or 'LIVE'.");

        var workerQuery = FilterWorkersForCaller(_db.Workers);
        var worker = await workerQuery.FirstOrDefaultAsync(w => w.Id == workerId);
        if (worker == null)
            return NotFound();

        worker.Mode = modeUpper;
        worker.LastPauseAt = DateTime.UtcNow;
        worker.PauseReason = $"Mode set to {modeUpper} via API at {DateTime.UtcNow:O}";

        if (string.IsNullOrWhiteSpace(worker.OwnerUserId))
            worker.OwnerUserId = AdminOwnerId;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{workerId:int}/active")]
    public async Task<IActionResult> UpdateActive(int workerId, [FromBody] UpdateWorkerActiveRequest req)
    {
        var workerQuery = FilterWorkersForCaller(_db.Workers);
        var worker = await workerQuery.FirstOrDefaultAsync(w => w.Id == workerId);
        if (worker == null)
            return NotFound();

        // IsActive = 0 means fully disabled and not in use; UI should not show those workers.
        if (!worker.IsActive)
            return BadRequest("Worker is disabled and cannot be started via this API.");

        var tradingShouldBeActive = req.IsActive;

        worker.IsTradingPaused = !tradingShouldBeActive;
        worker.LastPauseAt = DateTime.UtcNow;
        worker.PauseReason = tradingShouldBeActive ? "Unpaused via API" : "Paused via API";

        if (string.IsNullOrWhiteSpace(worker.OwnerUserId))
            worker.OwnerUserId = AdminOwnerId;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{workerId:int}/daily-capital")]
    public async Task<IActionResult> AllocateDailyCapital(
        int workerId,
        [FromBody] AllocateWorkerDailyRequest req)
    {
        if (req.DailyCapital <= 0)
            return BadRequest("DailyCapital must be > 0.");

        var workerQuery = FilterWorkersForCaller(_db.Workers);
        var worker = await workerQuery.FirstOrDefaultAsync(w => w.Id == workerId);
        if (worker == null)
            return NotFound();

        worker.InitialCapital = req.DailyCapital;
        worker.PauseReason =
            $"Daily capital set to {req.DailyCapital:F2} via API at {DateTime.UtcNow:O}";

        if (string.IsNullOrWhiteSpace(worker.OwnerUserId))
            worker.OwnerUserId = AdminOwnerId;

        await ResetWorkerStateAsync(workerId, worker.InitialCapital);

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{workerId:int}/reset-daily")]
    public async Task<IActionResult> ResetDaily(
        int workerId,
        [FromBody] ResetWorkerDailyRequest req)
    {
        var workerQuery = FilterWorkersForCaller(_db.Workers);
        var worker = await workerQuery.FirstOrDefaultAsync(w => w.Id == workerId);
        if (worker == null)
            return NotFound();

        if (req.NewDailyCapital.HasValue)
        {
            if (req.NewDailyCapital.Value <= 0)
                return BadRequest("NewDailyCapital must be > 0.");

            worker.InitialCapital = req.NewDailyCapital.Value;
        }

        await ResetWorkerStateAsync(workerId, worker.InitialCapital);

        worker.PauseReason = req.ResetNote ?? $"Daily reset via API at {DateTime.UtcNow:O}";
        worker.LastPauseAt = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(worker.OwnerUserId))
            worker.OwnerUserId = AdminOwnerId;

        await _db.SaveChangesAsync();
        return NoContent();
    }
}
