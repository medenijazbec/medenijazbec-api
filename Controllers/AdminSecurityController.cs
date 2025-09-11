using honey_badger_api.Data;
using honey_badger_api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace honey_badger_api.Controllers
{
    [ApiController]
    [Route("api/admin/security")]
    [Authorize(Roles = "Admin")]
    public sealed class AdminSecurityController : ControllerBase
    {
        private readonly AppDbContext _db;
        public AdminSecurityController(AppDbContext db) => _db = db;

        [HttpGet("ip-bans")]
        public async Task<IActionResult> ListBans()
        {
            var list = await _db.IpBans
                .OrderByDescending(b => b.CreatedUtc)
                .Take(1000).ToListAsync();
            return Ok(list);
        }

        public record BanRequest(string value, string? reason, DateTime? expiresUtc);
        [HttpPost("ip-bans")]
        public async Task<IActionResult> Ban([FromBody] BanRequest req)
        {
            var ban = new IpBan { Value = req.value.Trim(), Kind = "ip", Reason = req.reason, ExpiresUtc = req.expiresUtc };
            _db.IpBans.Add(ban);
            await _db.SaveChangesAsync();
            return Ok(ban);
        }

        [HttpDelete("ip-bans/{id:long}")]
        public async Task<IActionResult> Unban(long id)
        {
            var b = await _db.IpBans.FindAsync(id);
            if (b == null) return NotFound();
            _db.IpBans.Remove(b);
            await _db.SaveChangesAsync();
            return NoContent();
        }
    }
}
