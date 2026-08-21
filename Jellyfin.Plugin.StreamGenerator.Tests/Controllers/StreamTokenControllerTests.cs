using Jellyfin.Plugin.StreamGenerator.Configuration;
using Jellyfin.Plugin.StreamGenerator.Controllers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Jellyfin.Plugin.StreamGenerator.Tests.Controllers;

public class StreamTokenControllerTests
{
    [Fact]
    public void GetSettings_ReturnsConfiguredValues()
    {
        var configuration = new PluginConfiguration
        {
            GenerateCustomApiTokens = false,
            RememberPlaybackProgressByDefault = false,
            DefaultCustomTokenDurationHours = 12,
            MaxCustomTokenDurationHours = 24,
        };
        var fixture = CreateFixture(configuration);

        var result = fixture.Subject.GetSettings().Result.Should().BeOfType<OkObjectResult>().Subject;
        var settings = result.Value.Should().BeOfType<PluginSettings>().Subject;

        settings.GenerateCustomApiTokens.Should().BeFalse();
        settings.RememberPlaybackProgressByDefault.Should().BeFalse();
        settings.DefaultTokenDurationHours.Should().Be(12);
        settings.MaxTokenDurationHours.Should().Be(24);
    }

    [Fact]
    public void GetSettings_WithoutConfiguration_ReturnsServiceUnavailable()
    {
        var fixture = CreateFixture(null);

        fixture.Subject.GetSettings().Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task GenerateToken_UsesAuthenticatedUserClampsDurationAndSaves()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var configuration = new PluginConfiguration { MaxCustomTokenDurationHours = 24 };
        var fixture = CreateFixture(configuration, userId);

        var action = await fixture.Subject.GenerateToken(itemId.ToString("N"), 72);
        var result = action.Result.Should().BeOfType<OkObjectResult>().Subject;
        var token = result.Value.Should().BeOfType<string>().Subject;

        token.Should().MatchRegex("^[0-9a-f]{32}$");
        configuration.StreamTokens.Should().ContainKey(token);
        configuration.StreamTokens[token].UserId.Should().Be(userId);
        configuration.StreamTokens[token].ItemId.Should().Be(itemId.ToString("N"));
        configuration.StreamTokens[token].Duration.Should().Be(TimeSpan.FromHours(24));
        configuration.StreamTokens[token].RememberPlaybackProgress.Should().BeTrue();
        fixture.Accessor.Verify(x => x.Save(), Times.Once);
    }

    [Fact]
    public async Task GenerateToken_ExplicitProgressSettingOverridesDefault()
    {
        var configuration = new PluginConfiguration { RememberPlaybackProgressByDefault = true };
        var fixture = CreateFixture(configuration, Guid.NewGuid());

        var action = await fixture.Subject.GenerateToken(Guid.NewGuid().ToString("N"), rememberPlaybackProgress: false);
        var token = action.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<string>().Subject;

        configuration.StreamTokens[token].RememberPlaybackProgress.Should().BeFalse();
    }

    [Fact]
    public void RevokeToken_ExistingToken_RemovesAndSaves()
    {
        var configuration = new PluginConfiguration();
        configuration.StreamTokens["token"] = CreateToken(Guid.NewGuid());
        var fixture = CreateFixture(configuration);

        fixture.Subject.RevokeToken("token").Should().BeOfType<NoContentResult>();

        configuration.StreamTokens.Should().BeEmpty();
        fixture.Accessor.Verify(x => x.Save(), Times.Once);
    }

    [Fact]
    public void RevokeToken_MissingToken_DoesNotSave()
    {
        var fixture = CreateFixture(new PluginConfiguration());

        fixture.Subject.RevokeToken("missing").Should().BeOfType<NoContentResult>();

        fixture.Accessor.Verify(x => x.Save(), Times.Never);
    }

    [Fact]
    public void RevokeTokensBulk_RemovesOnlyRequestedUsersTokens()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var configuration = new PluginConfiguration();
        configuration.StreamTokens["matching"] = CreateToken(userId);
        configuration.StreamTokens["other"] = CreateToken(otherUserId);
        var fixture = CreateFixture(configuration);

        fixture.Subject.RevokeTokensBulk(userId).Should().BeOfType<NoContentResult>();

        configuration.StreamTokens.Keys.Should().Equal("other");
        fixture.Accessor.Verify(x => x.Save(), Times.Once);
    }

    [Fact]
    public void GetTokens_ReturnsNewestFirstAndExpiration()
    {
        var configuration = new PluginConfiguration();
        var old = CreateToken(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-2), TimeSpan.FromHours(1));
        var recent = CreateToken(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-1), null);
        configuration.StreamTokens["old"] = old;
        configuration.StreamTokens["recent"] = recent;
        var fixture = CreateFixture(configuration);

        var result = fixture.Subject.GetTokens().Result.Should().BeOfType<OkObjectResult>().Subject;
        var tokens = result.Value.Should().BeAssignableTo<IEnumerable<StreamTokenDto>>().Subject.ToArray();

        tokens.Select(x => x.Token).Should().Equal("recent", "old");
        tokens[0].ExpiresAt.Should().BeNull();
        tokens[1].ExpiresAt.Should().Be(old.CreatedAt + old.Duration);
    }

    private static Fixture CreateFixture(PluginConfiguration? configuration, Guid? userId = null)
    {
        var authorizationContext = new Mock<IAuthorizationContext>();
        authorizationContext.Setup(x => x.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo
            {
                User = userId.HasValue ? new Jellyfin.Database.Implementations.Entities.User("test", "auth", "reset") : null,
            });
        if (userId.HasValue)
        {
            var info = authorizationContext.Object.GetAuthorizationInfo(new DefaultHttpContext()).GetAwaiter().GetResult();
            typeof(Jellyfin.Database.Implementations.Entities.User).GetProperty("Id")!.SetValue(info.User, userId.Value);
            authorizationContext.Setup(x => x.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(info);
        }

        var accessor = new Mock<IStreamGeneratorConfigurationAccessor>();
        accessor.SetupGet(x => x.Configuration).Returns(configuration);
        var subject = new StreamTokenController(
            authorizationContext.Object,
            new Mock<ILibraryManager>().Object,
            new Mock<IUserManager>().Object,
            accessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return new Fixture(subject, accessor);
    }

    private static StreamTokenInformation CreateToken(
        Guid userId,
        DateTimeOffset? createdAt = null,
        TimeSpan? duration = null)
        => new()
        {
            UserId = userId,
            ItemId = Guid.NewGuid().ToString("N"),
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            Duration = duration,
        };

    private sealed record Fixture(
        StreamTokenController Subject,
        Mock<IStreamGeneratorConfigurationAccessor> Accessor);
}
