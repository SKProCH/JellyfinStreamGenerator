using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.StreamGenerator.Model;

public class PatchRequestPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}