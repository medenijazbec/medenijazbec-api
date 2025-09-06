using Microsoft.AspNetCore.Mvc;

namespace honey_badger_api.Controllers
{
    using honey_badger_api.Data;
    using honey_badger_api.Entities;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.EntityFrameworkCore;

    [ApiController]
    [Route("api/blog")]
    public class BlogController : ControllerBase
    {
        private readonly AppDbContext _db;
        public BlogController(AppDbContext db) => _db = db;

        [HttpGet]
        public async Task<IActionResult> ListPublished()
        {
            var posts = await _db.BlogPosts
                .Where(p => p.Status == "published")
                .OrderByDescending(p => p.PublishedAt)
                .Select(p => new { p.Id, p.Slug, p.Title, p.Excerpt, p.CoverImageUrl, p.PublishedAt })
                .ToListAsync();
            return Ok(posts);
        }

        [HttpGet("{slug}")]
        public async Task<IActionResult> GetBySlug(string slug)
        {
            var post = await _db.BlogPosts.Include(p => p.Tags).ThenInclude(t => t.BlogTag)
                .FirstOrDefaultAsync(p => p.Slug == slug);
            return post is null ? NotFound() : Ok(post);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<IActionResult> Create(BlogPost req)
        {
            if (await _db.BlogPosts.AnyAsync(p => p.Slug == req.Slug)) return Conflict("Slug exists");
            _db.BlogPosts.Add(req);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetBySlug), new { slug = req.Slug }, req);
        }

        [Authorize(Roles = "Admin")]
        [HttpPut("{id:long}")]
        public async Task<IActionResult> Update(long id, BlogPost req)
        {
            var post = await _db.BlogPosts.Include(p => p.Tags).FirstOrDefaultAsync(p => p.Id == id);
            if (post is null) return NotFound();

            post.Slug = req.Slug;
            post.Title = req.Title;
            post.Excerpt = req.Excerpt;
            post.Content = req.Content;
            post.CoverImageUrl = req.CoverImageUrl;
            post.Status = req.Status;
            post.PublishedAt = req.PublishedAt;
            post.UpdatedAt = DateTime.UtcNow;

            // Tags: (replace-all simple approach)
            post.Tags.Clear();
            foreach (var t in req.Tags)
                post.Tags.Add(new BlogPostTag { BlogTagId = t.BlogTagId == 0 ? t.BlogTag!.Id : t.BlogTagId });

            await _db.SaveChangesAsync();
            return NoContent();
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete("{id:long}")]
        public async Task<IActionResult> Delete(long id)
        {
            var post = await _db.BlogPosts.FindAsync(id);
            if (post is null) return NotFound();
            _db.Remove(post);
            await _db.SaveChangesAsync();
            return NoContent();
        }
    }

}
