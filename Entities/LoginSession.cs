namespace honey_badger_api.Entities
{
    public class LoginSession
    {
        public long Id { get; set; }
        public string UserId { get; set; } = "";
        public string Email { get; set; } = "";
        public string Ip { get; set; } = "";
        public string? UserAgent { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        public string? JwtId { get; set; }      // jti claim if you add it later
        public bool Revoked { get; set; }
        public DateTime? RevokedUtc { get; set; }
    }
}
