using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Jellyfin.Plugin.StreamGenerator.Progress;

namespace Jellyfin.Plugin.StreamGenerator.Tests.Filters;

public class DynamicHlsContentInterceptionFilterTests
{
    private static readonly Guid ItemId = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    [Fact]
    public async Task NonDynamicHlsRequest_BypassesWithoutMutation()
    {
        var fixture = CreateFixture(controller: "Items", action: "GetItem");

        await fixture.Subject.OnActionExecutionAsync(fixture.Context, fixture.Next);

        fixture.NextCalls.Should().Be(1);
        fixture.Context.HttpContext.Request.Headers.UserAgent.ToString().Should().BeEmpty();
        fixture.Manager.Verify(x => x.GetActiveTranscodingJobs(), Times.Never);
    }

    [Fact]
    public async Task PlaceholderSession_AssignsStreamGeneratorSessionAndMutatesRequest()
    {
        var fixture = CreateFixture(query: "?mediaSourceId=source&segmentContainer=ts&segmentLength=6&api_key=secret");

        await fixture.Subject.OnActionExecutionAsync(fixture.Context, fixture.Next);

        var sessionId = fixture.Context.ActionArguments["playSessionId"].Should().BeOfType<string>().Subject;
        sessionId.Should().MatchRegex("^sg_[0-9a-f]{12}_[0-9a-f]{8}$");
        fixture.Context.ActionArguments["deviceId"].Should().Be(sessionId);
        fixture.Context.HttpContext.Request.Query["playSessionId"].ToString().Should().Be(sessionId);
        fixture.Context.HttpContext.Request.Query["deviceId"].ToString().Should().Be(sessionId);
        fixture.Context.HttpContext.Request.Query["api_key"].ToString().Should().Be("secret");
        fixture.Context.HttpContext.Request.Headers.UserAgent.ToString().Should().Be("StreamGenerator/1.0");
        fixture.NextCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExactStreamGeneratorSession_RemainsStableWhenAnotherJobHasSegment()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(query: "?mediaSourceId=source&segmentContainer=ts&segmentLength=6", segmentId: 3);
        var key = DynamicHlsContentInterceptionFilter.ComputeConfigKey(fixture.Context, fixture.Context.HttpContext.Request);
        var requestedSession = $"sg_{key}_11111111";
        var otherSession = $"sg_{key}_22222222";
        fixture.Context.ActionArguments["playSessionId"] = requestedSession;
        fixture.Context.HttpContext.Request.QueryString = new QueryString(
            $"?mediaSourceId=source&segmentContainer=ts&segmentLength=6&playSessionId={requestedSession}");

        var requestedPlaylist = Path.Combine(temp.Path, "requested.m3u8");
        var otherPlaylist = Path.Combine(temp.Path, "other.m3u8");
        File.WriteAllText(Path.Combine(temp.Path, "other3.ts"), "segment");
        fixture.Manager.Setup(x => x.GetActiveTranscodingJobs()).Returns(
        [
            CreateJob(requestedSession, requestedPlaylist, "requested-device"),
            CreateJob(otherSession, otherPlaylist, "other-device"),
        ]);

        await fixture.Subject.OnActionExecutionAsync(fixture.Context, fixture.Next);

        fixture.Context.ActionArguments["playSessionId"].Should().Be(requestedSession);
        fixture.Context.ActionArguments["deviceId"].Should().Be("requested-device");
    }

    [Theory]
    [InlineData("sg_abc_")]
    [InlineData("sg_000000000000_nothex")]
    [InlineData("sg_000000000000_111111111")]
    public async Task MalformedStreamGeneratorSession_Bypasses(string sessionId)
    {
        var fixture = CreateFixture(playSessionId: sessionId);

        await fixture.Subject.OnActionExecutionAsync(fixture.Context, fixture.Next);

        fixture.Context.ActionArguments["playSessionId"].Should().Be(sessionId);
        fixture.Manager.Verify(x => x.GetActiveTranscodingJobs(), Times.Never);
    }

    [Fact]
    public async Task ExistingFmp4InitSegment_ReusesItsJob()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(query: "?mediaSourceId=source&segmentContainer=mp4&segmentLength=6", segmentId: -1);
        var key = DynamicHlsContentInterceptionFilter.ComputeConfigKey(fixture.Context, fixture.Context.HttpContext.Request);
        var session = $"sg_{key}_12345678";
        var playlist = Path.Combine(temp.Path, "transcode.m3u8");
        File.WriteAllText(Path.Combine(temp.Path, "transcode-1.mp4"), "init");
        fixture.Manager.Setup(x => x.GetActiveTranscodingJobs()).Returns([CreateJob(session, playlist, "fmp4-device")]);

        await fixture.Subject.OnActionExecutionAsync(fixture.Context, fixture.Next);

        fixture.Context.ActionArguments["playSessionId"].Should().Be(session);
        fixture.Context.ActionArguments["deviceId"].Should().Be("fmp4-device");
    }

    [Fact]
    public async Task SuccessfulResponse_TracksOnlyAfterResponseCompletes()
    {
        var observation = new SegmentProgressObservation(Guid.NewGuid(), ItemId, 60_000_000);
        var fixture = CreateFixture();
        fixture.ProgressTracker
            .Setup(x => x.CreateObservationAsync(fixture.Context))
            .ReturnsAsync(observation);

        await fixture.Subject.OnActionExecutionAsync(fixture.Context, fixture.Next);

        fixture.ProgressTracker.Verify(x => x.Track(It.IsAny<SegmentProgressObservation>()), Times.Never);

        await fixture.ResponseFeature.CompleteAsync();

        fixture.ProgressTracker.Verify(x => x.Track(observation), Times.Once);
    }

    [Fact]
    public async Task FailedResponse_DoesNotTrackAfterResponseCompletes()
    {
        var observation = new SegmentProgressObservation(Guid.NewGuid(), ItemId, 60_000_000);
        var fixture = CreateFixture();
        fixture.ProgressTracker
            .Setup(x => x.CreateObservationAsync(fixture.Context))
            .ReturnsAsync(observation);
        fixture.Context.HttpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        await fixture.Subject.OnActionExecutionAsync(fixture.Context, fixture.Next);
        await fixture.ResponseFeature.CompleteAsync();

        fixture.ProgressTracker.Verify(x => x.Track(It.IsAny<SegmentProgressObservation>()), Times.Never);
    }

    [Fact]
    public async Task ObservationFailure_DoesNotPreventSegmentAction()
    {
        var fixture = CreateFixture();
        fixture.ProgressTracker
            .Setup(x => x.CreateObservationAsync(fixture.Context))
            .ThrowsAsync(new InvalidOperationException("tracking failed"));

        await fixture.Subject.OnActionExecutionAsync(fixture.Context, fixture.Next);

        fixture.NextCalls.Should().Be(1);
    }

    [Fact]
    public void ConfigKey_TsAndFmp4AreDifferent()
    {
        var ts = CreateFixture(query: "?mediaSourceId=source&segmentContainer=ts&segmentLength=6");
        var mp4 = CreateFixture(query: "?mediaSourceId=source&segmentContainer=mp4&segmentLength=6");

        var tsKey = DynamicHlsContentInterceptionFilter.ComputeConfigKey(ts.Context, ts.Context.HttpContext.Request);
        var mp4Key = DynamicHlsContentInterceptionFilter.ComputeConfigKey(mp4.Context, mp4.Context.HttpContext.Request);

        tsKey.Should().NotBe(mp4Key);
    }

    [Fact]
    public void ConfigKey_DefaultsEqualExplicitTsAndSixSeconds()
    {
        var defaults = CreateFixture(query: "?mediaSourceId=source", includeSegmentArguments: false);
        var explicitValues = CreateFixture(query: "?mediaSourceId=source&segmentContainer=ts&segmentLength=6");

        var defaultKey = DynamicHlsContentInterceptionFilter.ComputeConfigKey(defaults.Context, defaults.Context.HttpContext.Request);
        var explicitKey = DynamicHlsContentInterceptionFilter.ComputeConfigKey(explicitValues.Context, explicitValues.Context.HttpContext.Request);

        defaultKey.Should().Be(explicitKey);
    }

    [Theory]
    [InlineData("subtitleCodec", "vtt")]
    [InlineData("maxWidth", "1280")]
    [InlineData("audioBitRate", "192000")]
    [InlineData("videoStreamIndex", "1")]
    public void ConfigKey_OutputSettingChangesKey(string parameter, string value)
    {
        var baseline = CreateFixture(query: "?mediaSourceId=source&segmentContainer=ts&segmentLength=6");
        var changed = CreateFixture(query: $"?mediaSourceId=source&segmentContainer=ts&segmentLength=6&{parameter}={value}");

        var baselineKey = DynamicHlsContentInterceptionFilter.ComputeConfigKey(baseline.Context, baseline.Context.HttpContext.Request);
        var changedKey = DynamicHlsContentInterceptionFilter.ComputeConfigKey(changed.Context, changed.Context.HttpContext.Request);

        changedKey.Should().NotBe(baselineKey);
    }

    [Fact]
    public void ConfigKey_NewOutputParameterAutomaticallyChangesKey()
    {
        var baseline = CreateFixture(query: "?mediaSourceId=source&segmentContainer=ts&segmentLength=6");
        var changed = CreateFixture(query: "?mediaSourceId=source&segmentContainer=ts&segmentLength=6&futureEncodingOption=value");

        var baselineKey = DynamicHlsContentInterceptionFilter.ComputeConfigKey(baseline.Context, baseline.Context.HttpContext.Request);
        var changedKey = DynamicHlsContentInterceptionFilter.ComputeConfigKey(changed.Context, changed.Context.HttpContext.Request);

        changedKey.Should().NotBe(baselineKey);
    }

    [Theory]
    [InlineData("api_key", "other-token")]
    [InlineData("ApiKey", "other-token")]
    [InlineData("deviceId", "other-device")]
    [InlineData("playSessionId", "other-session")]
    [InlineData("runtimeTicks", "60000000")]
    [InlineData("actualSegmentLengthTicks", "60000000")]
    public void ConfigKey_RequestIdentityAndSegmentPositionDoNotChangeKey(string parameter, string value)
    {
        var baseline = CreateFixture(query: "?mediaSourceId=source&segmentContainer=ts&segmentLength=6");
        var changed = CreateFixture(query: $"?mediaSourceId=source&segmentContainer=ts&segmentLength=6&{parameter}={value}");

        var baselineKey = DynamicHlsContentInterceptionFilter.ComputeConfigKey(baseline.Context, baseline.Context.HttpContext.Request);
        var changedKey = DynamicHlsContentInterceptionFilter.ComputeConfigKey(changed.Context, changed.Context.HttpContext.Request);

        changedKey.Should().Be(baselineKey);
    }

    [Fact]
    public void ConfigKey_QueryOrderDoesNotChangeKey()
    {
        var first = CreateFixture(query: "?mediaSourceId=source&videoCodec=h264&segmentContainer=ts&segmentLength=6");
        var second = CreateFixture(query: "?segmentLength=6&segmentContainer=ts&videoCodec=h264&mediaSourceId=source");

        var firstKey = DynamicHlsContentInterceptionFilter.ComputeConfigKey(first.Context, first.Context.HttpContext.Request);
        var secondKey = DynamicHlsContentInterceptionFilter.ComputeConfigKey(second.Context, second.Context.HttpContext.Request);

        secondKey.Should().Be(firstKey);
    }

    private static Fixture CreateFixture(
        string controller = "DynamicHls",
        string action = "GetHlsVideoSegment",
        string playSessionId = "stream_generator_random",
        string query = "?mediaSourceId=source&segmentContainer=ts&segmentLength=6",
        int segmentId = 0,
        bool includeSegmentArguments = true)
    {
        var httpContext = new DefaultHttpContext();
        var responseFeature = new TestHttpResponseFeature();
        httpContext.Features.Set<IHttpResponseFeature>(responseFeature);
        httpContext.Request.QueryString = new QueryString(query);
        var routeData = new RouteData();
        routeData.Values["controller"] = controller;
        routeData.Values["action"] = action;
        routeData.Values["itemId"] = ItemId.ToString("N");
        var actionContext = new ActionContext(httpContext, routeData, new ControllerActionDescriptor(), new ModelStateDictionary());
        var arguments = new Dictionary<string, object?>
        {
            ["playSessionId"] = playSessionId,
            ["deviceId"] = "stream_generator",
            ["segmentId"] = segmentId,
            ["streamOptions"] = new Dictionary<string, string>
            {
                ["playSessionId"] = playSessionId,
                ["deviceId"] = "stream_generator",
            },
        };
        if (includeSegmentArguments)
        {
            arguments["segmentContainer"] = httpContext.Request.Query["segmentContainer"].ToString();
            arguments["segmentLength"] = int.TryParse(httpContext.Request.Query["segmentLength"], out var length) ? length : null;
        }

        var context = new ActionExecutingContext(actionContext, [], arguments, new object());
        var manager = new Mock<IAdvancedTranscodeManager>();
        manager.Setup(x => x.GetActiveTranscodingJobs()).Returns([]);
        var progressTracker = new Mock<IPlaybackProgressTracker>();
        progressTracker.Setup(x => x.CreateObservationAsync(It.IsAny<ActionExecutingContext>()))
            .ReturnsAsync((SegmentProgressObservation?)null);
        var subject = new DynamicHlsContentInterceptionFilter(
            manager.Object,
            progressTracker.Object,
            NullLogger<DynamicHlsContentInterceptionFilter>.Instance);
        var fixture = new Fixture(subject, context, manager, progressTracker, responseFeature);
        fixture.Next = () =>
        {
            fixture.NextCalls++;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
        };
        return fixture;
    }

    private static TranscodingJob CreateJob(string sessionId, string path, string deviceId)
        => new(NullLogger<TranscodingJob>.Instance)
        {
            PlaySessionId = sessionId,
            Path = path,
            DeviceId = deviceId,
        };

    private sealed class Fixture(
        DynamicHlsContentInterceptionFilter subject,
        ActionExecutingContext context,
        Mock<IAdvancedTranscodeManager> manager,
        Mock<IPlaybackProgressTracker> progressTracker,
        TestHttpResponseFeature responseFeature)
    {
        public DynamicHlsContentInterceptionFilter Subject { get; } = subject;
        public ActionExecutingContext Context { get; } = context;
        public Mock<IAdvancedTranscodeManager> Manager { get; } = manager;
        public Mock<IPlaybackProgressTracker> ProgressTracker { get; } = progressTracker;
        public TestHttpResponseFeature ResponseFeature { get; } = responseFeature;
        public ActionExecutionDelegate Next { get; set; } = null!;
        public int NextCalls { get; set; }
    }

    private sealed class TestHttpResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _completedCallbacks = [];

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => false;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
            => _completedCallbacks.Add((callback, state));

        public async Task CompleteAsync()
        {
            foreach (var (callback, state) in _completedCallbacks.AsEnumerable().Reverse())
            {
                await callback(state);
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stream-generator-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
