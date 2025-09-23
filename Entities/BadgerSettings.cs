// path: honey_badger_api/Entities/BadgerSettings.cs
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace honey_badger_api.Entities
{
    [Table("BadgerSettings")]
    public class BadgerSettings
    {
        [Key]
        public int Id { get; set; } = 1; // single-row config

        // Positions
        public int OffsetY { get; set; } = 0;
        public int SaucerOffsetY { get; set; } = 0;

        // Lighting
        public int LightYaw { get; set; } = 0;
        public int LightHeight { get; set; } = 120;
        public int LightDist { get; set; } = 200;

        // Zoom / scale controls
        public double ModelZoom { get; set; } = 1.0;
        public double SaucerZoom { get; set; } = 1.0;
        public double CameraZoom { get; set; } = 1.0;

        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
