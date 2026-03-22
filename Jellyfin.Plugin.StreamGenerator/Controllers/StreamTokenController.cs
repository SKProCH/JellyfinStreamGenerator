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
    IUserManager userManager)
    : ControllerBase
{
    private static readonly TimeSpan DefaultTokenDuration = TimeSpan.FromHours(24);

    [HttpGet("Settings")]
    [Authorize]
    public ActionResult<PluginSettings> GetSettings()
    {
        var plugin = StreamGeneratorPlugin.Instance;
        if (plugin is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        return Ok(new PluginSettings
        {
            GenerateCustomApiTokens = plugin.Configuration.GenerateCustomApiTokens
        });
    }

    [HttpGet("Tokens")]
    [Authorize(Roles = "Administrator")]
    public ActionResult<IEnumerable<StreamTokenDto>> GetTokens()
    {
        var plugin = StreamGeneratorPlugin.Instance;
        if (plugin is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var result = new List<StreamTokenDto>();

        foreach (var (tokenStr, tokenInfo) in plugin.Configuration.StreamTokens)
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
                IsExpired = tokenInfo.IsExpired()
            });
        }

        return Ok(result.OrderByDescending(x => x.CreatedAt));
    }

    [HttpDelete("Tokens/{token}")]
    [Authorize(Roles = "Administrator")]
    public ActionResult RevokeToken(string token)
    {
        var plugin = StreamGeneratorPlugin.Instance;
        if (plugin is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (plugin.Configuration.StreamTokens.Remove(token))
        {
            plugin.SaveConfiguration();
        }

        return NoContent();
    }

    [HttpPost("GenerateToken")]
    [Authorize]
    public async Task<ActionResult<string>> GenerateToken([FromQuery] string itemId)
    {
        var plugin = StreamGeneratorPlugin.Instance;
        if (plugin is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var authInfo = await authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);

        var token = Guid.NewGuid().ToString("n");
        plugin.Configuration.StreamTokens[token] = new StreamTokenInformation
        {
            UserId = authInfo.UserId,
            ItemId = itemId,
            Duration = DefaultTokenDuration,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        plugin.SaveConfiguration();

        return Ok(token);
    }
}

public sealed class PluginSettings
{
    public bool GenerateCustomApiTokens { get; set; }
}

public sealed class StreamTokenDto
{
    public required string Token { get; set; }
    public required string ItemId { get; set; }
    public string? ItemName { get; set; }
    public required Guid UserId { get; set; }
    public string? UserName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsExpired { get; set; }
}

