namespace Interfold.Contracts.Ids;

/// <summary>
/// Discriminated union of the three OAuth provider identity shapes returned by
/// <c>OAuthControllerBase.ExtractProviderIdentityAsync</c>. Exactly one of
/// <see cref="Discord"/> / <see cref="Google"/> / <see cref="Apple"/> is populated on a
/// successful extract; construction is only via the three static factories, which enforce
/// the invariant at compile time. Carries the typed identity end-to-end so the OAuth
/// callback pipeline never falls back to <c>string</c>.
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

    public T MatchOrThrow<T>(Func<DiscordId, T> onDiscord, Func<Email, T> onGoogle, Func<AppleId, T> onApple)
        => this switch
        {
            { Discord: { } id } => onDiscord(id),
            { Google: { } email } => onGoogle(email),
            { Apple: { } appleId } => onApple(appleId),
            _ => throw new ArgumentOutOfRangeException(nameof(ProviderIdentity), this, "ProviderIdentity has no Discord/Google/Apple member populated.")
        };
}
