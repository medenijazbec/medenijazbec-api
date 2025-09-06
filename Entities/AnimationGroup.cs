using honey_badger_api.Data;

namespace honey_badger_api.Entities
{
    public class AnimationGroup
    {
        public long Id { get; set; }
        public string Slug { get; set; } = default!;
        public string Title { get; set; } = default!;
        public string? Description { get; set; }
        public string? TagsJson { get; set; }
        public bool Published { get; set; } = false;

        public string? AuthorUserId { get; set; }
        public AppUser? AuthorUser { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<AnimationGroupItem> Items { get; set; } = new List<AnimationGroupItem>();
    }
}
