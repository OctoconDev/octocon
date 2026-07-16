using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Commands;

// Persisted + idempotency-hashed payload; Username's raw-string converter keeps stored
// hashes valid.
public sealed record UpdateUsernameCommand(Username Username);

public sealed record UnlinkDiscordCommand();

public sealed record UnlinkEmailCommand();

public sealed record UnlinkAppleCommand();

public sealed record DeleteAccountCommand();

public sealed record WipeAltersCommand();

public sealed record WipeTagsCommand();
