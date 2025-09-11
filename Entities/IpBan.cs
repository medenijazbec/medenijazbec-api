namespace honey_badger_api.Entities
{
    public class IpBan
    {
        public long Id { get; set; }
        public string Value { get; set; } = "";   // IP or CIDR
        public string Kind { get; set; } = "ip";  // ip|cidr|user|asn|country
        public string? Reason { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresUtc { get; set; } // null = no expiry
        public bool Disabled { get; set; }
        public string? CreatedByUserId { get; set; }
    }
}
