using honey_badger_api.Data;
using honey_badger_api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

        public AuthController(UserManager<AppUser> userManager, SignInManager<AppUser> signInManager,
            RoleManager<IdentityRole> roleManager, IConfiguration cfg)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _cfg = cfg;
        }

        [HttpPost("register")]
        public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req)
        {
            var user = new AppUser { UserName = req.Email, Email = req.Email };
            var result = await _userManager.CreateAsync(user, req.Password);
            if (!result.Succeeded) return BadRequest(result.Errors);

            // Ensure Admin role exists
            if (!await _roleManager.RoleExistsAsync("Admin"))
                await _roleManager.CreateAsync(new IdentityRole("Admin"));

            // Make the first user an admin (optional: or guard with config)
            if ((await _userManager.Users.CountAsync()) == 1)
                await _userManager.AddToRoleAsync(user, "Admin");

            var roles = await _userManager.GetRolesAsync(user);
            var token = JwtTokenGeneration.Create(user, roles, _cfg);
            return Ok(token);
        }

        [HttpPost("login")]
        public async Task<ActionResult<AuthResponse>> Login(LoginRequest req)
        {
            var user = await _userManager.FindByEmailAsync(req.Email);
            if (user is null) return Unauthorized();

            var check = await _signInManager.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
            if (!check.Succeeded) return Unauthorized();

            var roles = await _userManager.GetRolesAsync(user);
            var token = JwtTokenGeneration.Create(user, roles, _cfg);
            return Ok(token);
        }
    }
}
