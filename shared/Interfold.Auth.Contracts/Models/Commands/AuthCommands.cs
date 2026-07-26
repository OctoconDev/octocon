using Interfold.Auth.Contracts.Ids;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Auth.Contracts.Models.Commands;

public sealed record AuthenticateOAuthCommand(ProviderIdentity Identity);

public sealed record LinkOAuthIdentityCommand(LinkToken LinkToken, ProviderIdentity Identity);

public sealed record RecordAuthTokenCommand(Jti Jti, SystemId SystemId, DateTimeOffset ExpiresAt);

public sealed record RevokeAuthTokenCommand(Jti Jti);
