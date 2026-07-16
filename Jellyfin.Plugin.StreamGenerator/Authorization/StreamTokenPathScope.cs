using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.StreamGenerator.Authorization;

internal enum StreamTokenResourceKind
{
    Playlist,
    Segment,
    Subtitle,
    Trickplay,
}

internal sealed record StreamTokenResourceScope(
    Guid ItemId,
    StreamTokenResourceKind Kind,
    string? MediaSourceId = null,
    int? SubtitleIndex = null);

internal static class StreamTokenPathScope
{
    internal static bool TryParse(HttpRequest request, out StreamTokenResourceScope? scope)
    {
        scope = null;
        var path = request.Path.Value;
        if (path is null || path.Contains('\\', StringComparison.Ordinal) || request.Method is not ("GET" or "HEAD"))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length < 4 || segments[0].Length != 0 || !segments[1].Equals("Videos", StringComparison.OrdinalIgnoreCase)
            || !TryParseRouteGuid(segments[2], out var itemId))
        {
            return false;
        }

        if (segments.Length == 4 && IsPlaylist(segments[3]))
        {
            if (request.Method == "HEAD" && !segments[3].Equals("master.m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!TryGetRequiredSingleQueryValue(request, "mediaSourceId", out var mediaSourceId))
            {
                return false;
            }

            scope = new StreamTokenResourceScope(
                itemId,
                StreamTokenResourceKind.Playlist,
                mediaSourceId);
            return true;
        }

        if (request.Method != "GET")
        {
            return false;
        }

        if (segments.Length == 6
            && segments[3].Equals("hls1", StringComparison.OrdinalIgnoreCase)
            && segments[4].Equals("main", StringComparison.OrdinalIgnoreCase)
            && TryParseSegment(segments[5]))
        {
            if (!TryGetRequiredSingleQueryValue(request, "mediaSourceId", out var mediaSourceId))
            {
                return false;
            }

            scope = new StreamTokenResourceScope(
                itemId,
                StreamTokenResourceKind.Segment,
                mediaSourceId);
            return true;
        }

        if (segments.Length == 7
            && segments[4].Equals("Subtitles", StringComparison.OrdinalIgnoreCase)
            && TryParseNonNegativeInt(segments[5], out var subtitleIndex)
            && (segments[6].Equals("subtitles.m3u8", StringComparison.OrdinalIgnoreCase)
                || segments[6].Equals("stream.vtt", StringComparison.OrdinalIgnoreCase)))
        {
            scope = new StreamTokenResourceScope(itemId, StreamTokenResourceKind.Subtitle, segments[3], subtitleIndex);
            return ValidateSubtitleOverrides(request, itemId, segments[3], subtitleIndex);
        }

        if (segments.Length == 6
            && segments[3].Equals("Trickplay", StringComparison.OrdinalIgnoreCase)
            && TryParsePositiveInt(segments[4], out _))
        {
            if (segments[5].Equals("tiles.m3u8", StringComparison.OrdinalIgnoreCase)
                || (segments[5].EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    && TryParseNonNegativeInt(segments[5][..^4], out _)))
            {
                if (!TryGetRequiredSingleQueryValue(request, "MediaSourceId", out var mediaSourceId))
                {
                    return false;
                }

                scope = new StreamTokenResourceScope(
                    itemId,
                    StreamTokenResourceKind.Trickplay,
                    mediaSourceId);
                return true;
            }
        }

        return false;
    }

    private static bool IsPlaylist(string value)
        => value.Equals("master.m3u8", StringComparison.OrdinalIgnoreCase)
           || value.Equals("main.m3u8", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseSegment(string filename)
    {
        var separator = filename.LastIndexOf('.');
        if (separator <= 0 || separator == filename.Length - 1)
        {
            return false;
        }

        var segmentName = filename[..separator];
        var extension = filename[(separator + 1)..];
        if (extension.Equals("ts", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseNonNegativeInt(segmentName, out _);
        }

        return extension.Equals("mp4", StringComparison.OrdinalIgnoreCase)
               && (segmentName == "-1" || TryParseNonNegativeInt(segmentName, out _));
    }

    private static bool ValidateSubtitleOverrides(HttpRequest request, Guid itemId, string mediaSourceId, int subtitleIndex)
    {
        if (!TryGetOptionalSingleQueryValue(request, "itemId", out var itemOverride)
            || !TryGetOptionalSingleQueryValue(request, "mediaSourceId", out var sourceOverride)
            || !TryGetOptionalSingleQueryValue(request, "index", out var indexOverride)
            || !TryGetOptionalSingleQueryValue(request, "format", out var formatOverride))
        {
            return false;
        }

        if (itemOverride is not null && (!TryParseRouteGuid(itemOverride, out var queryItemId) || queryItemId != itemId))
        {
            return false;
        }

        if (sourceOverride is not null && !sourceOverride.Equals(mediaSourceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (indexOverride is not null && (!TryParseNonNegativeInt(indexOverride, out var queryIndex) || queryIndex != subtitleIndex))
        {
            return false;
        }

        return formatOverride is null || formatOverride.Equals("vtt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetOptionalSingleQueryValue(HttpRequest request, string key, out string? value)
    {
        value = null;
        if (!request.Query.TryGetValue(key, out var values))
        {
            return true;
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return false;
        }

        value = values[0];
        return true;
    }

    private static bool TryGetRequiredSingleQueryValue(HttpRequest request, string key, out string value)
    {
        value = string.Empty;
        if (!request.Query.TryGetValue(key, out var values) || values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return false;
        }

        value = values[0]!;
        return true;
    }

    private static bool TryParseRouteGuid(string value, out Guid id)
        => Guid.TryParseExact(value, "N", out id) || Guid.TryParseExact(value, "D", out id);

    private static bool TryParsePositiveInt(string value, out int result)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

    private static bool TryParseNonNegativeInt(string value, out int result)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;
}
