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

		int totalNewlyRevealed = 0;
		int totalAlreadyRevealed = 0;
		int totalFailed = 0;
		int totalSkipped = 0;
		int totalCompletedSkipped = 0;
		int totalSteamRedeemed = 0;
		int totalSteamSkippedForRateLimit = 0;
		int totalFailurePlaceholdersAdded = 0;
		int totalFailuresAlreadyTracked = 0;
		int ordersMarkedCompleted = 0;
		bool steamRateLimited = false;
		bool choiceMetadataChanged = false;

		foreach (ChoiceOrderInfo choiceOrder in choiceOrders) {
			try {
				bool isPaidOrder = paidChoiceKeys.Contains(choiceOrder.GameKey);

				if (isPaidOrder && payMonthlyButNotReveal) {
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] PAID (not revealing): '{choiceOrder.HumanName}' - key reveal skipped by config");
					continue;
				}

				// Skip choice months that we've already fully processed in a previous run.
				// `Completed` is only set after a pass with zero failures, so this never
				// hides keys we still owe — but it does cut out the redundant page-fetch
				// + "CHOICE REDEEMED" log spam for finished months.
				if (choiceOrder.Completed) {
					ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] CHOICE SKIPPED (already complete): '{choiceOrder.HumanName}' (delete the bot cache to force re-process)");
					totalCompletedSkipped++;
					continue;
				}

				bool revealButNotToSteam = isPaidOrder && payMonthlyRevealButNotToSteam;

				List<ChoiceRedemptionResult> results = await webHandler.ProcessChoiceOrderAsync(
					choiceOrder.GameKey,
					choiceOrder.ChoiceUrl,
					choiceOrder.HumanName
				).ConfigureAwait(false);

				int orderFailureCount = 0;
				int orderResultCount = results.Count;

				foreach (ChoiceRedemptionResult result in results) {
					// Convert to HumbleTpkInfo and add to cache
					if (!string.IsNullOrEmpty(result.Key)) {
						// Reuse an existing cached TPK so flags like SteamRedeemAttempted persist;
						// otherwise create and append a new one.
						HumbleTpkInfo? tpk = humbleTpks.FirstOrDefault(t =>
							t.MachineName.Equals(result.MachineName, StringComparison.OrdinalIgnoreCase) &&
							t.GameKey.Equals(choiceOrder.GameKey, StringComparison.OrdinalIgnoreCase));

						// "Already revealed" = a TPK already exists in our cache with the same
						// key value, meaning a previous run already revealed it and the choice
						// page just echoed it back to us. No new HTTP work happened — just log noise.
						bool wasAlreadyRevealed = tpk != null
							&& !string.IsNullOrEmpty(tpk.RedeemedKeyVal)
							&& string.Equals(tpk.RedeemedKeyVal, result.Key, StringComparison.Ordinal);

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

						if (wasAlreadyRevealed) {
							ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] CHOICE ALREADY REVEALED (cached): '{result.GameName}' from {result.ChoiceTitle}");
							totalAlreadyRevealed++;
						} else if (revealButNotToSteam) {
							ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] CHOICE REVEALED (not for Steam): '{result.GameName}' from {result.ChoiceTitle} => {result.Key}");
							totalNewlyRevealed++;
						} else {
							ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] CHOICE REDEEMED: '{result.GameName}' from {result.ChoiceTitle} => {result.Key}");
							totalNewlyRevealed++;
						}

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
							// The choice page didn't return a key for this TPK. Look up our
							// cache: if we already have a successfully-revealed key from a prior
							// run, treat this as a transient page flake — our key is still valid
							// (Humble's choice page intermittently omits keys we already have).
							// Otherwise it's a genuine failure that needs retry tracking.
							HumbleTpkInfo? existing = humbleTpks.FirstOrDefault(t =>
								t.MachineName.Equals(result.MachineName, StringComparison.OrdinalIgnoreCase) &&
								t.GameKey.Equals(choiceOrder.GameKey, StringComparison.OrdinalIgnoreCase));

							if (existing != null && !string.IsNullOrEmpty(existing.RedeemedKeyVal)) {
								// We already have the key — flake, not a real failure.
								ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] CHOICE PAGE FLAKE (key cached, ignoring): '{result.GameName}' from {result.ChoiceTitle}");
								totalAlreadyRevealed++;
								continue;
							}

							ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] CHOICE FAILED: '{result.GameName}' from {result.ChoiceTitle} - {result.Error}");
							totalFailed++;
							orderFailureCount++;

							// Track the failure as an unrevealed placeholder TPK so the
							// retry-timer's "unrevealed" count includes it. If an existing
							// placeholder is already there (RedeemedKeyVal null), it's already
							// counted by the predicate — no new placeholder needed.
							if (existing == null) {
								humbleTpks.Add(new HumbleTpkInfo {
									GameKey = choiceOrder.GameKey,
									HumanName = result.GameName,
									MachineName = result.MachineName,
									SteamAppId = 0,
									RedeemedKeyVal = null,
									IsExpired = false,
									SoldOut = false,
									KeyIndex = 0,
									IsGift = false,
									DisallowedCountries = [],
									ExclusiveCountries = []
								});
								totalFailurePlaceholdersAdded++;
							} else {
								totalFailuresAlreadyTracked++;
							}
						}
					}
				}

				// Mark the choice order Completed when this pass produced no failures AND we
				// actually got results back (i.e. the choice page was reachable). Subsequent
				// runs will skip this order entirely. If a previously-Completed order surfaced
				// failures here, reset to false so it gets reprocessed next time.
				if (orderResultCount > 0 && orderFailureCount == 0) {
					if (!choiceOrder.Completed) {
						choiceOrder.Completed = true;
						choiceMetadataChanged = true;
						ordersMarkedCompleted++;
						ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] CHOICE COMPLETE: '{choiceOrder.HumanName}' has no remaining work — will be skipped on future runs");
					}
				} else if (orderFailureCount > 0 && choiceOrder.Completed) {
					choiceOrder.Completed = false;
					choiceMetadataChanged = true;
				}

				// Small delay between choice orders
				await Task.Delay(1000).ConfigureAwait(false);
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{bot.BotName}] Failed to process Choice order: {choiceOrder.HumanName}");
				totalFailed++;
				if (choiceOrder.Completed) {
					choiceOrder.Completed = false;
					choiceMetadataChanged = true;
				}
			}
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] === Choice Processing Complete ===");

		string failurePlaceholderSummary = totalFailed > 0
			? $" ({totalFailurePlaceholdersAdded} new placeholders, {totalFailuresAlreadyTracked} already tracked)"
			: "";
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Newly revealed: {totalNewlyRevealed}, Already revealed (cached): {totalAlreadyRevealed}, Failed: {totalFailed}{failurePlaceholderSummary}, Skipped expired/sold-out: {totalSkipped}");

		if (totalCompletedSkipped > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Skipped {totalCompletedSkipped} previously-completed Choice orders");
		}

		if (ordersMarkedCompleted > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Marked {ordersMarkedCompleted} Choice orders as fully complete");
		}

		if (redeemOnSteam) {
			string suffix = totalSteamSkippedForRateLimit > 0
				? $", {totalSteamSkippedForRateLimit} skipped due to Steam rate limit (will retry on next timer cycle)"
				: "";
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Steam redeem results: {totalSteamRedeemed} Choice keys forwarded to Steam{suffix}");
		}

		// Persist if anything in humbleTpks changed: revealed Choice keys, or new failure
		// placeholders that need to survive across restarts so the retry timer can re-attempt them.
		if (totalNewlyRevealed > 0 || totalFailurePlaceholdersAdded > 0) {
			botCache.CachedTpks = humbleTpks;
			await botCache.SaveAsync().ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Updated cache with {totalNewlyRevealed} newly revealed Choice keys and {totalFailurePlaceholdersAdded} failure placeholders for retry");
		}

		// Persist Choice metadata changes (Completed flag flips) separately so a clean
		// no-new-keys pass that just marks orders complete still saves to disk.
		if (choiceMetadataChanged) {
			botCache.CachedChoiceOrders = new List<ChoiceOrderInfo>(choiceOrders);
			await botCache.SaveAsync().ConfigureAwait(false);
		}
	}
}
