using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;

namespace HumbleRedeemer;

internal sealed partial class HumbleRedeemer {
	private static async Task ProcessChoiceOrders(Bot bot, List<HumbleTpkInfo> humbleTpks, HashSet<uint> ownedAppIds, string? countryCode, bool ignoreStoreLocation = false) {
		if (!BotChoiceOrders.TryGetValue(bot, out List<ChoiceOrderInfo>? choiceOrders) || choiceOrders.Count == 0) {
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] No Choice orders to process");
			return;
		}

		if (!BotHandlers.TryGetValue(bot, out HumbleBundleWebHandler? webHandler)) {
			ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] No web handler available for processing Choice orders");
			return;
		}

		if (!BotCaches.TryGetValue(bot, out HumbleBundleBotCache? botCache)) {
			ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] No cache available for saving Choice keys");
			return;
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] === Processing {choiceOrders.Count} Humble Choice Orders ===");

		BotConfigs.TryGetValue(bot, out HumbleBundleBotConfig? choiceConfig);
		bool payMonthlyButNotReveal = choiceConfig?.PayMonthlyButNotReveal ?? false;
		bool payMonthlyRevealButNotToSteam = choiceConfig?.PayMonthlyRevealButNotToSteam ?? false;
		bool redeemOnSteam = choiceConfig?.RedeemOnSteam ?? false;
		HashSet<string> paidChoiceKeys = BotPaidGameKeys.TryGetValue(bot, out HashSet<string>? cpgk) ? cpgk : new HashSet<string>();

		int totalRedeemed = 0;
		int totalFailed = 0;
		int totalSkipped = 0;
		int totalSteamRedeemed = 0;
		int totalSteamSkippedForRateLimit = 0;
		bool steamRateLimited = false;

		foreach (ChoiceOrderInfo choiceOrder in choiceOrders) {
			try {
				bool isPaidOrder = paidChoiceKeys.Contains(choiceOrder.GameKey);

				if (isPaidOrder && payMonthlyButNotReveal) {
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] PAID (not revealing): '{choiceOrder.HumanName}' - key reveal skipped by config");
					continue;
				}

				bool revealButNotToSteam = isPaidOrder && payMonthlyRevealButNotToSteam;

				List<ChoiceRedemptionResult> results = await webHandler.ProcessChoiceOrderAsync(
					choiceOrder.GameKey,
					choiceOrder.ChoiceUrl,
					choiceOrder.HumanName
				).ConfigureAwait(false);

				foreach (ChoiceRedemptionResult result in results) {
					// Convert to HumbleTpkInfo and add to cache
					if (!string.IsNullOrEmpty(result.Key)) {
						// Reuse an existing cached TPK so flags like SteamRedeemAttempted persist;
						// otherwise create and append a new one.
						HumbleTpkInfo? tpk = humbleTpks.FirstOrDefault(t =>
							t.MachineName.Equals(result.MachineName, StringComparison.OrdinalIgnoreCase) &&
							t.GameKey.Equals(choiceOrder.GameKey, StringComparison.OrdinalIgnoreCase));

						if (tpk == null) {
							tpk = new HumbleTpkInfo {
								GameKey = choiceOrder.GameKey,
								HumanName = result.GameName,
								MachineName = result.MachineName,
								SteamAppId = 0, // Choice page doesn't always provide AppId reliably
								RedeemedKeyVal = result.Key,
								IsExpired = false,
								SoldOut = false,
								KeyIndex = 0,
								IsGift = false,
								DisallowedCountries = [],
								ExclusiveCountries = []
							};

							humbleTpks.Add(tpk);
						} else {
							tpk.RedeemedKeyVal = result.Key;
						}

						if (revealButNotToSteam) {
							ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] CHOICE REVEALED (not for Steam): '{result.GameName}' from {result.ChoiceTitle} => {result.Key}");
						} else {
							ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] CHOICE REDEEMED: '{result.GameName}' from {result.ChoiceTitle} => {result.Key}");
						}

						totalRedeemed++;

						// Forward Steam keys to Steam if enabled (only "steam" key types; not gift/skipSteam).
						// Skip when rate-limited or when we already know the AppId is owned (defensive
						// — Choice usually leaves SteamAppId == 0 so this mostly matters for re-runs
						// where we picked up the AppId from a regular order TPK).
						if (redeemOnSteam && !revealButNotToSteam && !steamRateLimited
							&& result.KeyType.Equals("steam", StringComparison.OrdinalIgnoreCase)
							&& !tpk.SteamRedeemAttempted
							&& (tpk.SteamAppId == 0 || !ownedAppIds.Contains(tpk.SteamAppId))) {
							SteamRedeemOutcome outcome = await TryRedeemKeyOnSteamAsync(bot, result.Key, result.GameName, tpk.SteamAppId).ConfigureAwait(false);

							switch (outcome) {
								case SteamRedeemOutcome.Terminal:
									tpk.SteamRedeemAttempted = true;
									totalSteamRedeemed++;

									break;
								case SteamRedeemOutcome.RateLimited:
									steamRateLimited = true;
									totalSteamSkippedForRateLimit++;

									break;
							}
						} else if (redeemOnSteam && !revealButNotToSteam && steamRateLimited
							&& result.KeyType.Equals("steam", StringComparison.OrdinalIgnoreCase)
							&& !tpk.SteamRedeemAttempted
							&& (tpk.SteamAppId == 0 || !ownedAppIds.Contains(tpk.SteamAppId))) {
							totalSteamSkippedForRateLimit++;
						}
					} else if (!string.IsNullOrEmpty(result.Error)) {
						if (result.Error == "Expired" || result.Error == "Sold out") {
							ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] CHOICE SKIPPED: '{result.GameName}' from {result.ChoiceTitle} ({result.Error})");
							totalSkipped++;
						} else {
							ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] CHOICE FAILED: '{result.GameName}' from {result.ChoiceTitle} - {result.Error}");
							totalFailed++;
						}
					}
				}

				// Small delay between choice orders
				await Task.Delay(1000).ConfigureAwait(false);
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{bot.BotName}] Failed to process Choice order: {choiceOrder.HumanName}");
				totalFailed++;
			}
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] === Choice Processing Complete ===");
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Redeemed: {totalRedeemed}, Failed: {totalFailed}, Skipped: {totalSkipped}");

		if (redeemOnSteam) {
			string suffix = totalSteamSkippedForRateLimit > 0
				? $", {totalSteamSkippedForRateLimit} skipped due to Steam rate limit (will retry on next timer cycle)"
				: "";
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Steam redeem results: {totalSteamRedeemed} Choice keys forwarded to Steam{suffix}");
		}

		// Update cache with redeemed Choice keys
		if (totalRedeemed > 0) {
			botCache.CachedTpks = humbleTpks;
			await botCache.SaveAsync().ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Updated cache with {totalRedeemed} newly redeemed Choice keys");
		}
	}
}
