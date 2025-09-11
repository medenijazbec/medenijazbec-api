namespace honey_badger_api.Entities
{
    public class MetricSnapshot
    {
        public long Id { get; set; }
        public DateTime WindowStartUtc { get; set; }
        public DateTime WindowEndUtc { get; set; }

        public int Requests { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
        public int UniqueIps { get; set; }
        public int Errors4xx { get; set; }
        public int Errors5xx { get; set; }

        public string? StatusCountsJson { get; set; }
        public string? CountrySplitJson { get; set; }
        public string? UaBotHistogramJson { get; set; }
        public string? Notes { get; set; }
    }
}
