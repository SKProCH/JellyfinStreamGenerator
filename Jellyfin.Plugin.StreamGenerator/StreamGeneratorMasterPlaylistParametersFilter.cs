using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.StreamGenerator;

public sealed class StreamGeneratorMasterPlaylistParametersFilter(
    IAuthorizationContext authorizationContext,
    ILibraryManager libraryManager,
    IMediaSourceManager mediaSourceManager) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        await AddOriginalVideoParametersAsync(context, context.HttpContext.Request).ConfigureAwait(false);
        await next().ConfigureAwait(false);
    }

    private async Task AddOriginalVideoParametersAsync(ActionExecutingContext context, HttpRequest request)
    {
        if (!context.RouteData.Values.TryGetValue("controller", out var controller)
            || controller is not "DynamicHls"
            || !context.RouteData.Values.TryGetValue("action", out var action)
            || action?.ToString() != "GetMasterHlsVideoPlaylist"
            || request.Query.ContainsKey("videoBitrate")
            || request.Query["deviceId"] != "stream_generator"
            || request.Query["playSessionId"] != "stream_generator_random")
        {
            return;
        }

        var authorizationInfo = await authorizationContext.GetAuthorizationInfo(request).ConfigureAwait(false);
        if (!authorizationInfo.IsAuthenticated || authorizationInfo.User is null
            || !Guid.TryParse(context.RouteData.Values["itemId"]?.ToString(), out var itemId)
            || !request.Query.TryGetValue("mediaSourceId", out var mediaSourceId)
            || mediaSourceId.Count != 1
            || string.IsNullOrWhiteSpace(mediaSourceId[0]))
        {
            return;
        }

        var item = libraryManager.GetItemById<BaseItem>(itemId, authorizationInfo.User);
        if (item is null)
        {
            return;
        }

        var mediaSource = await mediaSourceManager.GetMediaSource(
                item,
                mediaSourceId[0]!,
                request.Query["liveStreamId"].ToString(),
                false,
                request.HttpContext.RequestAborted)
            .ConfigureAwait(false);
        var requestedVideoStreamIndex = request.Query.TryGetValue("videoStreamIndex", out var videoStreamIndexValue)
            && int.TryParse(videoStreamIndexValue.ToString(), CultureInfo.InvariantCulture, out var parsedVideoStreamIndex)
            ? parsedVideoStreamIndex
            : (int?)null;
        var videoStream = mediaSource?.MediaStreams.FirstOrDefault(stream =>
            stream.Type == MediaStreamType.Video
            && (!requestedVideoStreamIndex.HasValue || stream.Index == requestedVideoStreamIndex.Value));
        var videoBitrate = videoStream?.BitRate;
        if ((!videoBitrate.HasValue || videoBitrate.Value <= 0) && mediaSource?.Bitrate is > 0)
        {
            var audioBitrate = mediaSource.MediaStreams
                .Where(stream => stream.Type == MediaStreamType.Audio)
                .Sum(stream => stream.BitRate ?? 0);
            var estimatedVideoBitrate = mediaSource.Bitrate.Value - audioBitrate;
            if (estimatedVideoBitrate > 0)
            {
                videoBitrate = estimatedVideoBitrate;
            }
        }

        if (videoStream is null || !videoBitrate.HasValue || videoBitrate.Value <= 0)
        {
            return;
        }

        var query = QueryHelpers.ParseQuery(request.QueryString.Value);
        query["videoBitrate"] = videoBitrate.Value.ToString(CultureInfo.InvariantCulture);
        if (videoStream.Width is > 0)
        {
            query["maxWidth"] = videoStream.Width.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (videoStream.Height is > 0)
        {
            query["maxHeight"] = videoStream.Height.Value.ToString(CultureInfo.InvariantCulture);
        }

        request.QueryString = QueryString.Create(query);
        SetActionArgument(context, "videoBitRate", videoBitrate.Value);
        SetActionArgument(context, "maxWidth", videoStream.Width);
        SetActionArgument(context, "maxHeight", videoStream.Height);
    }

    private static void SetActionArgument(ActionExecutingContext context, string name, object? value)
    {
        if (context.ActionArguments.ContainsKey(name))
        {
            context.ActionArguments[name] = value;
        }
    }
}
