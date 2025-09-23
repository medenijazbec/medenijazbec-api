namespace honey_badger_api.Entities
{
    public record BadgerSettings
    {
        public int OffsetY { get; init; } = 0;
        public int LightYaw { get; init; } = 0;
        public int LightHeight { get; init; } = 120;
        public int LightDist { get; init; } = 200;
    }
}
