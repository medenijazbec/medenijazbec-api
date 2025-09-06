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


        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);

            // Projects
            b.Entity<Project>()
                .HasIndex(p => p.Slug).IsUnique();
            b.Entity<ProjectImage>()
                .HasOne<Project>()
                .WithMany(p => p.Images)
                .HasForeignKey(pi => pi.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

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
            b.Entity<AnimationGroup>()
                .HasIndex(g => g.Slug).IsUnique();

            b.Entity<AnimationGroupItem>()
                .HasOne(i => i.Group)
                .WithMany(g => g.Items)
                .HasForeignKey(i => i.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            b.Entity<AnimationGroupItem>()
                .HasIndex(i => new { i.GroupId, i.SortOrder });
        }
    }
}
