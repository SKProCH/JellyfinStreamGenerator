using Jellyfin.Plugin.StreamGenerator.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.StreamGenerator.Controllers;

[ApiController]
[Route("StreamGenerator")]
public class StreamTokenController(
    IAuthorizationContext authorizationContext,
    ILibraryManager libraryManager,
    IUserManager userManager,
    IStreamGeneratorConfigurationAccessor configurationAccessor)
    : ControllerBase
{
    [HttpGet("Settings")]
    [Authorize]
    public ActionResult<PluginSettings> GetSettings()
    {
        var config = configurationAccessor.Configuration;
        if (config is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        return Ok(new PluginSettings
        {
            GenerateCustomApiTokens = config.GenerateCustomApiTokens,
            RememberPlaybackProgressByDefault = config.RememberPlaybackProgressByDefault,
            DefaultTokenDurationHours = config.DefaultCustomTokenDurationHours,
            MaxTokenDurationHours = config.MaxCustomTokenDurationHours
        });
    }

    [HttpGet("Tokens")]
    [Authorize(Roles = "Administrator")]
    public ActionResult<IEnumerable<StreamTokenDto>> GetTokens()
    {
        var config = configurationAccessor.Configuration;
        if (config is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var result = new List<StreamTokenDto>();

        foreach (var (tokenStr, tokenInfo) in config.StreamTokens)
        {
            var user = userManager.GetUserById(tokenInfo.UserId);
            var itemName = "Unknown Item";

            if (Guid.TryParse(tokenInfo.ItemId, out var itemId))
            {
                var item = libraryManager.GetItemById(itemId);
                if (item is not null)
                {
                    itemName = item.Name;
                }
            }

            result.Add(new StreamTokenDto
            {
                Token = tokenStr,
                ItemId = tokenInfo.ItemId,
                ItemName = itemName,
                UserId = tokenInfo.UserId,
                UserName = user?.Username ?? "Unknown User",
                CreatedAt = tokenInfo.CreatedAt,
                ExpiresAt = tokenInfo.CreatedAt + tokenInfo.Duration,
                IsExpired = tokenInfo.IsExpired(),
                RememberPlaybackProgress = tokenInfo.RememberPlaybackProgress,
            });
        }

        return Ok(result.OrderByDescending(x => x.CreatedAt));
    }

    [HttpDelete("Tokens")]
    [Authorize(Roles = "Administrator")]
    public ActionResult RevokeTokensBulk([FromQuery] Guid? userId)
    {
        var config = configurationAccessor.Configuration;
        if (config is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var tokens = config.StreamTokens;
        var oldCount = tokens.Count;

        if (userId.HasValue)
        {
            var keysToRemove = tokens
                .Where(kv => kv.Value.UserId == userId.Value)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in keysToRemove)
            {
                tokens.Remove(key);
            }
        }
        else if (oldCount > 0)
        {
            tokens.Clear();
        }

        if (tokens.Count != oldCount)
        {
            configurationAccessor.Save();
        }

        return NoContent();
    }

    [HttpDelete("Tokens/{token}")]
    [Authorize(Roles = "Administrator")]
    public ActionResult RevokeToken(string token)
    {
        var config = configurationAccessor.Configuration;
        if (config is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (config.StreamTokens.Remove(token))
        {
            configurationAccessor.Save();
        }

        return NoContent();
    }

    [HttpPost("GenerateToken")]
    [Authorize]
    public async Task<ActionResult<string>> GenerateToken(
        [FromQuery] string itemId,
        [FromQuery] double? durationHours = null,
        [FromQuery] bool? rememberPlaybackProgress = null)
    {
        var config = configurationAccessor.Configuration;
        if (config is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var authInfo = await authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);

        var finalDurationHours = durationHours ?? config.DefaultCustomTokenDurationHours;

        if (config.MaxCustomTokenDurationHours.HasValue)
        {
            if (!finalDurationHours.HasValue || finalDurationHours.Value > config.MaxCustomTokenDurationHours.Value)
            {
                finalDurationHours = config.MaxCustomTokenDurationHours.Value;
            }
        }

        var token = Guid.NewGuid().ToString("n");
        config.StreamTokens[token] = new StreamTokenInformation
        {
            UserId = authInfo.UserId,
            ItemId = itemId,
            Duration = finalDurationHours.HasValue ? TimeSpan.FromHours(finalDurationHours.Value) : null,
            RememberPlaybackProgress = rememberPlaybackProgress ?? config.RememberPlaybackProgressByDefault,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        configurationAccessor.Save();

        return Ok(token);
    }
}

public sealed class PluginSettings
{
    public bool GenerateCustomApiTokens { get; set; }
    public bool RememberPlaybackProgressByDefault { get; set; }
    public double? DefaultTokenDurationHours { get; set; }
    public double? MaxTokenDurationHours { get; set; }
}

public sealed class StreamTokenDto
{
    public required string Token { get; set; }
    public required string ItemId { get; set; }
    public string? ItemName { get; set; }
    public required Guid UserId { get; set; }
    public string? UserName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsExpired { get; set; }
    public bool RememberPlaybackProgress { get; set; }
}
