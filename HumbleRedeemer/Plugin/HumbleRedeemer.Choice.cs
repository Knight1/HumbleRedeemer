using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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
		bool redeemEpicKeyless = choiceConfig?.RedeemEpicKeyless ?? false;
		bool redeemGogKeyless = choiceConfig?.RedeemGogKeyless ?? false;
		bool redeemBlizzardKeyless = choiceConfig?.RedeemBlizzardKeyless ?? false;
		bool redeemOriginKeyless = choiceConfig?.RedeemOriginKeyless ?? false;
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
		List<string> depletedGames = new();

		// The current month's choice_url (e.g. "may-2026"). The current month is never skipped even
		// when marked Completed, because Humble can still append keys to it (e.g. a late "playtest"
		// key) — we must re-check it every pass so those are picked up.
		DateTime nowUtc = DateTime.UtcNow;
#pragma warning disable CA1308 // Lowercase required to match Humble's choice_url format
		string currentChoiceUrl = $"{nowUtc.ToString("MMMM", CultureInfo.InvariantCulture).ToLowerInvariant()}-{nowUtc.Year}";
#pragma warning restore CA1308

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
				// + "CHOICE REDEEMED" log spam for finished months. The CURRENT month is the
				// exception: Humble can add keys to it after completion, so always re-check it.
				bool isCurrentMonth = choiceOrder.ChoiceUrl.Equals(currentChoiceUrl, StringComparison.OrdinalIgnoreCase);

				if (choiceOrder.Completed && !isCurrentMonth) {
					// Per-choice line stays at debug level (only visible when ASF is in debug mode);
					// the visible summary at the bottom of this method aggregates the count + cache hint.
					ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] CHOICE SKIPPED (already complete): '{choiceOrder.HumanName}'");
					totalCompletedSkipped++;
					continue;
				}

				bool revealButNotToSteam = isPaidOrder && payMonthlyRevealButNotToSteam;

				List<ChoiceRedemptionResult> results = await webHandler.ProcessChoiceOrderAsync(
					choiceOrder.GameKey,
					choiceOrder.ChoiceUrl,
					choiceOrder.HumanName,
					redeemEpicKeyless,
					redeemGogKeyless,
					redeemBlizzardKeyless,
					redeemOriginKeyless
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
								KeyType = result.KeyType,
								SteamAppId = 0, // Choice page doesn't always provide AppId reliably
								RedeemedKeyVal = result.Key,
								IsExpired = false,
								SoldOut = false,
								KeyIndex = 0,
								IsGift = false,
								DisallowedCountries = [],
								ExclusiveCountries = [],
								IsChoiceTpk = true
							};

							humbleTpks.Add(tpk);
						} else {
							tpk.RedeemedKeyVal = result.Key;
							tpk.IsChoiceTpk = true;
							if (string.IsNullOrEmpty(tpk.KeyType)) {
								tpk.KeyType = result.KeyType;
							}
						}

						if (wasAlreadyRevealed) {
							// No per-game log here — even at debug level this would emit one line
							// per cached key, hundreds per run. The summary at the end shows the count.
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

							// Retroactively mark cached entries — older cache files predate IsChoiceTpk
							// so legacy Choice TPKs default to false; setting it here on touch lets the
							// unrevealed-count predicate include them in this and future passes.
							// Also backfill KeyType so the keyless-eligibility checks work on legacy entries.
							if (existing != null) {
								existing.IsChoiceTpk = true;
								if (string.IsNullOrEmpty(existing.KeyType)) {
									existing.KeyType = result.KeyType;
								}
							}

							if (existing != null && !string.IsNullOrEmpty(existing.RedeemedKeyVal)) {
								// We already have the key — flake, not a real failure.
								// No per-game log: same reasoning as the wasAlreadyRevealed branch above.
								totalAlreadyRevealed++;
								continue;
							}

							ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] CHOICE FAILED: '{result.GameName}' from {result.ChoiceTitle} - {result.Error}");
							totalFailed++;
							orderFailureCount++;

							if (result.Error == "Depleted") {
								depletedGames.Add(result.GameName);
							}

							// Track the failure as an unrevealed placeholder TPK so the
							// retry-timer's "unrevealed" count includes it. If an existing
							// placeholder is already there (RedeemedKeyVal null), it's already
							// counted by the predicate — no new placeholder needed.
							if (existing == null) {
								humbleTpks.Add(new HumbleTpkInfo {
									GameKey = choiceOrder.GameKey,
									HumanName = result.GameName,
									MachineName = result.MachineName,
									KeyType = result.KeyType,
									SteamAppId = 0,
									RedeemedKeyVal = null,
									IsExpired = false,
									SoldOut = false,
									KeyIndex = 0,
									IsGift = false,
									DisallowedCountries = [],
									ExclusiveCountries = [],
									IsChoiceTpk = true
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

		if (depletedGames.Count > 0) {
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Choice keys depleted by Humble ({depletedGames.Count}, will retry): {string.Join(", ", depletedGames)}");
		}

		if (totalCompletedSkipped > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] {totalCompletedSkipped} Choice orders skipped (already complete — delete the bot cache to force re-process)");
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

	/// <summary>
	/// Re-fetches the order for the <em>current</em> Humble Choice month and appends any TPKs not
	/// already present in <paramref name="steamTpks"/>. Humble sometimes adds keys to a month after
	/// the fact (e.g. a late "playtest" key); because a cached month is otherwise never re-fetched,
	/// such additions would never be discovered. The current month is identified by matching a cached
	/// Choice order's <c>choice_url</c> (e.g. <c>may-2026</c>) against the current UTC date. Only that
	/// single order is fetched, so this stays cheap. The merge is purely additive — existing TPK
	/// entries are left untouched so revealed keys and <c>SteamRedeemAttempted</c> are preserved, and
	/// the masked <c>redeemed_key_val</c> from the order JSON never overwrites a real revealed key.
	/// Returns the number of newly-discovered TPKs (which then flow through the normal
	/// compare/redeem path once added to <paramref name="steamTpks"/>).
	/// </summary>
	private static async Task<int> RefreshCurrentChoiceMonthAsync(Bot bot, HumbleBundleWebHandler webHandler, List<ChoiceOrderInfo> choiceOrders, List<string> newGameKeys, List<HumbleTpkInfo> steamTpks) {
		DateTime now = DateTime.UtcNow;
#pragma warning disable CA1308 // Lowercase is required to match Humble's choice_url format
		string monthName = now.ToString("MMMM", CultureInfo.InvariantCulture).ToLowerInvariant();
#pragma warning restore CA1308
		string currentChoiceUrl = $"{monthName}-{now.Year}"; // e.g. "may-2026"

		ChoiceOrderInfo? currentMonth = choiceOrders.FirstOrDefault(c => string.Equals(c.ChoiceUrl, currentChoiceUrl, StringComparison.OrdinalIgnoreCase));

		// No subscription this month, or it isn't cached yet.
		if (currentMonth == null) {
			return 0;
		}

		// A brand-new order was already fetched fresh this run — re-fetching would be redundant.
		if (newGameKeys.Contains(currentMonth.GameKey, StringComparer.OrdinalIgnoreCase)) {
			return 0;
		}

		Dictionary<string, JsonElement>? fetched = await webHandler.GetAllOrdersIndividuallyAsync(new List<string> { currentMonth.GameKey }).ConfigureAwait(false);

		if (fetched == null || !fetched.TryGetValue(currentMonth.GameKey, out JsonElement orderData)) {
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Could not re-fetch current Choice month '{currentMonth.HumanName}' to check for new keys");
			return 0;
		}

		List<HumbleTpkInfo> freshTpks = ExtractSteamTpksFromOrder(bot.BotName, currentMonth.GameKey, orderData);

		// Key existing TPKs for this order by machine_name + keyindex so we only add genuinely new ones.
		HashSet<string> existing = new(
			steamTpks
				.Where(t => t.GameKey.Equals(currentMonth.GameKey, StringComparison.OrdinalIgnoreCase))
				.Select(t => $"{t.MachineName}#{t.KeyIndex}"),
			StringComparer.OrdinalIgnoreCase);

		int added = 0;

		foreach (HumbleTpkInfo tpk in freshTpks) {
			if (existing.Add($"{tpk.MachineName}#{tpk.KeyIndex}")) {
				steamTpks.Add(tpk);
				added++;
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] NEW KEY in current Choice month '{currentMonth.HumanName}': '{tpk.HumanName}' ({tpk.KeyType}, AppID: {tpk.SteamAppId})");
			}
		}

		if (added > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Current Choice month '{currentMonth.HumanName}': discovered {added} newly-added key(s)");
		} else {
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Current Choice month '{currentMonth.HumanName}': no new keys");
		}

		return added;
	}
}
