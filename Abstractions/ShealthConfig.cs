using System.IO;

namespace honey_badger_api.Abstractions
{
    public sealed class ShealthConfig
    {
        // Root Samsung-Data directory (e.g., C:\...\honey_badger_api\Samsung-Data)
        public string RootDir { get; init; } = default!;

        public string ZipDir => Path.Combine(RootDir, "ZIP_FILES");
        public string RawDir => Path.Combine(RootDir, "RAW_DATA");
    }
}
