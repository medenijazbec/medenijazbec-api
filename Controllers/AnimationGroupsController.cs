using honey_badger_api.Data;
using honey_badger_api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace honey_badger_api.Controllers
{
    [ApiController]
    [Route("api/animgroups")]
    public class AnimationGroupsController : ControllerBase
    {
        private readonly AppDbContext _db;

        public AnimationGroupsController(AppDbContext db) => _db = db;

        // Public list (optionally only published)
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] bool publishedOnly = false)
        {
            var q = _db.AnimationGroups.AsQueryable();
            if (publishedOnly) q = q.Where(g => g.Published);

            var data = await q
                .OrderByDescending(g => g.UpdatedAt)
                .Select(g => new
                {
                    g.Id,
                    g.Slug,
                    g.Title,
                    g.Published,
                    Items = g.Items.Count,
                    g.UpdatedAt
                })
                .ToListAsync();

            return Ok(data);
        }

        // Public get by id or slug
        [HttpGet("{idOrSlug}")]
        public async Task<IActionResult> Get(string idOrSlug)
        {
            bool isId = long.TryParse(idOrSlug, out var id);
            var q = _db.AnimationGroups.Include(g => g.Items.OrderBy(i => i.SortOrder)).AsQueryable();

            var entity = isId
                ? await q.FirstOrDefaultAsync(g => g.Id == id)
                : await q.FirstOrDefaultAsync(g => g.Slug == idOrSlug);

            return entity is null ? NotFound() : Ok(entity);
        }

        // Create
        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] AnimationGroupDto req)
        {
            var slug = string.IsNullOrWhiteSpace(req.Slug) ? Slugify(req.Title) : req.Slug.Trim();
            if (await _db.AnimationGroups.AnyAsync(g => g.Slug == slug))
                return Conflict("Slug already exists.");

            var group = new AnimationGroup
            {
                Slug = slug,
                Title = req.Title,
                Description = req.Description,
                TagsJson = req.TagsJson,
                Published = req.Published,
                AuthorUserId = User?.Identity?.IsAuthenticated == true ? User.FindFirst("sub")?.Value : null,
            };

            int order = 0;
            foreach (var item in req.Items ?? Enumerable.Empty<AnimationGroupItemDto>())
            {
                group.Items.Add(new AnimationGroupItem
                {
                    FileName = item.FileName,
                    Label = item.Label,
                    SortOrder = order++
                });
            }

            _db.AnimationGroups.Add(group);
            await _db.SaveChangesAsync();

            return CreatedAtAction(nameof(Get), new { idOrSlug = group.Slug }, group);
        }

        // Update
        [Authorize(Roles = "Admin")]
        [HttpPut("{id:long}")]
        public async Task<IActionResult> Update(long id, [FromBody] AnimationGroupDto req)
        {
            var entity = await _db.AnimationGroups.Include(g => g.Items).FirstOrDefaultAsync(g => g.Id == id);
            if (entity is null) return NotFound();

            var newSlug = string.IsNullOrWhiteSpace(req.Slug) ? Slugify(req.Title) : req.Slug.Trim();
            if (newSlug != entity.Slug && await _db.AnimationGroups.AnyAsync(g => g.Slug == newSlug))
                return Conflict("Slug already exists.");

            entity.Slug = newSlug;
            entity.Title = req.Title;
            entity.Description = req.Description;
            entity.TagsJson = req.TagsJson;
            entity.Published = req.Published;
            entity.UpdatedAt = DateTime.UtcNow;

            // replace items (simple approach)
            entity.Items.Clear();
            int order = 0;
            foreach (var item in req.Items ?? Enumerable.Empty<AnimationGroupItemDto>())
            {
                entity.Items.Add(new AnimationGroupItem
                {
                    FileName = item.FileName,
                    Label = item.Label,
                    SortOrder = order++
                });
            }

            await _db.SaveChangesAsync();
            return NoContent();
        }

        // Delete
        [Authorize(Roles = "Admin")]
        [HttpDelete("{id:long}")]
        public async Task<IActionResult> Delete(long id)
        {
            var entity = await _db.AnimationGroups.FindAsync(id);
            if (entity is null) return NotFound();
            _db.Remove(entity);
            await _db.SaveChangesAsync();
            return NoContent();
        }

        private static string Slugify(string input)
        {
            var s = (input ?? "").Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
            s = Regex.Replace(s, @"\s+", "-");
            s = Regex.Replace(s, "-{2,}", "-");
            return s.Trim('-');
        }
    }

    public class AnimationGroupDto
    {
        public string Title { get; set; } = default!;
        public string? Slug { get; set; }
        public string? Description { get; set; }
        public string? TagsJson { get; set; }
        public bool Published { get; set; }
        public List<AnimationGroupItemDto>? Items { get; set; }
    }

    public class AnimationGroupItemDto
    {
        public string FileName { get; set; } = default!;
        public string? Label { get; set; }
    }
}
