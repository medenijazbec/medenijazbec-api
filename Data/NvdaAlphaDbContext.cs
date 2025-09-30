// path: honey_badger_api/Data/NvdaAlphaDbContext.cs
using Microsoft.EntityFrameworkCore;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace honey_badger_api.Data;

public sealed class NvdaAlphaDbContext : DbContext
{
    public NvdaAlphaDbContext(DbContextOptions<NvdaAlphaDbContext> options) : base(options) { }

    public DbSet<NvdaCompany> Companies => Set<NvdaCompany>();
    public DbSet<NvdaDailyPrice> DailyPrices => Set<NvdaDailyPrice>();
    public DbSet<NvdaNewsArticle> NewsArticles => Set<NvdaNewsArticle>();
    public DbSet<NvdaNewsArticleExt> NewsArticlesExt => Set<NvdaNewsArticleExt>();
    public DbSet<NvdaArticleCompanyMap> ArticleCompanyMap => Set<NvdaArticleCompanyMap>();
    public DbSet<NvdaArticleCompanyMapExt> ArticleCompanyMapExt => Set<NvdaArticleCompanyMapExt>();
    public DbSet<NvdaEtlCheckpoint> EtlCheckpoints => Set<NvdaEtlCheckpoint>();
    public DbSet<NvdaGdeltProbeBadDay> GdeltProbeBadDays => Set<NvdaGdeltProbeBadDay>();
    public DbSet<NvdaRequestLog> RequestLogs => Set<NvdaRequestLog>();
    public DbSet<NvdaKeywordJob> KeywordJobs => Set<NvdaKeywordJob>();
    public DbSet<NvdaJobProgress> JobProgress => Set<NvdaJobProgress>();
    public DbSet<NvdaWorkerHeartbeat> WorkerHeartbeats => Set<NvdaWorkerHeartbeat>();
    public DbSet<NvdaApiAccount> ApiAccounts => Set<NvdaApiAccount>();
    public DbSet<NvdaRateLimitEvent> RateLimitEvents => Set<NvdaRateLimitEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Composite keys / unique indexes that exist in your DDL
        b.Entity<NvdaArticleCompanyMap>().HasKey(x => new { x.ArticleId, x.CompanyId });
        b.Entity<NvdaArticleCompanyMapExt>().HasKey(x => new { x.ArticleId, x.CompanyId });
        b.Entity<NvdaGdeltProbeBadDay>().HasKey(x => new { x.Symbol, x.BadDate });
        b.Entity<NvdaEtlCheckpoint>().HasKey(x => x.EtlKey);
        b.Entity<NvdaJobProgress>()
            .HasIndex(x => new { x.JobId, x.Source })
            .IsUnique();
        b.Entity<NvdaRequestLog>()
            .HasIndex(x => new { x.Source, x.AttemptAt });
        b.Entity<NvdaRequestLog>()
            .HasIndex(x => new { x.Source, x.Symbol, x.AttemptAt });
        b.Entity<NvdaRequestLog>()
            .HasIndex(x => new { x.WindowStart, x.WindowEnd });
        b.Entity<NvdaApiAccount>()
            .HasIndex(x => new { x.Source, x.ApiKey })
            .IsUnique();
        b.Entity<NvdaApiAccount>()
            .HasIndex(x => new { x.Source, x.IsActive, x.ExhaustedUntil, x.LastUsedAt, x.UsageCount })
            .HasDatabaseName("idx_api_accounts_active");
    }
}

// =============== ENTITIES (snake_case mappings) ===============

[Table("companies")]
public sealed class NvdaCompany
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("symbol")] public string Symbol { get; set; } = "";
    [Column("name")] public string Name { get; set; } = "";
    [Column("sector")] public string? Sector { get; set; }
    [Column("industry")] public string? Industry { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("daily_prices")]
public sealed class NvdaDailyPrice
{
    [Key, Column("id")] public long Id { get; set; }
    [Column("company_id")] public int CompanyId { get; set; }
    [Column("price_date")] public DateTime PriceDate { get; set; }
    [Column("open")] public decimal? Open { get; set; }
    [Column("high")] public decimal? High { get; set; }
    [Column("low")] public decimal? Low { get; set; }
    [Column("close")] public decimal? Close { get; set; }
    [Column("adj_close")] public decimal? AdjClose { get; set; }
    [Column("volume")] public long? Volume { get; set; }
    [Column("dividends")] public decimal? Dividends { get; set; }
    [Column("stock_splits")] public decimal? StockSplits { get; set; }
}

[Table("news_articles")]
public sealed class NvdaNewsArticle
{
    [Key, Column("id")] public long Id { get; set; }
    [Column("url_hash")] public string UrlHash { get; set; } = "";
    [Column("url")] public string Url { get; set; } = "";
    [Column("title")] public string? Title { get; set; }
    [Column("source_domain")] public string? SourceDomain { get; set; }
    [Column("language")] public string? Language { get; set; }
    [Column("country")] public string? Country { get; set; }
    [Column("published_at")] public DateTime? PublishedAt { get; set; }
    [Column("gdelt_score")] public float? GdeltScore { get; set; }
    [Column("gdelt_source")] public string? GdeltSource { get; set; }
    [Column("raw_json")] public string? RawJson { get; set; } // Pomelo can map JSON to string
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("news_articles_ext")]
public sealed class NvdaNewsArticleExt
{
    [Key, Column("id")] public long Id { get; set; }
    [Column("url_hash")] public string UrlHash { get; set; } = "";
    [Column("url")] public string Url { get; set; } = "";
    [Column("title")] public string? Title { get; set; }
    [Column("source_name")] public string SourceName { get; set; } = "";
    [Column("source_domain")] public string? SourceDomain { get; set; }
    [Column("language")] public string? Language { get; set; }
    [Column("country")] public string? Country { get; set; }
    [Column("published_at")] public DateTime? PublishedAt { get; set; }
    [Column("source_score")] public float? SourceScore { get; set; }
    [Column("raw_json")] public string? RawJson { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("article_company_map")]
public sealed class NvdaArticleCompanyMap
{
    [Column("article_id")] public long ArticleId { get; set; }
    [Column("company_id")] public int CompanyId { get; set; }
}

[Table("article_company_map_ext")]
public sealed class NvdaArticleCompanyMapExt
{
    [Column("article_id")] public long ArticleId { get; set; }
    [Column("company_id")] public int CompanyId { get; set; }
}

[Table("etl_checkpoints")]
public sealed class NvdaEtlCheckpoint
{
    [Key, Column("etl_key")] public string EtlKey { get; set; } = "";
    [Column("checkpoint_value")] public string CheckpointValue { get; set; } = "";
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}

[Table("gdelt_probe_bad_days")]
public sealed class NvdaGdeltProbeBadDay
{
    [Column("symbol")] public string Symbol { get; set; } = "";
    [Column("bad_date")] public DateTime BadDate { get; set; }
}

[Table("request_log")]
public sealed class NvdaRequestLog
{
    [Key, Column("id")] public long Id { get; set; }
    [Column("source")] public string Source { get; set; } = "";
    [Column("symbol")] public string? Symbol { get; set; }
    [Column("endpoint")] public string? Endpoint { get; set; }
    [Column("url")] public string? Url { get; set; }
    [Column("status_code")] public int? StatusCode { get; set; }
    [Column("outcome")] public string Outcome { get; set; } = "";
    [Column("reason")] public string? Reason { get; set; }
    [Column("window_start")] public DateTime? WindowStart { get; set; }
    [Column("window_end")] public DateTime? WindowEnd { get; set; }
    [Column("attempt_at")] public DateTime AttemptAt { get; set; }
    [Column("response_meta")] public string? ResponseMeta { get; set; }
    [Column("account_id")] public int? AccountId { get; set; }
}

[Table("keyword_jobs")]
public sealed class NvdaKeywordJob
{
    [Key, Column("id")] public long Id { get; set; }
    [Column("keyword")] public string Keyword { get; set; } = "";
    [Column("start_utc")] public DateTime StartUtc { get; set; }
    [Column("end_utc")] public DateTime EndUtc { get; set; }
    [Column("status")] public string Status { get; set; } = "";
    [Column("priority")] public int Priority { get; set; }
    [Column("assigned_to")] public string? AssignedTo { get; set; }
    [Column("lease_expires_at")] public DateTime? LeaseExpiresAt { get; set; }
    [Column("last_progress_at")] public DateTime? LastProgressAt { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("job_progress")]
public sealed class NvdaJobProgress
{
    [Key, Column("id")] public long Id { get; set; }
    [Column("job_id")] public long JobId { get; set; }
    [Column("source")] public string Source { get; set; } = "";
    [Column("checkpoint_utc")] public DateTime? CheckpointUtc { get; set; }
    [Column("message")] public string? Message { get; set; }
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}

[Table("worker_heartbeats")]
public sealed class NvdaWorkerHeartbeat
{
    [Key, Column("worker_id")] public string WorkerId { get; set; } = "";
    [Column("hostname")] public string? Hostname { get; set; }
    [Column("pid")] public int? Pid { get; set; }
    [Column("started_at")] public DateTime StartedAt { get; set; }
    [Column("heartbeat_at")] public DateTime HeartbeatAt { get; set; }
    [Column("current_job_id")] public long? CurrentJobId { get; set; }
    [Column("current_source")] public string? CurrentSource { get; set; }
    [Column("current_keyword")] public string? CurrentKeyword { get; set; }
    [Column("current_window_start")] public DateTime? CurrentWindowStart { get; set; }
    [Column("current_window_end")] public DateTime? CurrentWindowEnd { get; set; }
}

[Table("api_accounts")]
public sealed class NvdaApiAccount
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("source")] public string Source { get; set; } = "";
    [Column("account_label")] public string? AccountLabel { get; set; }
    [Column("api_key")] public string ApiKey { get; set; } = "";
    [Column("daily_quota")] public int? DailyQuota { get; set; }
    [Column("reset_cron")] public string? ResetCron { get; set; }
    [Column("exhausted_until")] public DateTime? ExhaustedUntil { get; set; }
    [Column("is_active")] public bool IsActive { get; set; }
    [Column("usage_count")] public int UsageCount { get; set; }
    [Column("last_used_at")] public DateTime? LastUsedAt { get; set; }
}

[Table("rate_limit_events")]
public sealed class NvdaRateLimitEvent
{
    [Key, Column("id")] public long Id { get; set; }
    [Column("source")] public string Source { get; set; } = "";
    [Column("account_id")] public int AccountId { get; set; }
    [Column("keyword")] public string? Keyword { get; set; }
    [Column("window_start")] public DateTime? WindowStart { get; set; }
    [Column("window_end")] public DateTime? WindowEnd { get; set; }
    [Column("seen_at")] public DateTime SeenAt { get; set; }
    [Column("until")] public DateTime? Until { get; set; }
}
