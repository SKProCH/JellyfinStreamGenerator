using System.Globalization;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.StreamGenerator;

internal static class SubtitleOffset {
    internal static string Apply(string arguments, StreamState state) {
        if (state.SubtitleStream is null
            || state.SubtitleDeliveryMethod != SubtitleDeliveryMethod.Encode
            || state.Request.StreamOptions is not { } options
            || !options.TryGetValue("subtitleOffset", out var value)
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds == 0) {
            return arguments;
        }

        return ShiftFilter(arguments, seconds, state.SubtitleStream.IsTextSubtitleStream,
            state.SubtitleStream.IsExternal ? 1 : 0,
            EncodingHelper.GetSubtitleStreamIndexForFfmpeg(state.MediaSource, state.SubtitleStream));
    }

    internal static string ShiftFilter(string arguments, double seconds, bool text, int input, int stream) {
        if (!double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds));
        var offset = seconds.ToString("R", CultureInfo.InvariantCulture);
        var (start, length) = FindFilterArgument(arguments);
        var filter = arguments.AsSpan(start, length);
        if (!text) {
            var label = $"[{input}:{stream}]";
            var position = filter.IndexOf(label, StringComparison.Ordinal);
            if (position < 0)
                throw new InvalidOperationException("Cannot locate the subtitle input for the requested offset.");
            return arguments.Insert(start + position + label.Length, $"setpts=PTS-({offset})/TB,");
        }

        var filterStart = filter.IndexOf("subtitles=f=", StringComparison.Ordinal);
        if (filterStart < 0)
            throw new InvalidOperationException("Cannot locate the subtitle filter for the requested offset.");
        // Commas inside quoted or escaped filenames are part of the filter value.
        var end = filterStart;
        var quoted = false;
        for (; end < filter.Length; end++) {
            var character = filter[end];
            if (character == '\\') {
                end++;
                continue;
            }

            if (character == '\'') {
                quoted = !quoted;
                continue;
            }

            if (!quoted && (character == ',' || character == ';' || character == '[' || character == '"')) break;
        }

        // Match Jellyfin Web: positive advances subtitles; negative delays them.
        return arguments.Insert(start + end, $",setpts=PTS-({offset})/TB")
            .Insert(start + filterStart, $"setpts=PTS+({offset})/TB,");
    }

    // Like ReplayGain's tokenizer, retain source positions so unrelated arguments stay intact.
    private static (int Start, int Length) FindFilterArgument(string arguments) {
        var expectFilter = false;
        for (var index = 0; index < arguments.Length;) {
            while (index < arguments.Length && char.IsWhiteSpace(arguments[index])) index++;
            if (index == arguments.Length) break;
            var start = index;
            var quote = '\0';
            while (index < arguments.Length) {
                var current = arguments[index];
                if (current == '\\' && quote == '"' && index + 1 < arguments.Length) {
                    index += 2;
                    continue;
                }

                if (quote == '\0' && (current == '"' || current == '\'')) quote = current;
                else if (current == quote) quote = '\0';
                else if (quote == '\0' && char.IsWhiteSpace(current)) break;
                index++;
            }

            if (quote != '\0') throw new InvalidOperationException("Unterminated FFmpeg argument.");
            var length = index - start;
            if (length >= 2 && (arguments[start] == '"' || arguments[start] == '\'')) {
                start++;
                length -= 2;
            }

            if (expectFilter) return (start, length);
            var token = arguments.AsSpan(start, length);
            expectFilter = token is "-vf" || token is "-filter:v" || token is "-filter_complex";
        }

        throw new InvalidOperationException("Cannot locate the FFmpeg video filter for the requested offset.");
    }
}