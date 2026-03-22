using System.Text.RegularExpressions;
using Jellyfin.Plugin.StreamGenerator.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamGenerator;

public partial class CustomStreamTokensAuthorizationContext(
    IAuthorizationContext inner,
    IUserManager userManager,
    ILogger<CustomStreamTokensAuthorizationContext> logger)
    : IAuthorizationContext
{
    public Task<AuthorizationInfo> GetAuthorizationInfo(HttpContext requestContext)
        => TryAuthorizeByToken(requestContext.Request)
           ?? inner.GetAuthorizationInfo(requestContext);

    public Task<AuthorizationInfo> GetAuthorizationInfo(HttpRequest requestContext)
        => TryAuthorizeByToken(requestContext)
           ?? inner.GetAuthorizationInfo(requestContext);

    private Task<AuthorizationInfo>? TryAuthorizeByToken(HttpRequest request)
    {
        var config = StreamGeneratorPlugin.Instance?.Configuration;
        if (config is null) return null;

        if (!config.GenerateCustomApiTokens) return null;

        if (!request.Query.TryGetValue("api_key", out var apiKey))
            return null;

        if (!config.StreamTokens.TryGetValue(apiKey.ToString(), out var token))
            return null;

        if (token.IsExpired())
        {
            logger.LogWarning("Token {ApiKey} for user {UserId} is expired", apiKey, token.UserId);
            return null;
        }

        var videoId = ValidateUrlAndExtractToken(request);
        if (videoId is null)
        {
            logger.LogWarning("StreamGenerator token is valid, but failed to validate and/or extract video ID from request path: {RequestPath} " +
                              "If this is happened to you, please report it to https://github.com/SKProCH/JellyfinStreamGenerator/issues", request.Path);
            return null;
        }

        if (token.ItemId != videoId)
            return null;

        var user = userManager.GetUserById(token.UserId);
        if (user is null) return null;

        return Task.FromResult(new AuthorizationInfo
        {
            IsAuthenticated = true,
            User = user,
            Token = apiKey!,
            IsApiKey = true,
            DeviceId = $"StreamGenerator-{apiKey}",
            Device = "StreamGenerator",
            Client = "StreamGenerator",
        });
    }

    private static string? ValidateUrlAndExtractToken(HttpRequest request)
    {
        if (request.Path.Value is not { } path ||
            !path.Contains("/videos/", StringComparison.InvariantCultureIgnoreCase))
        {
            return null;
        }

        if (IsMatch(VideosMainMasterRegex, out var videoId))
            return videoId;

        if (IsMatch(VideosTsRegex, out videoId))
            return videoId;

        return null;

        bool IsMatch(Regex regex, out string videoIdInternal)
        {
            videoIdInternal = string.Empty;
            var match = regex.Match(path);
            if (!match.Success) return false;
            videoIdInternal = match.Groups[1].Value;
            return true;
        }
    }

    [GeneratedRegex("^/Videos/([0-9a-f]{32})/(?:[a-zA-Z0-9_-]+/)*[a-zA-Z0-9_-]+\\.m3u8$", RegexOptions.IgnoreCase)]
    private static partial Regex VideosMainMasterRegex { get; }

    [GeneratedRegex("^/Videos/([0-9a-f]{32})/(?:[a-zA-Z0-9_-]+/)*[a-zA-Z0-9_-]+\\.ts$", RegexOptions.IgnoreCase)]
    private static partial Regex VideosTsRegex { get; }
}