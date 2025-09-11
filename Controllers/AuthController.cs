// path: honey_badger_api/Controllers/AuthController.cs
using honey_badger_api.Data;
using honey_badger_api.Entities;
using honey_badger_api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace honey_badger_api.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<AppUser> _userManager;
        private readonly SignInManager<AppUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IConfiguration _cfg;
        private readonly AppDbContext _db;

        public AuthController(
            UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager,
            RoleManager<IdentityRole> roleManager,
            IConfiguration cfg,
            AppDbContext db) // <-- inject
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _cfg = cfg;
            _db = db; // <-- store
        }

        // POST /api/auth/register  { email, password, displayName? }
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return BadRequest("Email and password are required.");

            var user = new AppUser
            {
                UserName = req.Email,
                Email = req.Email
            };

            var result = await _userManager.CreateAsync(user, req.Password);
            if (!result.Succeeded) return BadRequest(result.Errors);

            // No auto-admin.
            var roles = await _userManager.GetRolesAsync(user);
            var jwt = JwtTokenGeneration.Create(user, roles, _cfg);

            return Ok(new
            {
                token = jwt.Token,
                roles = roles.ToArray(),
                expiresAt = jwt.ExpiresAt,
                user = new { id = user.Id, email = user.Email }
            });
        }

        // POST /api/auth/login  { email, password }
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            var user = await _userManager.FindByEmailAsync(req.Email);
            if (user is null) return Unauthorized("Invalid email or password.");

            var check = await _signInManager.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
            if (!check.Succeeded) return Unauthorized("Invalid email or password.");

            var roles = await _userManager.GetRolesAsync(user);

            // --- Record login session (NEW) ---
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString()
                     ?? Request.Headers["X-Forwarded-For"].ToString().Split(',').FirstOrDefault()
                     ?? "0.0.0.0";
            var ua = Request.Headers.UserAgent.ToString();

            _db.LoginSessions.Add(new LoginSession
            {
                UserId = user.Id,
                Email = user.Email ?? "",
                Ip = ip,
                UserAgent = ua,
                CreatedUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            // ----------------------------------

            var jwt = JwtTokenGeneration.Create(user, roles, _cfg);

            return Ok(new
            {
                token = jwt.Token,
                roles = roles.ToArray(),
                expiresAt = jwt.ExpiresAt,
                user = new { id = user.Id, email = user.Email }
            });
        }


        // GET /api/auth/me
        [Authorize]
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            var uid = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(uid)) return Unauthorized();

            var user = await _userManager.FindByIdAsync(uid);
            if (user is null) return Unauthorized();

            var roles = await _userManager.GetRolesAsync(user);
            return Ok(new
            {
                user = new { id = user.Id, email = user.Email, userName = user.UserName },
                roles = roles.ToArray()
            });
        }

        // POST /api/auth/users/{userId}/admin  { isAdmin: true|false }  (Admin only)
        [Authorize(Roles = "Admin")]
        [HttpPost("users/{userId}/admin")]
        public async Task<IActionResult> ToggleAdmin(string userId, [FromBody] ToggleAdminRequest body)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user is null) return NotFound("User not found.");

            if (!await _roleManager.RoleExistsAsync("Admin"))
                await _roleManager.CreateAsync(new IdentityRole("Admin"));

            var isAdminNow = await _userManager.IsInRoleAsync(user, "Admin");
            if (body.IsAdmin && !isAdminNow)
                await _userManager.AddToRoleAsync(user, "Admin");
            else if (!body.IsAdmin && isAdminNow)
                await _userManager.RemoveFromRoleAsync(user, "Admin");

            var roles = await _userManager.GetRolesAsync(user);
            return Ok(new { user = new { id = user.Id, email = user.Email }, roles });
        }
    }

    public record ToggleAdminRequest(bool IsAdmin);
}
