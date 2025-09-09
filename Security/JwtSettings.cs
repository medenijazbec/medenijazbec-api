// path: honey_badger_api/Security/JwtSettings.cs
using honey_badger_api.Data;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace honey_badger_api.Security
{
    // DTOs used by AuthController
    public record RegisterRequest(string Email, string Password, string? DisplayName);
    public record LoginRequest(string Email, string Password);

    public record JwtResult(string Token, DateTime ExpiresAt);

    public static class JwtTokenGeneration
    {
        public static JwtResult Create(AppUser user, IList<string> roles, IConfiguration cfg)
        {
            var section = cfg.GetSection("Jwt");
            var key = section["Key"] ?? throw new InvalidOperationException("Jwt:Key missing");
            var issuer = section["Issuer"];
            var audience = section["Audience"];

            var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
            var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id),
                new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName ?? user.Email ?? user.Id)
            };

            if (roles is not null)
                claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

            var expires = DateTime.UtcNow.AddDays(7);

            var token = new JwtSecurityToken(
                issuer: string.IsNullOrWhiteSpace(issuer) ? null : issuer,
                audience: string.IsNullOrWhiteSpace(audience) ? null : audience,
                claims: claims,
                expires: expires,
                signingCredentials: creds
            );

            var jwt = new JwtSecurityTokenHandler().WriteToken(token);
            return new JwtResult(jwt, expires);
        }
    }
}
