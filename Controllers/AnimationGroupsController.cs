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

        // -------- Response VMs (no cycles) --------
        public record AnimationGroupItemVm(long Id, string FileName, string? Label, int SortOrder);

        public record AnimationGroupVm(
            long Id,
            string Slug,
            string Title,
            string? Description,
            string? TagsJson,
            bool Published,
            string Category,
            bool IsDefaultForCategory,
            DateTime UpdatedAt,
            List<AnimationGroupItemVm> Items
        );

        private static AnimationGroupVm ToVm(AnimationGroup g) =>
            new(
                g.Id,
                g.Slug,
                g.Title,
                g.Description,
                g.TagsJson,
                g.Published,
                g.Category,
                g.IsDefaultForCategory,
                g.UpdatedAt,
                g.Items
                    .OrderBy(i => i.SortOrder)
                    .Select(i => new AnimationGroupItemVm(i.Id, i.FileName, i.Label, i.SortOrder))
                    .ToList()
            );

        // -------- Request DTOs --------
        public class AnimationGroupDto
        {
            public string Title { get; set; } = default!;
            public string? Slug { get; set; }
            public string? Description { get; set; }
            public string? TagsJson { get; set; }
            public bool Published { get; set; }

            public string? Category { get; set; }
            public bool IsDefaultForCategory { get; set; } = false;

            public List<AnimationGroupItemDto>? Items { get; set; }
        }

        public class AnimationGroupItemDto
        {
            public string FileName { get; set; } = default!;
            public string? Label { get; set; }
        }

        // -------- Endpoints --------

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] bool publishedOnly = false)
        {
            var q = _db.AnimationGroups.AsNoTracking();
            if (publishedOnly) q = q.Where(g => g.Published);

            var data = await q
                .OrderByDescending(g => g.UpdatedAt)
                .Select(g => new
                {
                    g.Id,
                    g.Slug,
                    g.Title,
                    g.Published,
                    g.Category,
                    g.IsDefaultForCategory,
                    Items = g.Items.Count,
                    g.UpdatedAt
                })
                .ToListAsync();

            return Ok(data);
        }

        [HttpGet("{idOrSlug}")]
        public async Task<IActionResult> Get(string idOrSlug)
        {
            var isId = long.TryParse(idOrSlug, out var id);

            var entity = await _db.AnimationGroups
                .AsNoTracking()
                .Include(g => g.Items)
                .FirstOrDefaultAsync(g => isId ? g.Id == id : g.Slug == idOrSlug);

            return entity is null ? NotFound() : Ok(ToVm(entity));
        }

        [HttpGet("by-category/{category}")]
        public async Task<IActionResult> ByCategory(string category, [FromQuery] bool publishedOnly = true)
        {
            var q = _db.AnimationGroups
                .AsNoTracking()
                .Include(g => g.Items)
                .Where(g => g.Category == category);

            if (publishedOnly) q = q.Where(g => g.Published);

            var list = await q.OrderByDescending(g => g.UpdatedAt).ToListAsync();
            return Ok(list.Select(ToVm).ToList());
        }

        [HttpGet("default")]
        public async Task<IActionResult> Default([FromQuery] string category)
        {
            var q = _db.AnimationGroups
                .AsNoTracking()
                .Include(g => g.Items)
                .Where(g => g.Category == category && g.Published);

            var def = await q.FirstOrDefaultAsync(g => g.IsDefaultForCategory);
            if (def != null) return Ok(ToVm(def));

            var any = await q.ToListAsync();
            if (any.Count == 0)
                return NotFound($"No published animation groups found for category '{category}'.");

            var rnd = new Random().Next(any.Count);
            return Ok(ToVm(any[rnd]));
        }

        [HttpGet("random")]
        public async Task<IActionResult> Random([FromQuery] string category)
        {
            var list = await _db.AnimationGroups
                .AsNoTracking()
                .Include(g => g.Items)
                .Where(g => g.Category == category && g.Published)
                .ToListAsync();

            if (list.Count == 0)
                return NotFound($"No published animation groups found for category '{category}'.");

            var rnd = new Random().Next(list.Count);
            return Ok(ToVm(list[rnd]));
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] AnimationGroupDto req)
        {
            var slug = string.IsNullOrWhiteSpace(req.Slug) ? Slugify(req.Title) : req.Slug!.Trim();
            if (await _db.AnimationGroups.AnyAsync(g => g.Slug == slug))
                return Conflict("Slug already exists.");

            var group = new AnimationGroup
            {
                Slug = slug,
                Title = req.Title,
                Description = req.Description,
                TagsJson = req.TagsJson,
                Published = req.Published,
                Category = string.IsNullOrWhiteSpace(req.Category) ? "misc" : req.Category!.Trim(),
                IsDefaultForCategory = req.IsDefaultForCategory,
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

            if (group.IsDefaultForCategory)
                await ClearOtherDefaults(group.Category);

            _db.AnimationGroups.Add(group);
            await _db.SaveChangesAsync();

            var saved = await _db.AnimationGroups
                .AsNoTracking()
                .Include(g => g.Items)
                .FirstAsync(g => g.Id == group.Id);

            return CreatedAtAction(nameof(Get), new { idOrSlug = saved.Slug }, ToVm(saved));
        }

        [Authorize(Roles = "Admin")]
        [HttpPut("{id:long}")]
        public async Task<IActionResult> Update(long id, [FromBody] AnimationGroupDto req)
        {
            var entity = await _db.AnimationGroups.Include(g => g.Items).FirstOrDefaultAsync(g => g.Id == id);
            if (entity is null) return NotFound();

            var newSlug = string.IsNullOrWhiteSpace(req.Slug) ? Slugify(req.Title) : req.Slug!.Trim();
            if (newSlug != entity.Slug && await _db.AnimationGroups.AnyAsync(g => g.Slug == newSlug))
                return Conflict("Slug already exists.");

            var newCategory = string.IsNullOrWhiteSpace(req.Category) ? "misc" : req.Category!.Trim();

            entity.Slug = newSlug;
            entity.Title = req.Title;
            entity.Description = req.Description;
            entity.TagsJson = req.TagsJson;
            entity.Published = req.Published;
            entity.Category = newCategory;
            entity.IsDefaultForCategory = req.IsDefaultForCategory;
            entity.UpdatedAt = DateTime.UtcNow;

            // Replace items, preserving incoming order -> SortOrder
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

            if (entity.IsDefaultForCategory)
                await ClearOtherDefaults(entity.Category, exceptId: entity.Id);

            await _db.SaveChangesAsync();
            return NoContent();
        }

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

        private async Task ClearOtherDefaults(string category, long? exceptId = null)
        {
            var others = await _db.AnimationGroups
                .Where(g => g.Category == category && g.IsDefaultForCategory && (!exceptId.HasValue || g.Id != exceptId.Value))
                .ToListAsync();

            foreach (var g in others) g.IsDefaultForCategory = false;
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
}
