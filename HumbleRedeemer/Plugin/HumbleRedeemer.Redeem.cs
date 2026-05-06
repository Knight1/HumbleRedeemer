using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Storage;

namespace HumbleRedeemer;

internal sealed partial class HumbleRedeemer {
	private static async Task RedeemAvailableKeys(Bot bot, List<HumbleTpkInfo> humbleTpks, HashSet<uint> ownedAppIds, string? countryCode, bool ignoreStoreLocation = false, bool ignoreStoreLocationButRedeem = false) {
		if (!BotHandlers.TryGetValue(bot, out HumbleBundleWebHandler? webHandler)) {
			ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] No web handler available for redeeming keys");
			return;
		}

		BotConfigs.TryGetValue(bot, out HumbleBundleBotConfig? config);
		bool useGiftLinkForOwned = config?.UseGiftLinkForOwned ?? false;
		bool redeemOnlyWithExpiration = config?.RedeemOnlyWithExpiration ?? false;
		bool skipUnknownAppIds = config?.SkipUnknownAppIds ?? false;
		bool effectiveIgnoreLocation = ignoreStoreLocation || ignoreStoreLocationButRedeem;
		bool payMonthlyButNotReveal = config?.PayMonthlyButNotReveal ?? false;
		bool payMonthlyRevealButNotToSteam = config?.PayMonthlyRevealButNotToSteam ?? false;
		bool redeemOnSteam = config?.RedeemOnSteam ?? false;
		HashSet<uint> blacklistedAppIds = config?.BlacklistedAppIds != null
			? new HashSet<uint>(config.BlacklistedAppIds)
			: new HashSet<uint>();
		HashSet<uint> redeemButNotToSteamAppIds = config?.RedeemButNotToSteamAppIds != null
			? new HashSet<uint>(config.RedeemButNotToSteamAppIds)
			: new HashSet<uint>();
		HashSet<string> paidGameKeys = BotPaidGameKeys.TryGetValue(bot, out HashSet<string>? pgk) ? pgk : new HashSet<string>();

		// Collect eligible TPKs: unrevealed, not expired, not sold out, not already a gift, not country blocked
		// If UseGiftLinkForOwned is true, also include games already owned (to redeem as gift links)
		// If RedeemOnlyWithExpiration is true, only include keys that have an expiration date
		// If SkipUnknownAppIds is true, skip keys without a Steam AppId entirely; otherwise reveal
		// them on Humble but never forward to Steam (unknown-AppId TPKs are often non-Steam codes
		// like "Get One Month of IGN Plus" — sending them to Steam would burn a rate-limit slot).
		List<(HumbleTpkInfo tpk, bool asGift, bool skipSteam)> toRedeem = new();

		foreach (HumbleTpkInfo tpk in humbleTpks) {
			if (!string.IsNullOrEmpty(tpk.RedeemedKeyVal) || tpk.IsExpired || tpk.SoldOut || tpk.IsGift) {
				continue;
			}

			// Unknown AppId: hard skip if the option is enabled, otherwise reveal-but-never-Steam.
			bool unknownAppId = tpk.SteamAppId == 0;

			if (unknownAppId && skipUnknownAppIds) {
				continue;
			}

			// Skip if this came from an auto-paid month and reveal is disabled
			if (payMonthlyButNotReveal && paidGameKeys.Contains(tpk.GameKey)) {
				continue;
			}

			// Skip if AppId is blacklisted (only meaningful when AppId is known)
			if (!unknownAppId && blacklistedAppIds.Contains(tpk.SteamAppId)) {
				continue;
			}

			if (!IsCountryAllowed(tpk, countryCode, effectiveIgnoreLocation)) {
				continue;
			}

			// If RedeemOnlyWithExpiration is enabled, skip keys without an expiration date
			if (redeemOnlyWithExpiration && !tpk.ExpiryDate.HasValue) {
				continue;
			}

			bool isOwned = !unknownAppId && ownedAppIds.Contains(tpk.SteamAppId);

			// If already owned and not using gift links for owned games, skip
			if (isOwned && !useGiftLinkForOwned) {
				continue;
			}

			// Redeem as gift if already owned and UseGiftLinkForOwned is enabled
			bool redeemAsGift = isOwned && useGiftLinkForOwned;
			// Mark as "not for Steam" if: AppId is in the list, key only passed country check
			// because IgnoreStoreLocationButRedeem is enabled, it's from an auto-paid month
			// with PayMonthlyRevealButNotToSteam enabled, or the AppId is unknown (we can't
			// verify ownership and the code may not even be a Steam key).
			bool isRegionRestricted = !IsCountryAllowed(tpk, countryCode);
			bool skipSteam = unknownAppId
				|| redeemButNotToSteamAppIds.Contains(tpk.SteamAppId)
				|| (ignoreStoreLocationButRedeem && isRegionRestricted)
				|| (payMonthlyRevealButNotToSteam && paidGameKeys.Contains(tpk.GameKey));
			toRedeem.Add((tpk, redeemAsGift, skipSteam));
		}

		if (toRedeem.Count == 0) {
			return;
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Attempting to redeem {toRedeem.Count} unrevealed keys...");

		int redeemed = 0;
		int redeemedAsGift = 0;
		int revealedNotForSteam = 0;
		int failed = 0;
		int steamRedeemed = 0;
		int steamSkippedForRateLimit = 0;
		bool cacheUpdated = false;
		bool steamRateLimited = false;

		foreach ((HumbleTpkInfo tpk, bool asGift, bool skipSteam) in toRedeem) {
			HumbleRedeemResult redeem = await webHandler.RedeemKeyAsync(tpk.MachineName, tpk.GameKey, tpk.KeyIndex, asGift).ConfigureAwait(false);
			string? key = redeem.Key;

			if (!string.IsNullOrEmpty(key)) {
				if (asGift) {
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] REDEEMED AS GIFT: '{tpk.HumanName}' (AppID: {tpk.SteamAppId}) => {key}");
					redeemedAsGift++;
				} else if (skipSteam) {
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] REVEALED (not redeemed on Steam): '{tpk.HumanName}' (AppID: {tpk.SteamAppId}) => {key}");
					revealedNotForSteam++;
				} else {
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] REDEEMED: '{tpk.HumanName}' (AppID: {tpk.SteamAppId}) => {key}");
				}
				tpk.RedeemedKeyVal = key;
				redeemed++;
				cacheUpdated = true;

				// Forward the revealed key to Steam if enabled and it's a real Steam key
				// (not a gift URL, not opted out via skipSteam). Once Steam returns RateLimited
				// we keep revealing on Humble (the keys still need to be cached for later) but
				// stop submitting to Steam until the retry timer comes around.
				if (redeemOnSteam && !asGift && !skipSteam && !steamRateLimited) {
					SteamRedeemOutcome outcome = await TryRedeemKeyOnSteamAsync(bot, key, tpk.HumanName, tpk.SteamAppId).ConfigureAwait(false);

					switch (outcome) {
						case SteamRedeemOutcome.Terminal:
							tpk.SteamRedeemAttempted = true;
							steamRedeemed++;

							break;
						case SteamRedeemOutcome.RateLimited:
							steamRateLimited = true;
							steamSkippedForRateLimit++;

							break;
					}
				} else if (redeemOnSteam && !asGift && !skipSteam && steamRateLimited) {
					steamSkippedForRateLimit++;
				}
			} else {
				string reason = redeem.ErrorType switch {
					"keys_depleted_email" => "depleted",
					"not_logged_in" => "not logged in",
					"transport" => "network error",
					"parse_error" => "bad response",
					_ => redeem.ErrorType ?? "unknown"
				};
				ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] FAILED TO REDEEM: '{tpk.HumanName}' (AppID: {tpk.SteamAppId}) - {reason}");
				failed++;
			}

			// Small delay between redemptions to avoid rate limiting
			await Task.Delay(500).ConfigureAwait(false);
		}

		// Process keys that were already revealed in a previous run but never sent to Steam.
		// This catches users who enabled HumbleBundleRedeemOnSteam after some keys were
		// already revealed by an earlier run.
		if (redeemOnSteam && !steamRateLimited) {
			foreach (HumbleTpkInfo tpk in humbleTpks) {
				if (string.IsNullOrEmpty(tpk.RedeemedKeyVal) || tpk.SteamRedeemAttempted || tpk.IsGift) {
					continue;
				}

				// Owned-on-Steam check: respect the existing ownership detection so we never
				// burn a Steam-side activation slot on a game we already have.
				if (tpk.SteamAppId == 0 || ownedAppIds.Contains(tpk.SteamAppId)) {
					continue;
				}

				if (blacklistedAppIds.Contains(tpk.SteamAppId) || redeemButNotToSteamAppIds.Contains(tpk.SteamAppId)) {
					continue;
				}

				if (!IsCountryAllowed(tpk, countryCode, effectiveIgnoreLocation)) {
					continue;
				}

				SteamRedeemOutcome outcome = await TryRedeemKeyOnSteamAsync(bot, tpk.RedeemedKeyVal, tpk.HumanName, tpk.SteamAppId).ConfigureAwait(false);

				switch (outcome) {
					case SteamRedeemOutcome.Terminal:
						tpk.SteamRedeemAttempted = true;
						steamRedeemed++;
						cacheUpdated = true;

						break;
					case SteamRedeemOutcome.RateLimited:
						steamRateLimited = true;
						steamSkippedForRateLimit++;

						break;
				}

				if (steamRateLimited) {
					break;
				}

				await Task.Delay(500).ConfigureAwait(false);
			}
		}

		int normalRedeemed = redeemed - redeemedAsGift - revealedNotForSteam;

		if (redeemedAsGift > 0 || revealedNotForSteam > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Redeem results: {normalRedeemed} succeeded, {redeemedAsGift} redeemed as gift links, {revealedNotForSteam} revealed (not for Steam), {failed} failed");
		} else {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Redeem results: {redeemed} succeeded, {failed} failed");
		}

		if (redeemOnSteam) {
			string suffix = steamSkippedForRateLimit > 0
				? $", {steamSkippedForRateLimit} skipped due to Steam rate limit (will retry on next timer cycle)"
				: "";
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Steam redeem results: {steamRedeemed} keys forwarded to Steam{suffix}");
		}

		// Update cache with redeemed keys
		if (cacheUpdated && BotCaches.TryGetValue(bot, out HumbleBundleBotCache? botCache)) {
			botCache.CachedTpks = humbleTpks;
			await botCache.SaveAsync().ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Updated cache with {redeemed} newly redeemed keys");
		}
	}

	private static void StartRedeemRetryTimer(Bot bot) {
		// Check if auto-retry is enabled
		if (!BotConfigs.TryGetValue(bot, out HumbleBundleBotConfig? config) || !config.AutoRetry) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Auto-retry is disabled, skipping timer start");
			return;
		}

		// Dispose existing timer if any
		if (BotRedeemTimers.TryRemove(bot, out System.Threading.Timer? existingTimer)) {
			existingTimer.Dispose();
		}

		int intervalMinutes = config.RedeemRetryIntervalMinutes;
		TimeSpan interval = TimeSpan.FromMinutes(intervalMinutes);

		System.Threading.Timer timer = new(
			_ => _ = Task.Run(async () => await RetryRedeemAvailableKeys(bot).ConfigureAwait(false)),
			null,
			interval,
			interval
		);

		BotRedeemTimers[bot] = timer;
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Redeem retry timer started (interval: {intervalMinutes} minutes)");
	}

	private static async Task RetryRedeemAvailableKeys(Bot bot) {
		if (!BotHandlers.TryGetValue(bot, out HumbleBundleWebHandler? webHandler)) {
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] No web handler available for redeem retry");
			return;
		}

		if (!BotHumbleTpks.TryGetValue(bot, out List<HumbleTpkInfo>? humbleTpks) || humbleTpks.Count == 0) {
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] No TPK data available for redeem retry");
			return;
		}

		BotCountryCodes.TryGetValue(bot, out string? countryCode);
		BotConfigs.TryGetValue(bot, out HumbleBundleBotConfig? config);
		bool ignoreStoreLocation = config?.IgnoreStoreLocation ?? false;
		bool ignoreStoreLocationButRedeem = config?.IgnoreStoreLocationButRedeem ?? false;
		bool effectiveIgnoreLocation = ignoreStoreLocation || ignoreStoreLocationButRedeem;
		bool skipUnknownAppIds = config?.SkipUnknownAppIds ?? false;

		// Re-fetch order keys to check for newly available keys
		List<string>? orderKeys = await webHandler.GetOrderKeysAsync().ConfigureAwait(false);

		if (orderKeys == null) {
			ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Failed to fetch order keys during redeem retry");
			return;
		}

		// Check for new orders that weren't in the original set
		HashSet<string> knownGameKeys = new(humbleTpks.Select(t => t.GameKey), StringComparer.OrdinalIgnoreCase);
		List<string> newGameKeys = orderKeys.Where(key => !knownGameKeys.Contains(key)).ToList();

		if (newGameKeys.Count > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Redeem retry found {newGameKeys.Count} new orders");

			Dictionary<string, JsonElement>? newOrders = await webHandler.GetAllOrdersIndividuallyAsync(newGameKeys).ConfigureAwait(false);

			if (newOrders != null && newOrders.Count > 0) {
				List<ChoiceOrderInfo> choiceOrders = BotChoiceOrders.GetValueOrDefault(bot) ?? new List<ChoiceOrderInfo>();

				foreach ((string orderKey, JsonElement orderData) in newOrders) {
					List<HumbleTpkInfo> orderTpks = ExtractSteamTpksFromOrder(bot.BotName, orderKey, orderData);
					humbleTpks.AddRange(orderTpks);

					// Check if this is a new Choice order
					ChoiceOrderInfo? choiceInfo = ExtractChoiceOrderInfo(orderKey, orderData);
					if (choiceInfo != null) {
						bool alreadyTracked = choiceOrders.Any(c => c.GameKey.Equals(choiceInfo.GameKey, StringComparison.OrdinalIgnoreCase));
						if (!alreadyTracked) {
							choiceOrders.Add(choiceInfo);
						}
					}
				}

				BotHumbleTpks[bot] = humbleTpks;
				BotChoiceOrders[bot] = choiceOrders;
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Updated TPK list, now {humbleTpks.Count} total");

				if (choiceOrders.Count > 0) {
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Tracking {choiceOrders.Count} Choice orders");
				}
			}
		}

		// Build owned app set
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

		// Attempt to redeem available keys
		await RedeemAvailableKeys(bot, humbleTpks, ownedAppIds, countryCode, ignoreStoreLocation, ignoreStoreLocationButRedeem).ConfigureAwait(false);

		// Process Choice orders (if any)
		await ProcessChoiceOrders(bot, humbleTpks, ownedAppIds, countryCode, ignoreStoreLocation).ConfigureAwait(false);

		// Check if there are still unrevealed keys remaining (matches the predicate in
		// CompareHumbleBundleWithSteamLibrary — unknown-AppId TPKs only count when SkipUnknownAppIds is false).
		int remainingCount = humbleTpks.Count(t =>
			string.IsNullOrEmpty(t.RedeemedKeyVal) && !t.IsExpired && !t.SoldOut && !t.IsGift
			&& IsCountryAllowed(t, countryCode, effectiveIgnoreLocation)
			&& (t.SteamAppId == 0
				? !skipUnknownAppIds
				: !ownedAppIds.Contains(t.SteamAppId)));

		if (remainingCount == 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] All keys redeemed, stopping retry timer");

			if (BotRedeemTimers.TryRemove(bot, out System.Threading.Timer? timer)) {
				await timer.DisposeAsync().ConfigureAwait(false);
			}
		} else {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] {remainingCount} keys still unredeemed, will retry later");
		}
	}
}
