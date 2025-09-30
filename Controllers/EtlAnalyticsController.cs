using System;
using System.Linq;
using System.Threading.Tasks;
using honey_badger_api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Controllers;

[ApiController]
[Route("api/etl")]
[Authorize(Roles = "Admin")]
public class EtlAnalyticsController : ControllerBase
{
    private readonly NvdaAlphaDbContext _db;
    public EtlAnalyticsController(NvdaAlphaDbContext db) => _db = db;

    [HttpGet("overview")]
    public async Task<IActionResult> Overview()
    {
        var since = DateTime.UtcNow.AddDays(-1);

        // sequential, no Task.WhenAll on the same DbContext
        var companies = await _db.Companies.AsNoTracking().CountAsync();
        var articlesGdelt = await _db.NewsArticles.AsNoTracking().CountAsync();
        var articlesExt = await _db.NewsArticlesExt.AsNoTracking().CountAsync();
        var requestsTotal = await _db.RequestLogs.AsNoTracking().CountAsync();
        var jobs = await _db.KeywordJobs.AsNoTracking().CountAsync();

        var perSource24h = await _db.RequestLogs.AsNoTracking()
            .Where(r => r.AttemptAt >= since)
            .GroupBy(r => r.Source)
            .Select(g => new
            {
                source = g.Key!,
                count = g.Count(),
                ok = g.Count(x => x.Outcome == "OK"),
                bad = g.Count(x => x.Outcome == "BAD"),
            })
            .OrderByDescending(x => x.count)
            .ToListAsync();

        return Ok(new
        {
            companies,
            articlesGdelt,
            articlesExt,
            requestsTotal,
            jobs,
            perSource24h
        });
    }

    [HttpGet("counts")]
    public async Task<IActionResult> CountsByCompany()
    {
        var rows = await _db.ArticleCompanyMap.AsNoTracking()
            .GroupBy(x => x.CompanyId)
            .Select(g => new { CompanyId = g.Key, Total = g.Count() })
            .Join(_db.Companies.AsNoTracking(),
                  g => g.CompanyId,
                  c => c.Id,
                  (g, c) => new { symbol = c.Symbol, total = g.Total })
            .OrderByDescending(x => x.total)
            .ToListAsync();

        return Ok(rows);
    }
}
