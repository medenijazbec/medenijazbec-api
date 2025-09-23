using honey_badger_api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Data
{
    public class AppUser : IdentityUser
    {
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class AppDbContext : IdentityDbContext<AppUser, IdentityRole, string>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Project> Projects => Set<Project>();
        public DbSet<ProjectImage> ProjectImages => Set<ProjectImage>();
        public DbSet<BlogPost> BlogPosts => Set<BlogPost>();
        public DbSet<BlogTag> BlogTags => Set<BlogTag>();
        public DbSet<BlogPostTag> BlogPostTags => Set<BlogPostTag>();
        public DbSet<FitnessDaily> FitnessDaily => Set<FitnessDaily>();
        public DbSet<ExerciseSession> ExerciseSessions => Set<ExerciseSession>();
        public DbSet<ContactInquiry> ContactInquiries => Set<ContactInquiry>();
        public DbSet<AnimationGroup> AnimationGroups => Set<AnimationGroup>();
        public DbSet<AnimationGroupItem> AnimationGroupItems => Set<AnimationGroupItem>();
        public DbSet<RequestLog> RequestLogs => Set<RequestLog>();
        public DbSet<DailyTopIp> DailyTopIps => Set<DailyTopIp>();
        public DbSet<LoginSession> LoginSessions => Set<LoginSession>();
        public DbSet<IpBan> IpBans => Set<IpBan>();
        public DbSet<MetricSnapshot> MetricSnapshots => Set<MetricSnapshot>();
        public DbSet<BadgerSettings> BadgerSettings => Set<BadgerSettings>();
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);

            b.Entity<RequestLog>()
       .HasIndex(x => x.StartedUtc);
            b.Entity<RequestLog>()
                .HasIndex(x => new { x.Path, x.StartedUtc });
            b.Entity<RequestLog>()
                .HasIndex(x => new { x.Ip, x.StartedUtc });
            b.Entity<RequestLog>()
                .HasIndex(x => x.StatusCode);

            b.Entity<DailyTopIp>()
                .HasIndex(x => new { x.Day, x.Rank }).IsUnique();
            b.Entity<DailyTopIp>()
                .HasIndex(x => new { x.Day, x.Ip });

            b.Entity<LoginSession>()
                .HasIndex(x => new { x.UserId, x.CreatedUtc });
            b.Entity<LoginSession>()
                .HasIndex(x => x.Email);

            b.Entity<IpBan>()
                .HasIndex(x => new { x.Kind, x.Value });

            b.Entity<MetricSnapshot>()
                .HasIndex(x => x.WindowStartUtc);

            b.Entity<Project>()
                            .HasMany(p => p.Images)
                            .WithOne()
                            .HasForeignKey(pi => pi.ProjectId)
                            .OnDelete(DeleteBehavior.Cascade);

            b.Entity<Project>()
                .HasMany(p => p.Images)
                .WithOne()
                .HasForeignKey(pi => pi.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            b.Entity<Project>()
                .HasIndex(p => p.Slug)
                .IsUnique();

            b.Entity<Project>()
                .HasIndex(p => new { p.Kind, p.Published });

            b.Entity<ProjectImage>()
                .HasIndex(pi => new { pi.ProjectId, pi.SortOrder });

            // Map property to existing MySQL JSON column "TechStack"
            b.Entity<Project>()
                .Property(p => p.TechStackJson)
                .HasColumnName("TechStack")
                .HasColumnType("json");

            // Blog
            b.Entity<BlogPost>()
                .HasIndex(p => p.Slug).IsUnique();
            b.Entity<BlogPostTag>()
                .HasKey(x => new { x.BlogPostId, x.BlogTagId });



            // Fitness
            b.Entity<FitnessDaily>()
                .HasIndex(x => new { x.UserId, x.Day })
                .IsUnique();

            // Inquiries
            b.Entity<ContactInquiry>()
                .HasIndex(x => x.Status);

            // Anim Groups
            b.Entity<AnimationGroup>().HasIndex(g => g.Slug).IsUnique();

            b.Entity<AnimationGroup>().HasIndex(g => new { g.Category, g.Published });
            b.Entity<AnimationGroup>().HasIndex(g => new { g.Category, g.IsDefaultForCategory });


            b.Entity<AnimationGroupItem>()
                .HasOne(i => i.Group)
                .WithMany(g => g.Items)
                .HasForeignKey(i => i.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            b.Entity<AnimationGroupItem>().HasIndex(i => new { i.GroupId, i.SortOrder });

        }
    }
}
