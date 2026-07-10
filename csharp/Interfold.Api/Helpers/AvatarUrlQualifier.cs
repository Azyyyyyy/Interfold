using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Api.Helpers;

internal static class AvatarUrlQualifier
{
    /// <summary>
    /// Returns <paramref name="url"/> with the server origin prepended when the stored
    /// value is a relative path. Already-absolute URLs are returned unchanged.
    /// </summary>
    /// <remarks>
    /// Avatar paths route through <see cref="QualifyAvatar(AvatarUrl?, AvatarSource?, string, HostString)"/>
    /// (or the origin overload) so they can use the persisted <c>avatar_source</c> as the
    /// authoritative discriminator. This raw helper is retained for non-avatar callers
    /// (e.g. internal utilities) that don't have a source flag to inspect.
    /// </remarks>
    internal static string? Qualify(string? url, string scheme, HostString host)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        var origin = $"{scheme}://{host}";
        return url.StartsWith('/') ? $"{origin}{url}" : $"{origin}/{url}";
    }

    /// <summary>
    /// Overload for callers that already hold a pre-built origin string
    /// (e.g. <c>"https://api.example.com"</c>).
    /// </summary>
    internal static string? Qualify(string? url, string? origin)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        if (string.IsNullOrWhiteSpace(origin))
            return url;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        return url.StartsWith('/') ? $"{origin}{url}" : $"{origin}/{url}";
    }

    /// <summary>
    /// Source-aware avatar qualification. The persisted <see cref="AvatarSource"/> is the
    /// single source of truth: <see cref="AvatarSource.Local"/> URLs get the server origin
    /// prepended, <see cref="AvatarSource.External"/> URLs are returned verbatim, and a
    /// null / blank input is passed through unchanged.
    ///
    /// <para>
    /// Round-2 Commit 10 (canvas #10): the untyped
    /// <c>QualifyAvatar(string? url, AvatarSource?, string, HostString)</c> and
    /// <c>QualifyAvatar(string? url, AvatarSource?, string?)</c> overloads that this typed
    /// overload used to delegate to have been deleted. Every consumer now routes through
    /// the typed wrapper (Commit 2's <c>AvatarStorage</c> retype promoted every read model
    /// and command payload to <see cref="AvatarUrl"/>?). Inlining the raw-string
    /// discrimination into the typed body drops the wrap/unwrap round-trip and eliminates
    /// the "which overload did the compiler pick?" footgun.
    /// </para>
    /// </summary>
    internal static AvatarUrl? QualifyAvatar(AvatarUrl? url, AvatarSource? source, string scheme, HostString host)
    {
        if (url is not { } present || string.IsNullOrWhiteSpace(present.Value))
            return url;

        return source == AvatarSource.Local
            ? AvatarUrl.FromNullable(Qualify(present.Value, scheme, host))
            : url;
    }

    /// <summary>
    /// Origin-string overload of <see cref="QualifyAvatar(AvatarUrl?, AvatarSource?, string, HostString)"/>
    /// for socket handlers and other callers that already hold a pre-built origin string.
    /// </summary>
    internal static AvatarUrl? QualifyAvatar(AvatarUrl? url, AvatarSource? source, string? origin)
    {
        if (url is not { } present || string.IsNullOrWhiteSpace(present.Value))
            return url;

        return source == AvatarSource.Local
            ? AvatarUrl.FromNullable(Qualify(present.Value, origin))
            : url;
    }
}
