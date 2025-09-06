using honey_badger_api.Data;

namespace honey_badger_api.Entities
{
    public class BlogPost
    {
        public long Id { get; set; }
        public string Slug { get; set; } = default!;
        public string Title { get; set; } = default!;
        public string? Excerpt { get; set; }
        public string Content { get; set; } = default!;
        public string? CoverImageUrl { get; set; }
        public string Status { get; set; } = "draft"; // draft|published|archived
        public DateTime? PublishedAt { get; set; }
        public string? AuthorUserId { get; set; }
        public AppUser? AuthorUser { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<BlogPostTag> Tags { get; set; } = new List<BlogPostTag>();
    }
}
