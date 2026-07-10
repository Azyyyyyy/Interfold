namespace Interfold.Contracts.Ids;

/// <summary>
/// Discriminated union of the three OAuth provider identity shapes returned by
/// <c>OAuthControllerBase.ExtractProviderIdentityAsync</c>. Exactly one of
/// <see cref="Discord"/> / <see cref="Google"/> / <see cref="Apple"/> is populated on a
/// successful extract; construction is only via the three static factories, which enforce
/// the invariant at compile time.
///
/// <para>
/// Round-2 Commit 7 introduced this to collapse three duplicated
/// <c>oauthProvider switch { Discord =&gt; new DiscordId(id), Google =&gt; new Email(id),
/// Apple =&gt; new AppleId(id) }</c> re-wrap sites (<c>AuthController.Callback</c>,
/// <c>AuthLinkController.Callback</c>, and <c>AuthLinkController.RedirectWithSocketEventAsync</c>)
/// that each independently held a raw <c>string identity</c> local across a switch/wrap
/// boundary and each rewrapped the same value in three different ways. The union carries
/// the typed identity from the extract site (where the OAuth service returns
/// <c>DiscordId?</c> / <c>Email?</c> / <c>AppleId?</c> already) all the way to the three
/// dispatch sites without ever dropping back to <c>string</c>. The three
/// <c>ExchangeCodeFor*</c> service methods already return the typed nullable, so this
/// union is the last mile: it removes the last unwrap-and-rewrap in the OAuth callback
/// pipeline.
/// </para>
///
/// <para>
/// Callers dispatch via property-pattern match:
/// <code>
/// identity switch
/// {
///     { Discord: { } discordId } =&gt; ...use discordId...,
///     { Google: { } email }      =&gt; ...use email...,
///     { Apple: { } appleId }     =&gt; ...use appleId...,
///     _ =&gt; ...(unreachable given caller-side presence guard)...
/// }
/// </code>
/// A caller that receives <c>null</c> from <c>ExtractProviderIdentityAsync</c> treats it
/// as "identity absent" (403 in both controllers today); the union itself is never
/// constructed in a fully-empty state through the public API.
/// </para>
/// </summary>
public readonly record struct ProviderIdentity
{
    public DiscordId? Discord { get; }
    public Email? Google { get; }
    public AppleId? Apple { get; }

    private ProviderIdentity(DiscordId? discord, Email? google, AppleId? apple)
    {
        Discord = discord;
        Google = google;
        Apple = apple;
    }

    public static ProviderIdentity FromDiscord(DiscordId id) => new(id, null, null);
    public static ProviderIdentity FromGoogle(Email id) => new(null, id, null);
    public static ProviderIdentity FromApple(AppleId id) => new(null, null, id);
}
