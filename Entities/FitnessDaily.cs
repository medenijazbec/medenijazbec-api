namespace honey_badger_api.Entities
{
    public class FitnessDaily
    {
        public long Id { get; set; }
        public string UserId { get; set; } = default!;
        public DateOnly Day { get; set; }
        public int? CaloriesIn { get; set; }
        public int? CaloriesOut { get; set; }
        public int? Steps { get; set; }
        public int? SleepMinutes { get; set; }
        public decimal? WeightKg { get; set; }
        public string? Notes { get; set; }
        public decimal? DistanceKm { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public bool? IsSynthetic { get; set; }
    }
}
