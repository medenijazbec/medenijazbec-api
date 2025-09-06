using honey_badger_api.Data;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace honey_badger_api.Security
{
    public record RegisterRequest(string Email, string Password, string? DisplayName);
    public record LoginRequest(string Email, string Password);
    public record AuthResponse(string AccessToken, DateTime ExpiresAt, bool IsAdmin);

    public static class JwtTokenGeneration
    {
        public static AuthResponse Create(AppUser user, IList<string> roles, IConfiguration cfg)
        {
            var jwt = cfg.GetSection("Jwt");
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName ?? user.Email ?? user.Id)
        };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

            var expires = DateTime.UtcNow.AddDays(7);
            var token = new JwtSecurityToken(
                issuer: jwt["Issuer"],
                audience: jwt["Audience"],
                claims: claims,
                expires: expires,
                signingCredentials: creds
            );

            var access = new JwtSecurityTokenHandler().WriteToken(token);
            return new AuthResponse(access, expires, roles.Contains("Admin"));
        }
    }

}
