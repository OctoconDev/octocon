using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Commands;

public sealed record AuthenticateOAuthCommand(ProviderIdentity Identity);

public sealed record LinkOAuthIdentityCommand(LinkToken LinkToken, ProviderIdentity Identity);

public sealed record RecordAuthTokenCommand(Jti Jti, SystemId SystemId, DateTimeOffset ExpiresAt);

public sealed record RevokeAuthTokenCommand(Jti Jti);
