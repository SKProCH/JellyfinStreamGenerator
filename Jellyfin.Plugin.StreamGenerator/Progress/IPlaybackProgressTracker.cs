using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.StreamGenerator.Progress;

public readonly record struct SegmentProgressObservation(Guid UserId, Guid ItemId, long PositionTicks);

public interface IPlaybackProgressTracker
{
    Task<SegmentProgressObservation?> CreateObservationAsync(ActionExecutingContext context);

    void Track(SegmentProgressObservation observation);
}
