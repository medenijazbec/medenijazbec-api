namespace honey_badger_api.Entities
{
    public class DailyTopIp
    {
        public long Id { get; set; }
        public DateOnly Day { get; set; }
        public string Ip { get; set; } = "";
        public int Count { get; set; }
        public int Rank { get; set; }
        public string? Country { get; set; }
        public string? Asn { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
}
