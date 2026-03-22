namespace Jellyfin.Plugin.StreamGenerator.Configuration;

public sealed class StreamTokenInformation
{
    public Guid UserId { get; set; }
    public string ItemId { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsExpired() => DateTime.UtcNow > CreatedAt + Duration;
}
