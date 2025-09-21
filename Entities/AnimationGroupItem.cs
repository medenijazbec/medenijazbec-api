using System.Text.Json.Serialization;

namespace honey_badger_api.Entities
{
    public class AnimationGroupItem
    {
        public long Id { get; set; }

        public long GroupId { get; set; }

        // Break the serializer cycle: Group -> Items -> Group -> ...
        [JsonIgnore]
        public AnimationGroup? Group { get; set; }

        public string FileName { get; set; } = default!;
        public string? Label { get; set; }
        public int SortOrder { get; set; }
    }
}
