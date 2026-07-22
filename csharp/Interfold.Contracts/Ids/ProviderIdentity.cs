namespace Interfold.Contracts.Ids;

/// <summary>Discriminated union: exactly one of Discord/Google/Apple is populated. Construct
/// via the static factories; dispatch via property-pattern match or <see cref="MatchOrThrow{T}"/>.</summary>
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
