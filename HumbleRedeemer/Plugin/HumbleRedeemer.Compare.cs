using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Storage;

namespace HumbleRedeemer;

internal sealed partial class HumbleRedeemer {
	private static async Task CompareHumbleBundleWithSteamLibrary(Bot bot) {
		if (!BotConfigs.ContainsKey(bot)) {
			return;
		}

		// Only run comparison once per session
		if (!BotComparisonDone.TryAdd(bot, true)) {
			return;
		}

		if (!BotHumbleTpks.TryGetValue(bot, out List<HumbleTpkInfo>? humbleTpks) || humbleTpks.Count == 0) {
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] No Humble Bundle TPK data available for comparison");
			return;
		}

		BotCountryCodes.TryGetValue(bot, out string? countryCode);
		BotConfigs.TryGetValue(bot, out HumbleBundleBotConfig? config);
		bool ignoreStoreLocation = config?.IgnoreStoreLocation ?? false;
		bool ignoreStoreLocationButRedeem = config?.IgnoreStoreLocationButRedeem ?? false;
		bool effectiveIgnoreLocation = ignoreStoreLocation || ignoreStoreLocationButRedeem;
		bool skipUnknownAppIds = config?.SkipUnknownAppIds ?? false;

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Starting Humble Bundle vs Steam library comparison...");
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Bot country: {countryCode ?? "unknown"} | Humble TPKs: {humbleTpks.Count} | Owned packages: {bot.OwnedPackages.Count}");

		// Build set of all owned Steam app IDs from OwnedPackages via GlobalDatabase
		HashSet<uint> ownedAppIds = new();

		if (ASF.GlobalDatabase != null) {
			foreach (uint packageId in bot.OwnedPackages.Keys) {
				if (ASF.GlobalDatabase.PackagesDataReadOnly.TryGetValue(packageId, out PackageData? packageData) && packageData.AppIDs != null) {
					foreach (uint appId in packageData.AppIDs) {
						ownedAppIds.Add(appId);
					}
				}
			}
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Resolved {ownedAppIds.Count} owned app IDs from {bot.OwnedPackages.Count} packages");

		int alreadyOwned = 0;
		int notOwned = 0;
		int countryBlocked = 0;
		int expired = 0;
		int soldOut = 0;
		int noAppId = 0;
		int alreadyRedeemed = 0;
		int availableToRedeem = 0;

		foreach (HumbleTpkInfo tpk in humbleTpks) {
			string gameName = tpk.HumanName;

			// Check if already revealed/redeemed on Humble
			bool hasKey = !string.IsNullOrEmpty(tpk.RedeemedKeyVal);

			// Check expiry
			if (tpk.IsExpired) {
				ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] EXPIRED: '{gameName}' (AppID: {tpk.SteamAppId})");
				expired++;
				continue;
			}

			// Check sold out
			if (tpk.SoldOut) {
				ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] SOLD OUT: '{gameName}' (AppID: {tpk.SteamAppId})");
				soldOut++;
				continue;
			}

			// Check country restrictions (unless IgnoreStoreLocation or IgnoreStoreLocationButRedeem is enabled)
			if (!effectiveIgnoreLocation && !string.IsNullOrEmpty(countryCode)) {
				// Check disallowed_countries - if bot's country is in the list, key cannot be redeemed
				if (tpk.DisallowedCountries.Count > 0) {
					bool isDisallowed = false;

					foreach (string disallowed in tpk.DisallowedCountries) {
						if (string.Equals(disallowed, countryCode, StringComparison.OrdinalIgnoreCase)) {
							isDisallowed = true;
							break;
						}
					}

					if (isDisallowed) {
						ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] COUNTRY BLOCKED: '{gameName}' (AppID: {tpk.SteamAppId}) - country '{countryCode}' is in disallowed list [{string.Join(", ", tpk.DisallowedCountries)}]");
						countryBlocked++;
						continue;
					}
				}

				// Check exclusive_countries - if non-empty, bot's country MUST be in the list
				if (tpk.ExclusiveCountries.Count > 0) {
					bool isAllowed = false;

					foreach (string allowed in tpk.ExclusiveCountries) {
						if (string.Equals(allowed, countryCode, StringComparison.OrdinalIgnoreCase)) {
							isAllowed = true;
							break;
						}
					}

					if (!isAllowed) {
						ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] COUNTRY BLOCKED: '{gameName}' (AppID: {tpk.SteamAppId}) - country '{countryCode}' is not in exclusive list [{string.Join(", ", tpk.ExclusiveCountries)}]");
						countryBlocked++;
						continue;
					}
				}
			}

			// Check if we have a steam_app_id to compare
			if (tpk.SteamAppId == 0) {
				if (skipUnknownAppIds) {
					ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] NO APP ID: '{gameName}' - skipped (HumbleBundleSkipUnknownAppIds=true)");
				} else if (hasKey) {
					ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] NO APP ID (key revealed, not for Steam): '{gameName}'");
				} else {
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] NO APP ID (will reveal on Humble, not for Steam): '{gameName}'");
					availableToRedeem++;
				}

				noAppId++;
				continue;
			}

			// Check if already owned on Steam
			if (ownedAppIds.Contains(tpk.SteamAppId)) {
				ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] ALREADY OWNED: '{gameName}' (AppID: {tpk.SteamAppId})");
				alreadyOwned++;
				continue;
			}

			// Not owned - check if key is already revealed
			if (hasKey) {
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] NOT OWNED (key revealed): '{gameName}' (AppID: {tpk.SteamAppId})");
				alreadyRedeemed++;
			} else {
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] NOT OWNED (key not yet revealed): '{gameName}' (AppID: {tpk.SteamAppId})");
				availableToRedeem++;
			}

			notOwned++;
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] === Humble Bundle vs Steam Comparison Results ===");
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Total Steam TPKs: {humbleTpks.Count}");
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Already owned on Steam: {alreadyOwned}");
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Not owned on Steam: {notOwned} (revealed: {alreadyRedeemed}, unrevealed: {availableToRedeem})");
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Country blocked ({countryCode ?? "unknown"}): {countryBlocked}");
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Expired: {expired}");
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Sold out: {soldOut}");
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] No App ID (cannot verify): {noAppId}");

		// Surface Choice orders too — they're processed in a separate pass after this comparison
		// (Choice TPKs aren't in humbleTpks at this point, so the Steam TPK count above doesn't include them).
		int trackedChoiceOrders = BotChoiceOrders.TryGetValue(bot, out List<ChoiceOrderInfo>? co) ? co.Count : 0;
		if (trackedChoiceOrders > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Humble Choice orders to process: {trackedChoiceOrders} (separate from the {humbleTpks.Count} regular TPKs above)");
		}

		// Automatically redeem unrevealed keys that are not owned
		if (availableToRedeem > 0) {
			await RedeemAvailableKeys(bot, humbleTpks, ownedAppIds, countryCode, ignoreStoreLocation, ignoreStoreLocationButRedeem).ConfigureAwait(false);
		}

		// Process Humble Choice orders
		await ProcessChoiceOrders(bot, humbleTpks, ownedAppIds, countryCode, ignoreStoreLocation).ConfigureAwait(false);

		// Start periodic retry timer for keys that couldn't be redeemed (sold out, etc.).
		// Unknown-AppId TPKs are included only when SkipUnknownAppIds is false (otherwise they
		// were filtered out and there's nothing to retry).
		int remainingUnrevealed = humbleTpks.Count(t =>
			string.IsNullOrEmpty(t.RedeemedKeyVal) && !t.IsExpired && !t.SoldOut && !t.IsGift
			&& IsCountryAllowed(t, countryCode, effectiveIgnoreLocation)
			&& (t.SteamAppId == 0
				? !skipUnknownAppIds
				: !ownedAppIds.Contains(t.SteamAppId)));

		if (remainingUnrevealed > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] {remainingUnrevealed} keys still unrevealed, starting retry timer");
			StartRedeemRetryTimer(bot);
		}
	}

	private static bool IsCountryAllowed(HumbleTpkInfo tpk, string? countryCode, bool ignoreStoreLocation = false) {
		if (ignoreStoreLocation || string.IsNullOrEmpty(countryCode)) {
			return true;
		}

		if (tpk.DisallowedCountries.Count > 0) {
			foreach (string disallowed in tpk.DisallowedCountries) {
				if (string.Equals(disallowed, countryCode, StringComparison.OrdinalIgnoreCase)) {
					return false;
				}
			}
		}

		if (tpk.ExclusiveCountries.Count > 0) {
			bool found = false;

			foreach (string allowed in tpk.ExclusiveCountries) {
				if (string.Equals(allowed, countryCode, StringComparison.OrdinalIgnoreCase)) {
					found = true;
					break;
				}
			}

			if (!found) {
				return false;
			}
		}

		return true;
	}
}
