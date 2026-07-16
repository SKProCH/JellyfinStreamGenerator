using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.StreamGenerator.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.StreamGenerator.Tests.Authorization;

public class CustomStreamTokensAuthorizationContextTests
{
    private static readonly Guid ItemId = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private const string Token = "11111111111111111111111111111111";
    private const string MediaSourceId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task GetAuthorizationInfo_ValidToken_ReturnsScopedUser()
    {
        var fixture = CreateFixture();
        var context = CreateContext($"/Videos/{ItemId:N}/master.m3u8", $"?api_key={Token}&mediaSourceId={MediaSourceId}");

        var result = await fixture.Subject.GetAuthorizationInfo(context);

        result.IsAuthenticated.Should().BeTrue();
        result.User.Should().BeSameAs(fixture.User);
        result.Token.Should().Be(Token);
        result.Client.Should().Be("StreamGenerator");
        result.DeviceId.Should().StartWith("StreamGenerator-");
        fixture.Inner.Verify(x => x.GetAuthorizationInfo(It.IsAny<HttpContext>()), Times.Never);
    }

    [Fact]
    public async Task GetAuthorizationInfo_ExplicitDeviceId_UsesIt()
    {
        var fixture = CreateFixture();
        var context = CreateContext(
            $"/Videos/{ItemId:N}/master.m3u8",
            $"?api_key={Token}&mediaSourceId={MediaSourceId}&deviceId=external-player");

        var result = await fixture.Subject.GetAuthorizationInfo(context.Request);

        result.DeviceId.Should().Be("external-player");
        fixture.Inner.Verify(x => x.GetAuthorizationInfo(It.IsAny<HttpRequest>()), Times.Never);
    }

    [Fact]
    public async Task GetAuthorizationInfo_MediaSourceBelongsToSameItem_AuthorizesAlternateVersion()
    {
        const string alternateSource = "cccccccccccccccccccccccccccccccc";
        var fixture = CreateFixture(false, MediaSourceId, alternateSource);
        var context = CreateContext(
            $"/Videos/{ItemId:N}/Trickplay/320/1.jpg",
            $"?api_key={Token}&MediaSourceId={alternateSource}");

        var result = await fixture.Subject.GetAuthorizationInfo(context);

        result.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task GetAuthorizationInfo_MediaSourceDoesNotBelongToItem_DelegatesToInner()
    {
        var fixture = CreateFixture();
        var context = CreateContext(
            $"/Videos/{ItemId:N}/Trickplay/320/1.jpg",
            $"?api_key={Token}&MediaSourceId=cccccccccccccccccccccccccccccccc");

        var result = await fixture.Subject.GetAuthorizationInfo(context);

        result.Should().BeSameAs(fixture.Fallback);
        fixture.Inner.Verify(x => x.GetAuthorizationInfo(context), Times.Once);
    }

    [Fact]
    public async Task GetAuthorizationInfo_OtherItem_DelegatesToInner()
    {
        var fixture = CreateFixture();
        var otherItem = Guid.NewGuid();
        var context = CreateContext(
            $"/Videos/{otherItem:N}/master.m3u8",
            $"?api_key={Token}&mediaSourceId={MediaSourceId}");

        var result = await fixture.Subject.GetAuthorizationInfo(context);

        result.Should().BeSameAs(fixture.Fallback);
    }

    [Fact]
    public async Task GetAuthorizationInfo_DuplicateApiKey_DelegatesToInner()
    {
        var fixture = CreateFixture();
        var context = CreateContext(
            $"/Videos/{ItemId:N}/master.m3u8",
            $"?api_key={Token}&api_key={Token}&mediaSourceId={MediaSourceId}");

        var result = await fixture.Subject.GetAuthorizationInfo(context);

        result.Should().BeSameAs(fixture.Fallback);
    }

    [Fact]
    public async Task GetAuthorizationInfo_CaseVariedApiKeyName_Authorizes()
    {
        var fixture = CreateFixture();
        var context = CreateContext(
            $"/Videos/{ItemId:N}/master.m3u8",
            $"?ApiKey={Token}&mediaSourceId={MediaSourceId}");

        var result = await fixture.Subject.GetAuthorizationInfo(context);

        result.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task GetAuthorizationInfo_CaseVariedDuplicateApiKey_DelegatesToInner()
    {
        var fixture = CreateFixture();
        var context = CreateContext(
            $"/Videos/{ItemId:N}/master.m3u8",
            $"?api_key={Token}&API_KEY={Token}&mediaSourceId={MediaSourceId}");

        var result = await fixture.Subject.GetAuthorizationInfo(context);

        result.Should().BeSameAs(fixture.Fallback);
    }

    [Fact]
    public async Task GetAuthorizationInfo_DuplicateDeviceId_UsesFingerprintFallback()
    {
        var fixture = CreateFixture();
        var context = CreateContext(
            $"/Videos/{ItemId:N}/master.m3u8",
            $"?api_key={Token}&mediaSourceId={MediaSourceId}&deviceId=one&deviceId=two");

        var result = await fixture.Subject.GetAuthorizationInfo(context);

        result.DeviceId.Should().MatchRegex("^StreamGenerator-[0-9a-f]{12}$");
        result.DeviceId.Should().NotContain(Token);
    }

    [Fact]
    public async Task GetAuthorizationInfo_ExpiredToken_DelegatesToInner()
    {
        var fixture = CreateFixture(expired: true);
        var context = CreateContext(
            $"/Videos/{ItemId:N}/master.m3u8",
            $"?api_key={Token}&mediaSourceId={MediaSourceId}");

        var result = await fixture.Subject.GetAuthorizationInfo(context);

        result.Should().BeSameAs(fixture.Fallback);
    }

    private static Fixture CreateFixture(bool expired = false, params string[] mediaSourceIds)
    {
        if (mediaSourceIds.Length == 0)
        {
            mediaSourceIds = [MediaSourceId];
        }

        var user = new User("test", "auth", "reset");
        var item = new Mock<BaseItem>().Object;
        var fallback = new AuthorizationInfo();
        var inner = new Mock<IAuthorizationContext>();
        inner.Setup(x => x.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(fallback);
        inner.Setup(x => x.GetAuthorizationInfo(It.IsAny<HttpRequest>())).ReturnsAsync(fallback);

        var userManager = new Mock<IUserManager>();
        userManager.Setup(x => x.GetUserById(user.Id)).Returns(user);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(x => x.GetItemById<BaseItem>(ItemId, user)).Returns(item);

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(x => x.GetStaticMediaSources(item, false, user))
            .Returns(mediaSourceIds.Select(id => new MediaSourceInfo { Id = id }).ToArray());

        var configuration = new PluginConfiguration();
        configuration.StreamTokens[Token] = new StreamTokenInformation
        {
            UserId = user.Id,
            ItemId = ItemId.ToString("N"),
            CreatedAt = expired ? DateTimeOffset.UtcNow.AddHours(-2) : DateTimeOffset.UtcNow,
            Duration = expired ? TimeSpan.FromHours(1) : null,
        };

        var accessor = new Mock<IStreamGeneratorConfigurationAccessor>();
        accessor.SetupGet(x => x.Configuration).Returns(configuration);

        var subject = new CustomStreamTokensAuthorizationContext(
            inner.Object,
            userManager.Object,
            libraryManager.Object,
            mediaSourceManager.Object,
            accessor.Object,
            NullLogger<CustomStreamTokensAuthorizationContext>.Instance);

        return new Fixture(subject, inner, user, fallback);
    }

    private static DefaultHttpContext CreateContext(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    private sealed record Fixture(
        CustomStreamTokensAuthorizationContext Subject,
        Mock<IAuthorizationContext> Inner,
        User User,
        AuthorizationInfo Fallback);
}
