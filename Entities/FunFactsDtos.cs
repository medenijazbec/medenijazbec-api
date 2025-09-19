namespace honey_badger_api.Entities
{
    public sealed record FunFactsRequest(
        string UserId,
        DateOnly? From = null,
        DateOnly? To = null,
        bool IncludeSynthetic = false,
        int StreakThreshold1 = 8000,
        int StreakThreshold2 = 10000
    );

    public sealed record TopDayDto(
        DateOnly Day,
        int Steps,
        decimal? DistanceKm,
        int? CaloriesOut,
        bool? IsSynthetic
    );

    public sealed record WeekdayAvgDto(
        int Weekday, // 1=Mon .. 7=Sun
        int AvgSteps,
        decimal AvgKm
    );

    public sealed record MonthSumDto(
        int Year,
        int Month,
        long StepsSum,
        decimal KmSum,
        int Days
    );

    public sealed record StreakDto(
        int Threshold,
        int Length,
        DateOnly? Start,
        DateOnly? End
    );

    public sealed record FunFactsResponse(
        int TotalDays,
        int DaysWithData,
        long TotalSteps,
        decimal TotalKm,
        long? TotalCaloriesOut,
        int AvgSteps,
        decimal AvgKm,
        int DaysStepsGte10k,
        int DaysStepsGte15k,
        int DaysKmGte5,
        int DaysKmGte10,
        StreakDto BestStreakGte8k,
        StreakDto BestStreakGte10k,
        WeekdayAvgDto[] WeekdayAverages,
        MonthSumDto BestMonthBySteps,
        MonthSumDto BestMonthByKm,
        TopDayDto[] Top10BySteps,
        TopDayDto[] Top10ByKm,
        TopDayDto[] Top10ByCaloriesOut
    );
}
