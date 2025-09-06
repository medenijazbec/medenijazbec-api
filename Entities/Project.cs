using honey_badger_api.Data;

namespace honey_badger_api.Entities
{
    public class Project
    {
        public long Id { get; set; }
        public string Slug { get; set; } = default!;
        public string Title { get; set; } = default!;
        public string? Summary { get; set; }
        public string? Description { get; set; }
        public string? TechStackJson { get; set; } // Map JSON from DB
        public string? LiveUrl { get; set; }
        public string? RepoUrl { get; set; }
        public bool Featured { get; set; }
        public bool Published { get; set; } = true;
        public string? OwnerUserId { get; set; }
        public AppUser? OwnerUser { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<ProjectImage> Images { get; set; } = new List<ProjectImage>();
    }
}
