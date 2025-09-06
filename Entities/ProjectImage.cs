namespace honey_badger_api.Entities
{
    public class ProjectImage
    {
        public long Id { get; set; }
        public long ProjectId { get; set; }
        public string Url { get; set; } = default!;
        public string? Alt { get; set; }
        public int SortOrder { get; set; }
    }
}
