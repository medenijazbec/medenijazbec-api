using honey_badger_api.Data;
using System.ComponentModel.DataAnnotations.Schema;

namespace honey_badger_api.Entities
{
    public class Project
    {
        public long Id { get; set; }
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Summary { get; set; }
        public string? Description { get; set; }

        // Store JSON (array of { name, iconUrl? }) in DB column "TechStack"
        [Column("TechStack", TypeName = "json")]
        public string? TechStackJson { get; set; }

        public string? LiveUrl { get; set; }
        public string? RepoUrl { get; set; }
        public bool Featured { get; set; }
        public bool Published { get; set; }
        public string? OwnerUserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string Kind { get; set; } = "software";

        public List<ProjectImage> Images { get; set; } = new();
    }
}
