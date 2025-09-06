namespace honey_badger_api.Entities
{
    public class AnimationGroupItem
    {
        public long Id { get; set; }

        public long GroupId { get; set; }
        public AnimationGroup? Group { get; set; }

        public string FileName { get; set; } = default!; // relative to ANIM_DIR
        public string? Label { get; set; }               // "Start" | "Middle" | "End" | other
        public int SortOrder { get; set; }
    }
}
