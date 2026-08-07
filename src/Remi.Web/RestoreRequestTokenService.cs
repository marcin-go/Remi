using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Remi.Web;

/// <summary>
/// Issues short-lived, one-use tokens for the browser's destructive restore request. The token
/// is sent in a custom same-origin header, which ordinary cross-site form posts cannot supply.
/// </summary>
public sealed class RestoreRequestTokenService(TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, DateTimeOffset> activeTokens = new(StringComparer.Ordinal);

    public string Issue()
    {
        RemoveExpiredTokens();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        activeTokens[token] = timeProvider.GetUtcNow().Add(Lifetime);
        return token;
    }

    public bool TryConsume(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        RemoveExpiredTokens();
        return activeTokens.TryRemove(token, out var expiresAtUtc) && expiresAtUtc > timeProvider.GetUtcNow();
    }

    private void RemoveExpiredTokens()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var token in activeTokens.Where(item => item.Value <= now))
        {
            activeTokens.TryRemove(token.Key, out _);
        }
    }
}
