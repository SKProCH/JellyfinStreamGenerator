using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.StreamGenerator.Progress;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.StreamGenerator;

public partial class DynamicHlsContentInterceptionFilter(
    IAdvancedTranscodeManager advancedTranscodeManager,
    IPlaybackProgressTracker playbackProgressTracker,
    ILogger<DynamicHlsContentInterceptionFilter> logger) : IAsyncActionFilter
{
    private const string SessionPrefix = "sg_";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;

        if (!TryParseStreamGeneratorSegmentRequest(context, request, out var segmentId, out var configKey, out var requestedSessionId))
        {
            await next();
            return;
        }

        request.Headers["User-Agent"] = "StreamGenerator/1.0";
        SegmentProgressObservation? progressObservation = null;
        try
        {
            progressObservation = await playbackProgressTracker.CreateObservationAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to prepare generated-link playback progress tracking");
        }

        var prefix = $"{SessionPrefix}{configKey}_";

        var activeJobs = advancedTranscodeManager.GetActiveTranscodingJobs();
        var candidateJobs = activeJobs
            .Where(j => j.PlaySessionId != null
                        && j.PlaySessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && !j.HasExited
                        && j.Path != null)
            .ToList();

        string? playSessionId = IsSessionIdForPrefix(requestedSessionId, prefix)
            ? requestedSessionId
            : null;
        string? deviceId = null;

        if (playSessionId is not null)
        {
            var requestedJob = candidateJobs.FirstOrDefault(job =>
                string.Equals(job.PlaySessionId, playSessionId, StringComparison.OrdinalIgnoreCase));
            deviceId = requestedJob?.DeviceId ?? playSessionId;
        }

        // First: check if the segment file already exists on disk for any candidate session
        foreach (var job in playSessionId is null ? candidateJobs : [])
        {
            var segmentPath = GetSegmentPath(job.Path!, segmentId, context);
            if (segmentPath != null && File.Exists(segmentPath))
            {
                playSessionId = job.PlaySessionId!;
                deviceId = job.DeviceId ?? playSessionId;
                logger.LogInformation(
                    "Reusing session {PlaySessionId} — segment {SegmentId} already exists on disk",
                    playSessionId, segmentId);
                break;
            }
        }

        // Second: check if any active transcode is close enough to produce this segment soon
        if (playSessionId == null && segmentId >= 0)
        {
            foreach (var job in candidateJobs)
            {
                var currentIndex = GetCurrentTranscodingIndex(job.Path!);
                if (currentIndex == null) continue;

                var segmentLength = GetSegmentLength(context);
                var maxGap = 24 / segmentLength;

                if (segmentId >= currentIndex.Value && segmentId - currentIndex.Value <= maxGap)
                {
                    playSessionId = job.PlaySessionId!;
                    deviceId = job.DeviceId ?? playSessionId;
                    logger.LogInformation(
                        "Reusing session {PlaySessionId} — segment {SegmentId} within transcode reach (current: {CurrentIndex}, gap: {Gap})",
                        playSessionId, segmentId, currentIndex.Value, segmentId - currentIndex.Value);
                    break;
                }
            }
        }

        // Third: new session with random suffix
        if (playSessionId == null)
        {
            playSessionId = $"{prefix}{Random.Shared.Next():x8}";
            deviceId = playSessionId;
            logger.LogDebug("Assigning new transcode session: {PlaySessionId} for segment {SegmentId}",
                playSessionId, segmentId);
        }

        context.ActionArguments["playSessionId"] = playSessionId;
        if (context.ActionArguments.ContainsKey("deviceId"))
            context.ActionArguments["deviceId"] = deviceId!;

        if (context.ActionArguments.TryGetValue("streamOptions", out var optObj)
            && optObj is Dictionary<string, string> so)
        {
            if (so.ContainsKey("playSessionId")) so["playSessionId"] = playSessionId;
            if (so.ContainsKey("deviceId")) so["deviceId"] = deviceId!;
        }

        var queryDict = QueryHelpers.ParseQuery(request.QueryString.Value);
        queryDict["playSessionId"] = new StringValues(playSessionId);
        queryDict["deviceId"] = new StringValues(deviceId);
        request.QueryString = QueryString.Create(queryDict);

        var executedContext = await next();
        if (progressObservation.HasValue && WasSuccessful(executedContext))
        {
            var response = context.HttpContext.Response;
            var requestAborted = context.HttpContext.RequestAborted;
            response.OnCompleted(() =>
            {
                if (response.StatusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices
                    && !requestAborted.IsCancellationRequested)
                {
                    playbackProgressTracker.Track(progressObservation.Value);
                }

                return Task.CompletedTask;
            });
        }
    }

    private static bool TryParseStreamGeneratorSegmentRequest(
        ActionExecutingContext context,
        HttpRequest request,
        out int segmentId,
        out string configKey,
        out string requestedSessionId)
    {
        segmentId = 0;
        configKey = string.Empty;
        requestedSessionId = string.Empty;

        if (!context.RouteData.Values.TryGetValue("controller", out var c) || c is not "DynamicHls")
            return false;

        if (!context.RouteData.Values.TryGetValue("action", out var a))
            return false;

        var action = a?.ToString();
        if (action is not ("GetHlsVideoSegment" or "GetHlsAudioSegment"))
            return false;

        if (!context.ActionArguments.TryGetValue("playSessionId", out var psObj))
            return false;

        var playSessionIdStr = psObj?.ToString();
        if (playSessionIdStr is not "stream_generator_random"
            && !IsStreamGeneratorSessionId(playSessionIdStr))
        {
            // Also check query string
            if (!request.Query.TryGetValue("playSessionId", out var qps) || qps.Count != 1)
                return false;
            var qpsStr = qps.ToString();
            if (qpsStr is not "stream_generator_random"
                && !IsStreamGeneratorSessionId(qpsStr))
                return false;
            playSessionIdStr = qpsStr;
        }

        requestedSessionId = playSessionIdStr!;

        if (context.ActionArguments.TryGetValue("segmentId", out var segObj)
            && int.TryParse(segObj?.ToString(), out var sid))
        {
            segmentId = sid;
        }

        configKey = ComputeConfigKey(context, request);
        return true;
    }

    internal static string ComputeConfigKey(ActionExecutingContext context, HttpRequest request)
    {
        var itemId = context.RouteData.Values.TryGetValue("itemId", out var id) ? id?.ToString() : "";
        var excludedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "api_key",
            "ApiKey",
            "deviceId",
            "playSessionId",
            "runtimeTicks",
            "actualSegmentLengthTicks",
        };
        var parameters = QueryHelpers.ParseQuery(request.QueryString.Value)
            .Where(parameter => !excludedKeys.Contains(parameter.Key)
                                && !parameter.Key.Equals("segmentContainer", StringComparison.OrdinalIgnoreCase)
                                && !parameter.Key.Equals("segmentLength", StringComparison.OrdinalIgnoreCase))
            .Select(parameter => $"{parameter.Key.ToLowerInvariant()}={parameter.Value}")
            .Order(StringComparer.Ordinal)
            .Append($"segmentcontainer={GetSegmentContainer(context).TrimStart('.')}")
            .Append($"segmentlength={GetSegmentLength(context).ToString(CultureInfo.InvariantCulture)}");
        var input = $"{itemId}|{string.Join('|', parameters)}";

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();
    }

    private static string? GetSegmentPath(string playlistPath, int segmentId, ActionExecutingContext context)
    {
        var folder = Path.GetDirectoryName(playlistPath);
        if (folder == null) return null;

        var filename = Path.GetFileNameWithoutExtension(playlistPath);
        var container = GetSegmentContainer(context);

        return Path.Combine(folder, filename + segmentId.ToString(CultureInfo.InvariantCulture) + container);
    }

    private static string GetSegmentContainer(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("segmentContainer", out var containerObj)
            && containerObj?.ToString() is { Length: > 0 } container)
        {
            return "." + NormalizeContainer(container);
        }

        return ".ts";
    }

    private static string NormalizeContainer(string container)
        => container.Trim().TrimStart('.').ToLowerInvariant();

    private static bool IsStreamGeneratorSessionId(string? sessionId)
        => sessionId is not null && StreamGeneratorSessionRegex().IsMatch(sessionId);

    private static bool IsSessionIdForPrefix(string sessionId, string prefix)
        => IsStreamGeneratorSessionId(sessionId)
           && sessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^sg_[0-9a-f]{12}_[0-9a-f]{8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StreamGeneratorSessionRegex();

    private static bool WasSuccessful(ActionExecutedContext context)
    {
        if (context.Canceled || context.Exception is not null && !context.ExceptionHandled)
        {
            return false;
        }

        return context.Result is not IStatusCodeActionResult { StatusCode: int statusCode }
               || statusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices;
    }

    private static int GetSegmentLength(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("segmentLength", out var lenObj)
            && int.TryParse(lenObj?.ToString(), out var len) && len > 0)
        {
            return len;
        }

        return 6;
    }

    private static int? GetCurrentTranscodingIndex(string playlistPath)
    {
        var folder = Path.GetDirectoryName(playlistPath);
        if (folder == null || !Directory.Exists(folder)) return null;

        var filePrefix = Path.GetFileNameWithoutExtension(playlistPath);

        try
        {
            var lastFile = Directory.EnumerateFiles(folder)
                .Select(f => Path.GetFileName(f))
                .Where(f => f.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase)
                            && !f.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                .Select(f =>
                {
                    var indexStr = Path.GetFileNameWithoutExtension(f.AsSpan()).Slice(filePrefix.Length);
                    return int.TryParse(indexStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ? idx : (int?)null;
                })
                .Where(i => i.HasValue)
                .Max();

            return lastFile;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
