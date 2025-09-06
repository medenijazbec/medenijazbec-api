namespace honey_badger_api.Entities
{
    public class BlogTag
    {
        public long Id { get; set; }
        public string Name { get; set; } = default!;
        public string Slug { get; set; } = default!;
    }
}
