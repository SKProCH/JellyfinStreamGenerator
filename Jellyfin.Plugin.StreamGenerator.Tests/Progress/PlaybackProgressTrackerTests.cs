using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.StreamGenerator.Configuration;
using Jellyfin.Plugin.StreamGenerator.Progress;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.StreamGenerator.Tests.Progress;

public class PlaybackProgressTrackerTests
{
    private static readonly Guid ItemId = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private const string Token = "11111111111111111111111111111111";
    private const string MediaSourceId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task CreateObservationAsync_EnabledToken_UsesEndOfSegment()
    {
        var fixture = CreateFixture(rememberProgress: true);
        var context = CreateActionContext(runtimeTicks: 120_000_000, segmentLengthTicks: 60_000_000);

        var observation = await fixture.Subject.CreateObservationAsync(context);

        observation.Should().Be(new SegmentProgressObservation(fixture.User.Id, ItemId, 180_000_000));
    }

    [Theory]
    [InlineData(false, 0, 60_000_000)]
    [InlineData(true, -1, 0)]
    [InlineData(true, 0, 0)]
    public async Task CreateObservationAsync_DisabledOrNonMediaSegment_ReturnsNull(
        bool rememberProgress,
        int segmentId,
        long segmentLengthTicks)
    {
        var fixture = CreateFixture(rememberProgress);
        var context = CreateActionContext(segmentId: segmentId, segmentLengthTicks: segmentLengthTicks);

        (await fixture.Subject.CreateObservationAsync(context)).Should().BeNull();
    }

    [Fact]
    public async Task CreateObservationAsync_RequestAuthenticatedWithAnotherToken_ReturnsNull()
    {
        var fixture = CreateFixture(authorizationToken: "different-token");

        var observation = await fixture.Subject.CreateObservationAsync(CreateActionContext());

        observation.Should().BeNull();
    }

    [Fact]
    public async Task CreateObservationAsync_MediaSourceOutsideItem_ReturnsNull()
    {
        var fixture = CreateFixture(mediaSourceIds: ["different-source"]);

        var observation = await fixture.Subject.CreateObservationAsync(CreateActionContext());

        observation.Should().BeNull();
    }

    [Fact]
    public void Track_HigherPosition_UpdatesAndSavesProgress()
    {
        var data = new UserItemData { Key = "key", PlaybackPositionTicks = 60_000_000 };
        var fixture = CreateFixture(data: data);
        fixture.UserDataManager
            .Setup(x => x.UpdatePlayState(fixture.Item, data, 120_000_000))
            .Callback(() => data.PlaybackPositionTicks = 120_000_000)
            .Returns(false);

        fixture.Subject.Track(new SegmentProgressObservation(fixture.User.Id, ItemId, 120_000_000));

        data.PlaybackPositionTicks.Should().Be(120_000_000);
        fixture.UserDataManager.Verify(
            x => x.SaveUserData(fixture.User, fixture.Item, data, UserDataSaveReason.PlaybackProgress, CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public void Track_LowerPosition_DoesNotRegressOrSave()
    {
        var data = new UserItemData { Key = "key", PlaybackPositionTicks = 120_000_000 };
        var fixture = CreateFixture(data: data);

        fixture.Subject.Track(new SegmentProgressObservation(fixture.User.Id, ItemId, 60_000_000));

        data.PlaybackPositionTicks.Should().Be(120_000_000);
        fixture.UserDataManager.Verify(x => x.UpdatePlayState(It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<long?>()), Times.Never);
        fixture.UserDataManager.Verify(
            x => x.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Track_AlreadyPlayedItem_RemainsCompleted()
    {
        var data = new UserItemData { Key = "key", Played = true, PlaybackPositionTicks = 0 };
        var fixture = CreateFixture(data: data);

        fixture.Subject.Track(new SegmentProgressObservation(fixture.User.Id, ItemId, 60_000_000));

        data.Played.Should().BeTrue();
        data.PlaybackPositionTicks.Should().Be(0);
        fixture.UserDataManager.Verify(x => x.UpdatePlayState(It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<long?>()), Times.Never);
    }

    [Fact]
    public void Track_JellyfinCompletion_SavesPlaybackFinishedAndKeepsResetPosition()
    {
        var data = new UserItemData { Key = "key", PlaybackPositionTicks = 800_000_000 };
        var fixture = CreateFixture(data: data);
        fixture.UserDataManager
            .Setup(x => x.UpdatePlayState(fixture.Item, data, 950_000_000))
            .Callback(() =>
            {
                data.Played = true;
                data.PlaybackPositionTicks = 0;
            })
            .Returns(true);

        fixture.Subject.Track(new SegmentProgressObservation(fixture.User.Id, ItemId, 950_000_000));

        data.Played.Should().BeTrue();
        data.PlaybackPositionTicks.Should().Be(0);
        fixture.UserDataManager.Verify(
            x => x.SaveUserData(fixture.User, fixture.Item, data, UserDataSaveReason.PlaybackFinished, CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public void Track_JellyfinRejectedNormalization_RestoresPreviousState()
    {
        var data = new UserItemData { Key = "key", PlaybackPositionTicks = 60_000_000 };
        var fixture = CreateFixture(data: data);
        fixture.UserDataManager
            .Setup(x => x.UpdatePlayState(fixture.Item, data, 120_000_000))
            .Callback(() => data.PlaybackPositionTicks = 0)
            .Returns(false);

        fixture.Subject.Track(new SegmentProgressObservation(fixture.User.Id, ItemId, 120_000_000));

        data.PlaybackPositionTicks.Should().Be(60_000_000);
        fixture.UserDataManager.Verify(
            x => x.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Track_SaveFailure_RestoresCachedState()
    {
        var data = new UserItemData { Key = "key", PlaybackPositionTicks = 60_000_000 };
        var fixture = CreateFixture(data: data);
        fixture.UserDataManager
            .Setup(x => x.UpdatePlayState(fixture.Item, data, 950_000_000))
            .Callback(() =>
            {
                data.Played = true;
                data.PlaybackPositionTicks = 0;
            })
            .Returns(true);
        fixture.UserDataManager
            .Setup(x => x.SaveUserData(fixture.User, fixture.Item, data, UserDataSaveReason.PlaybackFinished, CancellationToken.None))
            .Throws(new InvalidOperationException("save failed"));

        fixture.Subject.Track(new SegmentProgressObservation(fixture.User.Id, ItemId, 950_000_000));

        data.Played.Should().BeFalse();
        data.PlaybackPositionTicks.Should().Be(60_000_000);
    }

    [Fact]
    public async Task Track_ConcurrentOutOfOrderSegments_EndsAtHighestPosition()
    {
        var data = new UserItemData { Key = "key" };
        var fixture = CreateFixture(data: data);
        fixture.UserDataManager
            .Setup(x => x.UpdatePlayState(fixture.Item, data, It.IsAny<long?>()))
            .Callback<BaseItem, UserItemData, long?>((_, _, position) => data.PlaybackPositionTicks = position!.Value)
            .Returns(false);

        await Task.WhenAll(
            Task.Run(() => fixture.Subject.Track(new SegmentProgressObservation(fixture.User.Id, ItemId, 600_000_000))),
            Task.Run(() => fixture.Subject.Track(new SegmentProgressObservation(fixture.User.Id, ItemId, 540_000_000))));

        data.PlaybackPositionTicks.Should().Be(600_000_000);
    }

    private static Fixture CreateFixture(
        bool rememberProgress = true,
        UserItemData? data = null,
        string authorizationToken = Token,
        string[]? mediaSourceIds = null)
    {
        var user = new User("test", "auth", "reset");
        var item = new Mock<BaseItem>().Object;
        data ??= new UserItemData { Key = "key" };
        var configuration = new PluginConfiguration();
        configuration.StreamTokens[Token] = new StreamTokenInformation
        {
            UserId = user.Id,
            ItemId = ItemId.ToString("N"),
            RememberPlaybackProgress = rememberProgress,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var configurationAccessor = new Mock<IStreamGeneratorConfigurationAccessor>();
        configurationAccessor.SetupGet(x => x.Configuration).Returns(configuration);
        var authorizationContext = new Mock<IAuthorizationContext>();
        authorizationContext.Setup(x => x.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(new AuthorizationInfo
        {
            IsAuthenticated = true,
            User = user,
            Token = authorizationToken,
        });
        var userManager = new Mock<IUserManager>();
        userManager.Setup(x => x.GetUserById(user.Id)).Returns(user);
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(x => x.GetItemById<BaseItem>(ItemId, user)).Returns(item);
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(x => x.GetStaticMediaSources(item, false, user))
            .Returns((mediaSourceIds ?? [MediaSourceId]).Select(id => new MediaSourceInfo { Id = id }).ToArray());
        var userDataManager = new Mock<IUserDataManager>();
        userDataManager.Setup(x => x.GetUserData(user, item)).Returns(data);

        var subject = new PlaybackProgressTracker(
            configurationAccessor.Object,
            authorizationContext.Object,
            userManager.Object,
            libraryManager.Object,
            mediaSourceManager.Object,
            userDataManager.Object,
            NullLogger<PlaybackProgressTracker>.Instance);
        return new Fixture(subject, user, item, userDataManager);
    }

    private static ActionExecutingContext CreateActionContext(
        int segmentId = 2,
        long runtimeTicks = 120_000_000,
        long segmentLengthTicks = 60_000_000)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = $"/Videos/{ItemId:N}/hls1/main/{segmentId}.ts";
        httpContext.Request.QueryString = new QueryString($"?api_key={Token}&mediaSourceId={MediaSourceId}");
        var routeData = new RouteData();
        routeData.Values["controller"] = "DynamicHls";
        routeData.Values["action"] = "GetHlsVideoSegment";
        routeData.Values["itemId"] = ItemId.ToString("N");
        var actionContext = new ActionContext(httpContext, routeData, new ControllerActionDescriptor(), new ModelStateDictionary());
        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>
            {
                ["segmentId"] = segmentId,
                ["runtimeTicks"] = runtimeTicks,
                ["actualSegmentLengthTicks"] = segmentLengthTicks,
            },
            new object());
    }

    private sealed record Fixture(
        PlaybackProgressTracker Subject,
        User User,
        BaseItem Item,
        Mock<IUserDataManager> UserDataManager);
}
