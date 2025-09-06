namespace honey_badger_api.Entities
{
    public class ExerciseSession
    {
        public long Id { get; set; }
        public string UserId { get; set; } = default!;
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string Type { get; set; } = "other"; // cardio|strength|mobility|other
        public int? CaloriesBurned { get; set; }
        public decimal? DistanceKm { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
