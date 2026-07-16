using Jellyfin.Plugin.StreamGenerator.Authorization;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.StreamGenerator.Tests.Authorization;

public class StreamTokenPathScopeTests
{
    private const string ItemN = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ItemD = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Source = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData($"/Videos/{ItemN}/master.m3u8")]
    [InlineData($"/videos/{ItemD}/MAIN.M3U8")]
    [InlineData($"/Videos/{ItemN}/hls1/main/0.ts")]
    [InlineData($"/Videos/{ItemN}/hls1/main/42.mp4")]
    [InlineData($"/Videos/{ItemN}/hls1/main/-1.mp4")]
    [InlineData($"/Videos/{ItemN}/{Source}/Subtitles/2/subtitles.m3u8")]
    [InlineData($"/Videos/{ItemN}/{Source}/Subtitles/2/stream.vtt")]
    public void TryParse_AllowedPath_ReturnsScope(string path)
    {
        var request = path.Contains("/Subtitles/", StringComparison.OrdinalIgnoreCase)
            ? CreateRequest(path)
            : CreateRequest(path, $"?mediaSourceId={Source}");

        StreamTokenPathScope.TryParse(request, out var scope).Should().BeTrue();
        scope!.ItemId.Should().Be(Guid.Parse(ItemN));
    }

    [Theory]
    [InlineData($"/Videos/{ItemN}/Trickplay/320/tiles.m3u8")]
    [InlineData($"/Videos/{ItemN}/Trickplay/320/4.jpg")]
    public void TryParse_TrickplayWithMediaSource_ReturnsScope(string path)
    {
        var request = CreateRequest(path, $"?MediaSourceId={Source}");

        StreamTokenPathScope.TryParse(request, out var scope).Should().BeTrue();
        scope!.MediaSourceId.Should().Be(Source);
    }

    [Theory]
    [InlineData($"/Items/{ItemN}")]
    [InlineData($"/Videos/{ItemN}/arbitrary/file.m3u8")]
    [InlineData($"/Videos/{ItemN}/live.m3u8")]
    [InlineData($"/Videos/{ItemN}/arbitrary/file.mp4")]
    [InlineData($"/Videos/{ItemN}/hls1/main/-2.mp4")]
    [InlineData($"/Videos/{ItemN}/hls1/main/-1.ts")]
    [InlineData($"/Videos/{ItemN}/hls1/other/1.mp4")]
    [InlineData($"/Videos/{ItemN}/hls1/main/1.m4s")]
    [InlineData($"/Videos/{ItemN}/Trickplay/0/tiles.m3u8")]
    [InlineData($"/Videos/{ItemN}/Trickplay/320/-1.jpg")]
    [InlineData($"/Videos/{ItemN}/Trickplay/320/1.png")]
    [InlineData($"/Videos/{ItemN}/master.m3u8/extra")]
    [InlineData($"/prefix/Videos/{ItemN}/master.m3u8")]
    [InlineData("/Videos/not-a-guid/master.m3u8")]
    public void TryParse_DisallowedPath_ReturnsFalse(string path)
    {
        StreamTokenPathScope.TryParse(CreateRequest(path), out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_TrickplayWithoutMediaSource_ReturnsFalse()
    {
        StreamTokenPathScope.TryParse(CreateRequest($"/Videos/{ItemN}/Trickplay/320/1.jpg"), out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_SubtitleCrossItemOverride_ReturnsFalse()
    {
        var request = CreateRequest(
            $"/Videos/{ItemN}/{Source}/Subtitles/2/stream.vtt",
            "?itemId=cccccccccccccccccccccccccccccccc");

        StreamTokenPathScope.TryParse(request, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_SubtitleCrossSourceOverride_ReturnsFalse()
    {
        var request = CreateRequest(
            $"/Videos/{ItemN}/{Source}/Subtitles/2/stream.vtt",
            "?mediaSourceId=cccccccccccccccccccccccccccccccc");

        StreamTokenPathScope.TryParse(request, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_DuplicateIdentityOverride_ReturnsFalse()
    {
        var request = CreateRequest(
            $"/Videos/{ItemN}/{Source}/Subtitles/2/stream.vtt",
            $"?mediaSourceId={Source}&mediaSourceId={Source}");

        StreamTokenPathScope.TryParse(request, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("?index=3")]
    [InlineData("?index=-1")]
    [InlineData("?format=srt")]
    [InlineData("?format=vtt&format=vtt")]
    public void TryParse_InvalidSubtitleOverride_ReturnsFalse(string query)
    {
        var request = CreateRequest($"/Videos/{ItemN}/{Source}/Subtitles/2/stream.vtt", query);

        StreamTokenPathScope.TryParse(request, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_MatchingSubtitleOverrides_ReturnsScope()
    {
        var request = CreateRequest(
            $"/Videos/{ItemN}/{Source}/Subtitles/2/stream.vtt",
            $"?itemId={ItemD}&mediaSourceId={Source}&index=2&format=vtt");

        StreamTokenPathScope.TryParse(request, out var scope).Should().BeTrue();
        scope!.Kind.Should().Be(StreamTokenResourceKind.Subtitle);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public void TryParse_UnsafeMethod_ReturnsFalse(string method)
    {
        StreamTokenPathScope.TryParse(CreateRequest($"/Videos/{ItemN}/master.m3u8", method: method), out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_HeadMainPlaylist_ReturnsFalse()
    {
        StreamTokenPathScope.TryParse(CreateRequest($"/Videos/{ItemN}/main.m3u8", method: "HEAD"), out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_HeadMasterPlaylist_ReturnsScope()
    {
        var request = CreateRequest($"/Videos/{ItemN}/master.m3u8", $"?mediaSourceId={Source}", "HEAD");

        StreamTokenPathScope.TryParse(request, out var scope).Should().BeTrue();
        scope!.ItemId.Should().Be(Guid.Parse(ItemN));
    }

    [Theory]
    [InlineData("?mediaSourceId=")]
    [InlineData($"?mediaSourceId={Source}&MediaSourceId={Source}")]
    public void TryParse_InvalidRequiredMediaSource_ReturnsFalse(string query)
    {
        var request = CreateRequest($"/Videos/{ItemN}/master.m3u8", query);

        StreamTokenPathScope.TryParse(request, out _).Should().BeFalse();
    }

    private static HttpRequest CreateRequest(string path, string? query = null, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query ?? string.Empty);
        return context.Request;
    }
}
