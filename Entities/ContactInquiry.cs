namespace honey_badger_api.Entities
{
    public class ContactInquiry
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string Email { get; set; } = default!;
        public string? Phone { get; set; }
        public string? Subject { get; set; }
        public string Message { get; set; } = default!;
        public string Status { get; set; } = "new"; // new|replied|closed
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? HandledByUserId { get; set; }
    }
}
