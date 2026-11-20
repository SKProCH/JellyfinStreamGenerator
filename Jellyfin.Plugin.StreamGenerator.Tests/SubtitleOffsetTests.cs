using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Jellyfin.Plugin.StreamGenerator.Tests;

public class SubtitleOffsetTests {
    [Fact]
    public void LaterDecorator_PreservesAdvancedManagerAndItsInnerService() {
        var inner = new Mock<ITranscodeManager>();
        var services = new ServiceCollection();
        services.AddSingleton(inner.Object);
        services.AddSingleton(Mock.Of<IAuthorizationContext>());
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        var index = services.ToList().FindLastIndex(descriptor =>
            descriptor.ServiceType == typeof(ITranscodeManager) && !descriptor.IsKeyedService);
        var factory = services[index].ImplementationFactory!;
        ITranscodeManager? wrapped = null;
        services[index] = ServiceDescriptor.Singleton<ITranscodeManager>(provider => {
            wrapped = (ITranscodeManager)factory(provider);
            var outer = new Mock<ITranscodeManager>();
            outer.Setup(manager => manager.GetTranscodingJob("session"))
                .Returns(() => wrapped.GetTranscodingJob("session"));
            return outer.Object;
        });
        using var provider = services.BuildServiceProvider();

        var publicManager = provider.GetRequiredService<ITranscodeManager>();
        var advanced = provider.GetRequiredService<IAdvancedTranscodeManager>();
        advanced.Should().BeSameAs(wrapped);
        publicManager.GetTranscodingJob("session");
        inner.Verify(manager => manager.GetTranscodingJob("session"), Times.Once);
    }

    [Theory]
    [InlineData(SubtitleDeliveryMethod.Encode, true)]
    [InlineData(SubtitleDeliveryMethod.Hls, false)]
    public async Task
        StartFfMpeg_UsesOffsetFromStreamOptionsOnlyForBurnIn(SubtitleDeliveryMethod method, bool shifted) {
        var inner = new Mock<ITranscodeManager>();
        var services = new ServiceCollection();
        services.AddSingleton(inner.Object);
        services.AddSingleton(Mock.Of<IAuthorizationContext>());
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ITranscodeManager>();
        using var cancellation = new CancellationTokenSource();
        using var state = new StreamState(Mock.Of<IMediaSourceManager>(), TranscodingJobType.Hls, inner.Object) {
            Request = new VideoRequestDto
                { StreamOptions = new Dictionary<string, string> { ["subtitleOffset"] = "1.5" } },
            SubtitleStream = new MediaStream { IsExternal = true, Type = MediaStreamType.Subtitle, Codec = "srt" },
            SubtitleDeliveryMethod = method,
            MediaSource = new MediaSourceInfo { MediaStreams = [] }
        };
        var arguments = "-vf \"subtitles=f='real.srt'\"";

        await manager.StartFfMpeg(state, "out.ts", arguments, Guid.Empty, TranscodingJobType.Hls, cancellation);

        var expected = shifted ? "-vf \"setpts=PTS+(1.5)/TB,subtitles=f='real.srt',setpts=PTS-(1.5)/TB\"" : arguments;
        inner.Verify(
            value => value.StartFfMpeg(state, "out.ts", expected, Guid.Empty, TranscodingJobType.Hls, cancellation,
                null), Times.Once);
    }

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(-2.25, "-2.25")]
    public void TextFilter_ShiftsSubtitleClockAndRestoresVideoClock(double seconds, string value) {
        var result = SubtitleOffset.ShiftFilter(
            " -vf \"scale=640:360,subtitles=f='C\\:/subs/a,b.ass':fontsdir='fonts',format=yuv420p\"", seconds, true, 0,
            2);
        result.Should()
            .Be(
                $" -vf \"scale=640:360,setpts=PTS+({value})/TB,subtitles=f='C\\:/subs/a,b.ass':fontsdir='fonts',setpts=PTS-({value})/TB,format=yuv420p\"");
    }

    [Fact]
    public void GraphicalFilter_ShiftsOnlySubtitleInput() {
        SubtitleOffset
            .ShiftFilter(
                " -filter_complex \"[1:0]scale=640:360[sub];[0:0]scale=640:360[main];[main][sub]overlay=eof_action=pass\"",
                -1.25, false, 1, 0)
            .Should().Be(
                " -filter_complex \"[1:0]setpts=PTS-(-1.25)/TB,scale=640:360[sub];[0:0]scale=640:360[main];[main][sub]overlay=eof_action=pass\"");
    }

    [Fact]
    public void TextOverlay_RestoresClockBeforeSubtitleOutputLabel() {
        SubtitleOffset
            .ShiftFilter(
                " -filter_complex \"alphasrc=s=640x360,subtitles=f='a.ass':alpha=1:sub2video=1[sub];[0:0][sub]overlay\"",
                2, true, 0, 2)
            .Should().Be(
                " -filter_complex \"alphasrc=s=640x360,setpts=PTS+(2)/TB,subtitles=f='a.ass':alpha=1:sub2video=1,setpts=PTS-(2)/TB[sub];[0:0][sub]overlay\"");
    }

    [Fact]
    public void FilterArgument_IgnoresSubtitleTextInInputFilename() {
        SubtitleOffset.ShiftFilter("-i \"subtitles=f=decoy.mkv\" -vf \"subtitles=f='real.srt'\" -y output.ts", 1, true,
                0, 2)
            .Should().Be(
                "-i \"subtitles=f=decoy.mkv\" -vf \"setpts=PTS+(1)/TB,subtitles=f='real.srt',setpts=PTS-(1)/TB\" -y output.ts");
    }

    [Fact]
    public void FilterArgument_AcceptsWhitespaceAndSingleQuotes() {
        SubtitleOffset.ShiftFilter("-filter:v\t'subtitles=f=real.srt' -y output.ts", -0.1, true, 0, 2)
            .Should().Be("-filter:v\t'setpts=PTS+(-0.1)/TB,subtitles=f=real.srt,setpts=PTS-(-0.1)/TB' -y output.ts");
    }

    [Theory]
    [InlineData(-120.123, "-120.123")]
    [InlineData(120.123, "120.123")]
    public void ShiftFilter_AcceptsOffsetOutsideSliderRange(double seconds, string value) {
        var result = SubtitleOffset.ShiftFilter("-vf \"subtitles=f='a.srt'\"", seconds, true, 0, 0);
        result.Should().Be($"-vf \"setpts=PTS+({value})/TB,subtitles=f='a.srt',setpts=PTS-({value})/TB\"");
    }
}