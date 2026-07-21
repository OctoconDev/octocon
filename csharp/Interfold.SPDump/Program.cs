using System.Security.Cryptography;
using Interfold.Api.Services;
using Interfold.Api.Services.Http;
using Interfold.Api.SimplyPlural;
using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// --- Exit codes ---
const int ExitSuccess = 0;
const int ExitImportFailed = 2;
const int ExitUnhandled = 3;

var options = SpDumpOptions.FromArgs(args);

var services = new ServiceCollection();

// Basic logging
services.AddLogging(builder => builder.AddConsole());
services.AddSingleton<IConfiguration>(new ConfigurationManager());

// TimeProvider — SimplyPluralImportService (registered below via AddSimplyPluralImportCore)
// takes it via constructor injection so its "used import time as created date" fallback
// paths can be time-frozen in tests.
services.AddSingleton(TimeProvider.System);

// Register InMemory persistence support
InMemoryServiceCollectionExtensions.Register();
services.AddInterfoldPersistence(PersistenceMode.InMemory, cfg =>
{
	cfg.ScyllaKeyspace = ScyllaKeyspace.Nam;
});

// Domain handlers (some repositories expect handlers registered)
services.AddInterfoldDomainHandlers();

// Register a minimal avatar storage that writes to a temp folder
services.AddSingleton<IAvatarStorage, TempAvatarStorage>();

services.AddTransient<HttpLoggingHandler>();

// HttpClient used by SimplyPluralImportService (wired here because this utility owns
// its own HttpClient shape - see AddSimplyPluralImportCore comment).
services.AddHttpClient(HttpClientNames.SimplyPlural).AddHttpMessageHandler<HttpLoggingHandler>();

// Register the import service itself (it will resolve repositories from InMemory registration).
// Uses the -Core overload so we skip the async IImportJobRunner queue consumer that the
// full API extension registers - this utility drives ImportAsync directly.
services.AddSimplyPluralImportCore();

var provider = services.BuildServiceProvider();

using var scope = provider.CreateScope();
var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SPDump");

// Determine encryption key to use
RecoveryCode recoveryKey;
if (options.RecoveryKey is { } providedKey)
{
	recoveryKey = providedKey;
}
else
{
	// Generate a random 32-byte key and base64 encode
	var key = new byte[32];
	RandomNumberGenerator.Fill(key);
	var encryptionKeyBase64 = Convert.ToBase64String(key);
	recoveryKey = new(encryptionKeyBase64);
	logger.LogInformation("Using generated random encryption key (base64): {KeyPreview}", encryptionKeyBase64[..8] + "...");
}

try
{
	var importService = scope.ServiceProvider.GetRequiredService<ISimplyPluralImportService>();
	importService.WaitForAvatars = true;

	var result = await importService.ImportAsync(SpDumpOptions.DumpSystemId, options.Token, recoveryKey);
	if (result.Success)
	{
		logger.LogInformation("Import succeeded. Alters imported: {Count}", result.AlterCount);
		return ExitSuccess;
	}

	logger.LogError("Import failed: {Code} {Reason}", result.ErrorCode?.ToWire(), result.ErrorMessage);
	return ExitImportFailed;
}
catch (Exception ex)
{
	logger.LogError(ex, "Unhandled exception during SP import");
	return ExitUnhandled;
}

/// <summary>
/// The dump utility's parsed command line: <c>SPDump &lt;sp-token&gt; [recovery-key-base64]</c>.
/// Wraps both values in their Contracts ID types at the boundary so the rest of the program
/// never carries raw strings.
/// </summary>
internal readonly record struct SpDumpOptions(ImportToken Token, RecoveryCode? RecoveryKey)
{
	/// <summary>
	/// Placeholder system the import runs against. The InMemory backend keys its stores by
	/// whatever SystemId it is handed, so an empty ID is accepted and keeps the utility free
	/// of any real-system coupling.
	/// </summary>
	public static readonly SystemId DumpSystemId = new("");

	public static SpDumpOptions FromArgs(string[] args)
	{
		string spToken = "";
		if (args.Length >= 1)
		{
			spToken = args[0];
		}
		else
		{
			while (string.IsNullOrWhiteSpace(spToken))
			{
				Console.Write("Please put in your SP token:");
				spToken = Console.ReadLine()!;
			}
		}

		// A blank second argument counts as "no key provided" (a random one is generated),
		// matching the utility's historical behaviour.
		RecoveryCode? recoveryKey = args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1])
			? new RecoveryCode(args[1])
			: null;
		return new(new(spToken), recoveryKey);
	}
}

// Minimal temp avatar storage used by the dump utility
internal sealed class TempAvatarStorage : IAvatarStorage
{
	public Task<AvatarUrl> SaveSystemAvatarAsync(SystemId systemId, Stream stream, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(new AvatarUrl(""));
	}

	public Task<AvatarUrl> SaveAlterAvatarAsync(SystemId systemId, AlterId alterId, Stream stream, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(new AvatarUrl(""));
	}

	public Task<bool> DeleteByUrlAsync(AvatarUrl? avatarUrl, CancellationToken cancellationToken = default)
	{
		// no-op for the utility
		return Task.FromResult(false);
	}
}
