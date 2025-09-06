using honey_badger_api.Data;
using honey_badger_api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Controllers
{
    [ApiController]
    [Route("api/projects")]
    public class ProjectsController : ControllerBase
    {
        private readonly AppDbContext _db;
        public ProjectsController(AppDbContext db) => _db = db;

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Project>>> GetPublic([FromQuery] bool includeUnpublished = false)
        {
            var q = _db.Projects.Include(p => p.Images).AsQueryable();
            if (!includeUnpublished) q = q.Where(p => p.Published);
            var items = await q.OrderByDescending(p => p.Featured).ThenByDescending(p => p.UpdatedAt).ToListAsync();
            return Ok(items);
        }

        [HttpGet("{slug}")]
        public async Task<ActionResult<Project>> GetBySlug(string slug)
        {
            var proj = await _db.Projects.Include(p => p.Images).FirstOrDefaultAsync(p => p.Slug == slug);
            return proj is null ? NotFound() : Ok(proj);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<ActionResult<Project>> Create(Project dto)
        {
            if (await _db.Projects.AnyAsync(p => p.Slug == dto.Slug))
                return Conflict("Slug already exists");
            _db.Projects.Add(dto);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetBySlug), new { slug = dto.Slug }, dto);
        }

        [Authorize(Roles = "Admin")]
        [HttpPut("{id:long}")]
        public async Task<IActionResult> Update(long id, Project dto)
        {
            var entity = await _db.Projects.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == id);
            if (entity is null) return NotFound();

            entity.Slug = dto.Slug;
            entity.Title = dto.Title;
            entity.Summary = dto.Summary;
            entity.Description = dto.Description;
            entity.TechStackJson = dto.TechStackJson;
            entity.LiveUrl = dto.LiveUrl;
            entity.RepoUrl = dto.RepoUrl;
            entity.Featured = dto.Featured;
            entity.Published = dto.Published;
            entity.UpdatedAt = DateTime.UtcNow;

            // replace images (simple approach)
            entity.Images.Clear();
            foreach (var img in dto.Images) entity.Images.Add(new ProjectImage { Url = img.Url, Alt = img.Alt, SortOrder = img.SortOrder });

            await _db.SaveChangesAsync();
            return NoContent();
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete("{id:long}")]
        public async Task<IActionResult> Delete(long id)
        {
            var entity = await _db.Projects.FindAsync(id);
            if (entity is null) return NotFound();
            _db.Remove(entity);
            await _db.SaveChangesAsync();
            return NoContent();
        }
    }
}
