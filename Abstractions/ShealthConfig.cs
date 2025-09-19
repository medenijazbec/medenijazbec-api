using System.IO;

namespace honey_badger_api.Abstractions
{
    public sealed class ShealthConfig
    {
        // Root Samsung-Data directory (e.g., C:\...\honey_badger_api\Samsung-Data)
        public string RootDir { get; init; } = default!;

        // IANA timezone used in the Python pipeline (default: Europe/Ljubljana)
        public string LocalTimeZone { get; init; } = "Europe/Ljubljana";

        // Global day offset applied by the Python pipeline to NON-reference dates (default: 0)
        // Set to 1 if you need to push everything forward a day (fixes -1 day skew).
        public int DateShiftDays { get; init; } = 0;

        public string ZipDir => Path.Combine(RootDir, "ZIP_FILES");
        public string RawDir => Path.Combine(RootDir, "RAW_DATA");
    }
}
