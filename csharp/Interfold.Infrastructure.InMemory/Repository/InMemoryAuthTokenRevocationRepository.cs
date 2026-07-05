using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

/// <summary>
/// In-memory implementation of JWT token revocation tracking for testing and development.
/// Stores tokens in a static dictionary keyed by JTI so that tokens remain accessible
/// even when the DI container is rebuilt (e.g., WebApplicationFactory host recreation
/// during parallel test execution).
/// </summary>
public sealed class InMemoryAuthTokenRevocationRepository : IAuthTokenRevocationRepository
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, TokenRecord> s_tokens = new();
    
    private sealed record TokenRecord(
        string Jti,
        string SystemId,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RevokedAt = null
    );

    public Task RecordTokenAsync(
        Jti jti,
        SystemId systemId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId.Value, nameof(systemId));

        lock (s_lock)
        {
            s_tokens[jti.Value] = new TokenRecord(
                Jti: jti.Value,
                SystemId: NormalizeSystemId(systemId),
                IssuedAt: DateTimeOffset.UtcNow,
                ExpiresAt: expiresAt,
                RevokedAt: null
            );
        }

        return Task.CompletedTask;
    }

    public Task<bool> ValidateTokenNotRevokedAsync(
        Jti jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));

        lock (s_lock)
        {
            if (!s_tokens.TryGetValue(jti.Value, out var record))
            {
                return Task.FromResult(false);
            }

            // Token is valid if: not revoked AND not expired
            var isValid = record.RevokedAt is null && record.ExpiresAt > DateTimeOffset.UtcNow;
            return Task.FromResult(isValid);
        }
    }

    public Task RevokeTokenAsync(
        Jti jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti.Value, nameof(jti));

        lock (s_lock)
        {
            if (s_tokens.TryGetValue(jti.Value, out var record) && record.RevokedAt is null)
            {
                s_tokens[jti.Value] = record with { RevokedAt = DateTimeOffset.UtcNow };
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> FindTokensBySystemIdAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId.Value, nameof(systemId));

        lock (s_lock)
        {
            var normalizedSystemId = NormalizeSystemId(systemId);

            IReadOnlyList<string> tokens = s_tokens.Values
                .Where(t => t.SystemId == normalizedSystemId
                    && t.RevokedAt is null
                    && t.ExpiresAt > DateTimeOffset.UtcNow)
                .OrderByDescending(t => t.IssuedAt)
                .Select(t => t.Jti)
                .ToList()
                .AsReadOnly();

            return Task.FromResult(tokens);
        }
    }

    private static string NormalizeSystemId(SystemId systemId) => NormalizeSystemId(systemId.Value);

    private static string NormalizeSystemId(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return systemId;

        var separator = systemId.IndexOf(':');
        if (separator <= 0 || separator >= systemId.Length - 1)
            return systemId;

        return systemId[(separator + 1)..];
    }

    public Task<int> CleanupExpiredTokensAsync(
        DateTimeOffset? olderThan = null,
        CancellationToken cancellationToken = default)
    {
        var cleanupBefore = (olderThan ?? DateTimeOffset.UtcNow).UtcDateTime;

        lock (s_lock)
        {
            var keysToRemove = s_tokens
                .Where(kvp => kvp.Value.RevokedAt is not null
                    || kvp.Value.ExpiresAt.UtcDateTime < cleanupBefore)
                .Select(kvp => kvp.Key)
                .Take(5000)
                .ToList();

            foreach (var key in keysToRemove)
            {
                s_tokens.Remove(key);
            }

            return Task.FromResult(keysToRemove.Count);
        }
    }
}
