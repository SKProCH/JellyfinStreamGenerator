using Jellyfin.Plugin.StreamGenerator.Configuration;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.StreamGenerator.Controllers;

[ApiController]
[Route("StreamGenerator")]
public class StreamTokenController(IAuthorizationContext authorizationContext) : ControllerBase
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
