using Jellyfin.Plugin.StreamGenerator.Configuration;

namespace Jellyfin.Plugin.StreamGenerator.Tests.Configuration;

public class ConfigurationTests
{
    [Fact]
    public void PluginConfiguration_HasSafeDefaults()
    {
        var configuration = new PluginConfiguration();

        configuration.GenerateCustomApiTokens.Should().BeTrue();
        configuration.RememberPlaybackProgressByDefault.Should().BeTrue();
        configuration.DefaultCustomTokenDurationHours.Should().BeNull();
        configuration.MaxCustomTokenDurationHours.Should().BeNull();
        configuration.StreamTokens.Should().BeEmpty();
    }

    [Fact]
    public void StreamToken_WithoutDuration_DoesNotExpire()
    {
        var token = CreateToken(null, DateTimeOffset.UtcNow.AddYears(-1));

        token.IsExpired().Should().BeFalse();
    }

    [Fact]
    public void StreamToken_AfterDuration_Expires()
    {
        var token = CreateToken(TimeSpan.FromHours(1), DateTimeOffset.UtcNow.AddHours(-2));

        token.IsExpired().Should().BeTrue();
    }

    [Fact]
    public void StreamToken_BeforeDuration_DoesNotExpire()
    {
        var token = CreateToken(TimeSpan.FromHours(2), DateTimeOffset.UtcNow.AddHours(-1));

        token.IsExpired().Should().BeFalse();
    }

    private static StreamTokenInformation CreateToken(TimeSpan? duration, DateTimeOffset createdAt)
        => new()
        {
            UserId = Guid.NewGuid(),
            ItemId = Guid.NewGuid().ToString("N"),
            Duration = duration,
            CreatedAt = createdAt,
        };
}
