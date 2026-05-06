using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;

namespace HumbleRedeemer;

internal sealed partial class HumbleRedeemer {
	/// <summary>
	/// Claims all Humble Vault games to the account by calling the sign URL endpoint for each
	/// unclaimed game. Already-claimed games are tracked in the bot cache to avoid redundant calls.
	/// </summary>
	private static async Task ClaimAllVaultGamesAsync(Bot bot, HumbleBundleBotCache botCache, HumbleBundleWebHandler webHandler, HumbleBundleBotConfig config) {
		if (!config.ClaimVaultGames) {
			return;
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fetching Humble Vault game list...");

		List<VaultGameInfo>? vaultGames = await webHandler.GetAllVaultGamesAsync().ConfigureAwait(false);

		if (vaultGames == null || vaultGames.Count == 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] No Vault games found");
			return;
		}

		HashSet<string> alreadyClaimed = new(botCache.ClaimedVaultGames, StringComparer.OrdinalIgnoreCase);

		int newlyClaimed = 0;
		int skipped = 0;
		bool cacheUpdated = false;

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Found {vaultGames.Count} Vault games, {alreadyClaimed.Count} already claimed");

		foreach (VaultGameInfo game in vaultGames) {
			if (alreadyClaimed.Contains(game.DownloadMachineName)) {
				skipped++;
				continue;
			}

			bool success = await webHandler.ClaimVaultGameAsync(game.DownloadMachineName, game.Filename).ConfigureAwait(false);

			if (success) {
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Claimed Vault game: {game.HumanName} ({game.GameMachineName}) [{game.Platform}]");
				botCache.ClaimedVaultGames.Add(game.DownloadMachineName);
				alreadyClaimed.Add(game.DownloadMachineName);
				newlyClaimed++;
				cacheUpdated = true;
			} else {
				ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Failed to claim Vault game: {game.HumanName} ({game.GameMachineName}) [{game.Platform}]");
			}

			// Small delay to avoid rate limiting
			await Task.Delay(100).ConfigureAwait(false);
		}

		if (cacheUpdated) {
			await botCache.SaveAsync().ConfigureAwait(false);
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Vault claiming complete: {newlyClaimed} newly claimed, {skipped} already claimed");
	}
}
