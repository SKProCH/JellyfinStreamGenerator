using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.StreamGenerator.Authorization;
using Jellyfin.Plugin.StreamGenerator.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamGenerator;

public class CustomStreamTokensAuthorizationContext(
    IAuthorizationContext inner,
    IUserManager userManager,
    ILibraryManager libraryManager,
    IMediaSourceManager mediaSourceManager,
    IStreamGeneratorConfigurationAccessor configurationAccessor,
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
        var config = configurationAccessor.Configuration;
        if (config is null) return null;

        if (!config.GenerateCustomApiTokens) return null;

        var hasLegacyApiKey = request.Query.TryGetValue("api_key", out var legacyApiKeyValues);
        var hasApiKey = request.Query.TryGetValue("ApiKey", out var apiKeyValues);
        if (hasLegacyApiKey == hasApiKey)
            return null;

        var tokenValues = hasApiKey ? apiKeyValues : legacyApiKeyValues;
        if (tokenValues.Count != 1 || string.IsNullOrWhiteSpace(tokenValues[0]))
            return null;

        var apiKey = tokenValues[0]!;
        if (!config.StreamTokens.TryGetValue(apiKey, out var token))
            return null;

        if (token.IsExpired())
        {
            logger.LogWarning("Stream token {TokenId} for user {UserId} is expired", GetTokenFingerprint(apiKey), token.UserId);
            return null;
        }

        if (!StreamTokenPathScope.TryParse(request, out var scope) || scope is null)
        {
            logger.LogWarning("Stream token {TokenId} cannot access path {RequestPath}", GetTokenFingerprint(apiKey), request.Path);
            return null;
        }

        if (!Guid.TryParse(token.ItemId, out var tokenItemId) || tokenItemId != scope.ItemId)
            return null;

        var user = userManager.GetUserById(token.UserId);
        if (user is null) return null;

        var item = libraryManager.GetItemById<BaseItem>(scope.ItemId, user);
        if (item is null)
            return null;

        if (scope.MediaSourceId is not null
            && !mediaSourceManager.GetStaticMediaSources(item, false, user)
                .Any(source => string.Equals(source.Id, scope.MediaSourceId, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var deviceId = request.Query.TryGetValue("deviceId", out var queryDeviceId)
                       && queryDeviceId.Count == 1
                       && !string.IsNullOrWhiteSpace(queryDeviceId[0])
            ? queryDeviceId[0]!
            : $"StreamGenerator-{GetTokenFingerprint(apiKey)}";

        return Task.FromResult(new AuthorizationInfo
        {
            IsAuthenticated = true,
            User = user,
            Token = apiKey,
            IsApiKey = true,
            DeviceId = deviceId,
            Device = "StreamGenerator",
            Client = "StreamGenerator",
        });
    }

    private static string GetTokenFingerprint(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..12].ToLowerInvariant();
}
