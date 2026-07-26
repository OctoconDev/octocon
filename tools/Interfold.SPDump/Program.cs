using System.Security.Cryptography;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory;
using Interfold.Settings.Api.Services.Http;
using Interfold.Settings.Api.SimplyPlural;
using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Domain.Abstractions;
using Interfold.Shared.Api.Services;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.SPDump;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

const int ExitSuccess = 0;
const int ExitImportFailed = 2;
const int ExitUnhandled = 3;

var options = SpDumpOptions.FromArgs(args);

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole());
services.AddSingleton<IConfiguration>(new ConfigurationManager());
services.AddSingleton(TimeProvider.System);

InMemoryServiceCollectionExtensions.Register();
services.AddInterfoldPersistence(PersistenceMode.InMemory, cfg =>
{
	cfg.ScyllaKeyspace = ScyllaKeyspace.Nam;
});

services.AddInterfoldDomainHandlers();
services.AddSingleton<IAvatarStorage, TempAvatarStorage>();

services.AddTransient<HttpLoggingHandler>();
services.AddHttpClient(HttpClientNames.SimplyPlural).AddHttpMessageHandler<HttpLoggingHandler>();

// -Core overload skips the async IImportJobRunner queue consumer; this utility drives
// ImportAsync directly.
services.AddSimplyPluralImportCore();

var provider = services.BuildServiceProvider();

using var scope = provider.CreateScope();
var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SPDump");

RecoveryCode recoveryKey;
if (options.RecoveryKey is { } providedKey)
{
	recoveryKey = providedKey;
}
else
{
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

namespace Interfold.SPDump
{
	/// <summary>Parsed CLI: <c>SPDump &lt;sp-token&gt; [recovery-key-base64]</c>. Both values are
	/// wrapped in Contracts ID types at the boundary.</summary>
	internal readonly record struct SpDumpOptions(ImportToken Token, RecoveryCode? RecoveryKey)
	{
		/// <summary>Placeholder system id — the InMemory backend keys stores by whatever id it
		/// is handed, so an empty id keeps the utility free of real-system coupling.</summary>
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

			// Blank second arg == "no key provided" (a random one is generated).
			RecoveryCode? recoveryKey = args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1])
				? new RecoveryCode(args[1])
				: null;
			return new(new(spToken), recoveryKey);
		}
	}

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
			=> Task.FromResult(false);
	}
}