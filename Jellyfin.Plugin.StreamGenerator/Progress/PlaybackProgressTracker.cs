using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.StreamGenerator.Authorization;
using Jellyfin.Plugin.StreamGenerator.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamGenerator.Progress;

internal sealed class PlaybackProgressTracker(
    IStreamGeneratorConfigurationAccessor configurationAccessor,
    IAuthorizationContext authorizationContext,
    IUserManager userManager,
    ILibraryManager libraryManager,
    IMediaSourceManager mediaSourceManager,
    IUserDataManager userDataManager,
    ILogger<PlaybackProgressTracker> logger)
    : IPlaybackProgressTracker
{
    private const int LockStripeCount = 256;
    private readonly object[] _lockStripes = Enumerable.Range(0, LockStripeCount).Select(_ => new object()).ToArray();

    public async Task<SegmentProgressObservation?> CreateObservationAsync(ActionExecutingContext context)
    {
        var request = context.HttpContext.Request;
        var configuration = configurationAccessor.Configuration;
        if (configuration is null
            || !configuration.GenerateCustomApiTokens
            || !StreamTokenPathScope.TryParse(request, out var scope)
            || scope is null
            || scope.Kind != StreamTokenResourceKind.Segment
            || !TryGetToken(request, out var apiKey)
            || !configuration.StreamTokens.TryGetValue(apiKey, out var token)
            || token.IsExpired()
            || !token.RememberPlaybackProgress
            || !Guid.TryParse(token.ItemId, out var tokenItemId)
            || tokenItemId != scope.ItemId
            || !TryGetIntArgument(context, "segmentId", out var segmentId)
            || segmentId < 0
            || !TryGetLongArgument(context, "runtimeTicks", out var runtimeTicks)
            || runtimeTicks < 0
            || !TryGetLongArgument(context, "actualSegmentLengthTicks", out var segmentLengthTicks)
            || segmentLengthTicks <= 0)
        {
            return null;
        }

        var authorizationInfo = await authorizationContext.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
        if (!authorizationInfo.IsAuthenticated
            || authorizationInfo.UserId != token.UserId
            || !string.Equals(authorizationInfo.Token, apiKey, StringComparison.Ordinal))
        {
            return null;
        }

        var user = userManager.GetUserById(token.UserId);
        var item = user is null ? null : libraryManager.GetItemById<BaseItem>(tokenItemId, user);
        if (user is null
            || item is null
            || scope.MediaSourceId is null
            || !mediaSourceManager.GetStaticMediaSources(item, false, user)
                .Any(source => string.Equals(source.Id, scope.MediaSourceId, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        long positionTicks;
        try
        {
            positionTicks = checked(runtimeTicks + segmentLengthTicks);
        }
        catch (OverflowException)
        {
            return null;
        }

        return new SegmentProgressObservation(token.UserId, tokenItemId, positionTicks);
    }

    public void Track(SegmentProgressObservation observation)
    {
        lock (GetLock(observation.UserId, observation.ItemId))
        {
            try
            {
                var user = userManager.GetUserById(observation.UserId);
                if (user is null)
                {
                    return;
                }

                var item = libraryManager.GetItemById<BaseItem>(observation.ItemId, user);
                if (item is null)
                {
                    return;
                }

                var data = userDataManager.GetUserData(user, item);
                if (data is null || data.Played || observation.PositionTicks <= data.PlaybackPositionTicks)
                {
                    return;
                }

                var previousPosition = data.PlaybackPositionTicks;
                var previousPlayed = data.Played;
                try
                {
                    var playedToCompletion = userDataManager.UpdatePlayState(item, data, observation.PositionTicks);
                    if (!playedToCompletion
                        && data.PlaybackPositionTicks <= previousPosition
                        && data.Played == previousPlayed)
                    {
                        data.PlaybackPositionTicks = previousPosition;
                        data.Played = previousPlayed;
                        return;
                    }

                    userDataManager.SaveUserData(
                        user,
                        item,
                        data,
                        playedToCompletion ? UserDataSaveReason.PlaybackFinished : UserDataSaveReason.PlaybackProgress,
                        CancellationToken.None);
                }
                catch
                {
                    data.PlaybackPositionTicks = previousPosition;
                    data.Played = previousPlayed;
                    throw;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to update generated-link playback progress for user {UserId}, item {ItemId}",
                    observation.UserId,
                    observation.ItemId);
            }
        }
    }

    private object GetLock(Guid userId, Guid itemId)
    {
        var hash = unchecked((uint)HashCode.Combine(userId, itemId));
        return _lockStripes[hash % (uint)_lockStripes.Length];
    }

    private static bool TryGetToken(HttpRequest request, out string token)
    {
        token = string.Empty;
        var hasLegacyApiKey = request.Query.TryGetValue("api_key", out var legacyApiKeyValues);
        var hasApiKey = request.Query.TryGetValue("ApiKey", out var apiKeyValues);
        if (hasLegacyApiKey == hasApiKey)
        {
            return false;
        }

        var values = hasApiKey ? apiKeyValues : legacyApiKeyValues;
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return false;
        }

        token = values[0]!;
        return true;
    }

    private static bool TryGetIntArgument(ActionExecutingContext context, string name, out int value)
    {
        value = 0;
        return context.ActionArguments.TryGetValue(name, out var argument)
               && argument is not null
               && int.TryParse(argument.ToString(), out value);
    }

    private static bool TryGetLongArgument(ActionExecutingContext context, string name, out long value)
    {
        value = 0;
        return context.ActionArguments.TryGetValue(name, out var argument)
               && argument is not null
               && long.TryParse(argument.ToString(), out value);
    }
}
