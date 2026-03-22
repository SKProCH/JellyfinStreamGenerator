namespace Jellyfin.Plugin.StreamGenerator.Configuration;

public sealed class StreamTokenInformation
{
    public required Guid UserId { get; set; }
    public required string ItemId { get; set; }
    public TimeSpan? Duration { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }

    public bool IsExpired() => Duration.HasValue && DateTimeOffset.UtcNow > CreatedAt + Duration.Value;
}
