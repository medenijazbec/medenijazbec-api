namespace honey_badger_api.Entities
{
    public class BlogPostTag
    {
        public long BlogPostId { get; set; }
        public BlogPost? BlogPost { get; set; }
        public long BlogTagId { get; set; }
        public BlogTag? BlogTag { get; set; }
    }
}
