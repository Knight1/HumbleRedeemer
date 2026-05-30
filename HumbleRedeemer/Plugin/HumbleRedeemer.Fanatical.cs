using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;

namespace HumbleRedeemer;

internal sealed partial class HumbleRedeemer {
	private static readonly ConcurrentDictionary<Bot, FanaticalBotConfig> FanaticalConfigs = new();
	private static readonly ConcurrentDictionary<Bot, FanaticalBotCache> FanaticalCaches = new();
	private static readonly ConcurrentDictionary<Bot, FanaticalWebHandler> FanaticalHandlers = new();
	private static readonly ConcurrentDictionary<Bot, List<FanaticalKeyInfo>> FanaticalKeys = new();
	private static readonly ConcurrentDictionary<Bot, Timer> FanaticalRetryTimers = new();

	// Set once an auto-reveal probe comes back EmailRequired, so we don't trigger a fresh
	// verification email on every retry cycle. Reset only on bot restart (entry removed in OnBotDestroy).
	private static readonly ConcurrentDictionary<Bot, bool> FanaticalRevealEmailRequired = new();

	// Serialises ProcessFanaticalKeys per bot. Steam's LicenseListCallback fires repeatedly during a
	// session — and each successful key redemption adds a license, which re-fires it — so without a
	// gate multiple concurrent passes build their candidate lists before any of them sets
	// SteamRedeemAttempted, and the same revealed key is forwarded to Steam several times
	// (OK/NoDetail, then AlreadyPurchased, AlreadyPurchased...). A re-entrant pass is simply skipped:
	// the in-flight run already covers the current candidates, and the retry timer catches stragglers.
	private static readonly ConcurrentDictionary<Bot, SemaphoreSlim> FanaticalProcessLocks = new();

	/// <summary>
	/// Parses the Fanatical-specific options out of the bot's <c>additionalConfigProperties</c>.
	/// Mirrors <see cref="ParseBotConfig"/> but separated for clarity — Fanatical and Humble are
	/// independent integrations, even though they share a single ASF plugin.
	/// </summary>
	private static FanaticalBotConfig? ParseFanaticalBotConfig(string botName, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties) {
		if (additionalConfigProperties == null) {
			return null;
		}

		FanaticalBotConfig config = new();

		foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
			try {
				switch (configProperty) {
					case "FanaticalEnabled" when configValue.ValueKind == JsonValueKind.True:
						config.Enabled = true;
						break;
					case "FanaticalEnabled" when configValue.ValueKind == JsonValueKind.False:
						config.Enabled = false;
						break;
					case "FanaticalAuthToken" when configValue.ValueKind == JsonValueKind.String:
						config.AuthToken = configValue.GetString();
						break;
					case "FanaticalAnonId" when configValue.ValueKind == JsonValueKind.String:
						config.AnonId = configValue.GetString();
						break;
					case "FanaticalRedeemOnSteam" when configValue.ValueKind == JsonValueKind.True:
						config.RedeemOnSteam = true;
						break;
					case "FanaticalRedeemOnSteam" when configValue.ValueKind == JsonValueKind.False:
						config.RedeemOnSteam = false;
						break;
					case "FanaticalAttemptReveal" when configValue.ValueKind == JsonValueKind.True:
						config.AttemptReveal = true;
						break;
					case "FanaticalAttemptReveal" when configValue.ValueKind == JsonValueKind.False:
						config.AttemptReveal = false;
						break;
					case "FanaticalSteamKeysOnly" when configValue.ValueKind == JsonValueKind.True:
						config.SteamKeysOnly = true;
						break;
					case "FanaticalSteamKeysOnly" when configValue.ValueKind == JsonValueKind.False:
						config.SteamKeysOnly = false;
						break;
					case "FanaticalRedeemRetryIntervalMinutes" when configValue.ValueKind == JsonValueKind.Number:
						if (int.TryParse(configValue.GetRawText(), out int parsedInterval) && parsedInterval > 0) {
							config.RedeemRetryIntervalMinutes = parsedInterval;
						}

						break;
					case "FanaticalAutoRetry" when configValue.ValueKind == JsonValueKind.True:
						config.AutoRetry = true;
						break;
					case "FanaticalAutoRetry" when configValue.ValueKind == JsonValueKind.False:
						config.AutoRetry = false;
						break;
					case "FanaticalBlacklistedOrderIds" when configValue.ValueKind == JsonValueKind.Array:
						foreach (JsonElement item in configValue.EnumerateArray()) {
							if (item.ValueKind == JsonValueKind.String) {
								string? orderId = item.GetString();

								if (!string.IsNullOrEmpty(orderId)) {
									config.BlacklistedOrderIds.Add(orderId);
								}
							}
						}

						break;
					case "FanaticalSkipSteamForSlugs" when configValue.ValueKind == JsonValueKind.Array:
						foreach (JsonElement item in configValue.EnumerateArray()) {
							if (item.ValueKind == JsonValueKind.String) {
								string? slug = item.GetString();

								if (!string.IsNullOrEmpty(slug)) {
									config.SkipSteamForSlugs.Add(slug);
								}
							}
						}

						break;
					case "FanaticalProxy" when configValue.ValueKind == JsonValueKind.String:
						config.Proxy = configValue.GetString();
						break;
				}
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{botName}] Failed to parse Fanatical config property: {configProperty}");
			}
		}

		return config;
	}

	/// <summary>
	/// Walks a Fanatical order JSON and produces one <see cref="FanaticalKeyInfo"/> per game item,
	/// flattening the bundle / pickAndMix / standalone-game shapes into a single list. Items with
	/// type <c>software</c> or status <c>refunded</c> are skipped.
	/// </summary>
	private static List<FanaticalKeyInfo> ExtractKeysFromOrder(string botName, string orderId, JsonElement orderData) {
		List<FanaticalKeyInfo> keys = new();

		try {
			if (orderData.ValueKind != JsonValueKind.Object) {
				return keys;
			}

			string? orderStatus = null;
			JsonElement itemsArray = default;
			bool hasItems = false;

			foreach (JsonProperty prop in orderData.EnumerateObject()) {
				switch (prop.Name) {
					case "status" when prop.Value.ValueKind == JsonValueKind.String:
						orderStatus = prop.Value.GetString();
						break;
					case "items" when prop.Value.ValueKind == JsonValueKind.Array:
						itemsArray = prop.Value;
						hasItems = true;
						break;
				}
			}

			// Only COMPLETE orders carry usable items. Other statuses (PENDING / REFUNDED / etc.)
			// either don't have keys yet or won't ever.
			if (!string.Equals(orderStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase) || !hasItems) {
				return keys;
			}

			foreach (JsonElement item in itemsArray.EnumerateArray()) {
				if (item.ValueKind != JsonValueKind.Object) {
					continue;
				}

				string? itemId = null;
				string? itemType = null;
				string? itemStatus = null;
				JsonElement bundlesArray = default;
				bool hasBundles = false;

				foreach (JsonProperty prop in item.EnumerateObject()) {
					switch (prop.Name) {
						case "_id" when prop.Value.ValueKind == JsonValueKind.String:
							itemId = prop.Value.GetString();
							break;
						case "type" when prop.Value.ValueKind == JsonValueKind.String:
							itemType = prop.Value.GetString();
							break;
						case "status" when prop.Value.ValueKind == JsonValueKind.String:
							itemStatus = prop.Value.GetString();
							break;
						case "bundles" when prop.Value.ValueKind == JsonValueKind.Array:
							bundlesArray = prop.Value;
							hasBundles = true;
							break;
					}
				}

				if (string.Equals(itemStatus, "refunded", StringComparison.OrdinalIgnoreCase)) {
					continue;
				}

				if (string.Equals(itemType, "software", StringComparison.OrdinalIgnoreCase)) {
					continue;
				}

				// Bundle / pickAndMix items wrap the actual games in `bundles[].games[]`. Standalone
				// game items have the game fields directly on the item itself.
				if (hasBundles && bundlesArray.GetArrayLength() > 0) {
					foreach (JsonElement bundle in bundlesArray.EnumerateArray()) {
						if (bundle.ValueKind != JsonValueKind.Object) {
							continue;
						}

						foreach (JsonProperty bundleProp in bundle.EnumerateObject()) {
							if (bundleProp.Name.Equals("games", StringComparison.Ordinal) && bundleProp.Value.ValueKind == JsonValueKind.Array) {
								foreach (JsonElement game in bundleProp.Value.EnumerateArray()) {
									FanaticalKeyInfo? info = ExtractGameItem(game, orderId, itemId);

									if (info != null) {
										keys.Add(info);
									}
								}

								break;
							}
						}
					}
				} else {
					FanaticalKeyInfo? info = ExtractGameItem(item, orderId);

					if (info != null) {
						keys.Add(info);
					}
				}
			}
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{botName}] Failed to parse Fanatical order {orderId}");
		}

		return keys;
	}

	private static FanaticalKeyInfo? ExtractGameItem(JsonElement game, string orderId, string? bundleItemId = null) {
		if (game.ValueKind != JsonValueKind.Object) {
			return null;
		}

		string? id = null;
		string? iid = null;
		string? name = null;
		string? slug = null;
		string? type = null;
		string? status = null;
		string? key = null;
		string? serialId = null;
		bool drmSteam = false;
		bool drmFree = false;
		bool noKeyDelivery = false;
		List<string> drms = new();
		DateTime? serialExpiry = null;

		foreach (JsonProperty prop in game.EnumerateObject()) {
			switch (prop.Name) {
				case "_id" when prop.Value.ValueKind == JsonValueKind.String:
					id = prop.Value.GetString();
					break;
				case "iid" when prop.Value.ValueKind == JsonValueKind.String:
					iid = prop.Value.GetString();
					break;
				case "name" when prop.Value.ValueKind == JsonValueKind.String:
					name = prop.Value.GetString();
					break;
				case "slug" when prop.Value.ValueKind == JsonValueKind.String:
					slug = prop.Value.GetString();
					break;
				case "type" when prop.Value.ValueKind == JsonValueKind.String:
					type = prop.Value.GetString();
					break;
				case "status" when prop.Value.ValueKind == JsonValueKind.String:
					status = prop.Value.GetString();
					break;
				case "key" when prop.Value.ValueKind == JsonValueKind.String:
					key = prop.Value.GetString();
					break;
				case "serialId" when prop.Value.ValueKind == JsonValueKind.String:
					serialId = prop.Value.GetString();
					break;
				case "noKeyDelivery" when prop.Value.ValueKind == JsonValueKind.True:
					noKeyDelivery = true;
					break;
				case "serialExpiry" when prop.Value.ValueKind == JsonValueKind.String:
					if (DateTime.TryParse(prop.Value.GetString(), out DateTime parsed)) {
						serialExpiry = parsed;
					}

					break;
				case "drm" when prop.Value.ValueKind == JsonValueKind.Object:
					foreach (JsonProperty drmProp in prop.Value.EnumerateObject()) {
						if (drmProp.Value.ValueKind == JsonValueKind.True) {
							drms.Add(drmProp.Name);

							if (drmProp.Name.Equals("steam", StringComparison.OrdinalIgnoreCase)) {
								drmSteam = true;
							} else if (drmProp.Name.Equals("drm_free", StringComparison.OrdinalIgnoreCase)) {
								drmFree = true;
							}
						}
					}

					break;
			}
		}

		// We need at least an iid to dedupe items across re-fetches; without one we can't safely
		// merge fresh data into the cache.
		if (string.IsNullOrEmpty(iid)) {
			return null;
		}

		// Items with nothing for us to redeem on Steam must be skipped like refunded items — otherwise
		// their perpetually-empty Key flags the order as "pending" forever, causing endless re-fetches,
		// and inflates the unrevealed counts. Three signals:
		//   - type "software": e-learning courses / non-game products (e.g. Mammoth Interactive
		//     courses redeemed via an external link, not a Steam key). The top-level item loop already
		//     skips standalone software, but software items can also be nested inside a bundle's games.
		//   - noKeyDelivery: Fanatical's explicit flag for file-delivered (download) products.
		//   - drm_free without steam: a DRM-free-only item (comics / books / soundtracks).
		// Items that are both drm_free and steam (a game sold both ways) keep DrmSteam and stay.
		if (string.Equals(type, "software", StringComparison.OrdinalIgnoreCase) || noKeyDelivery || (drmFree && !drmSteam)) {
			return null;
		}

		return new FanaticalKeyInfo {
			OrderId = orderId,
			ItemId = iid,
			InternalId = id ?? "",
			Name = name ?? "Unknown",
			Slug = slug ?? "",
			DrmSteam = drmSteam,
			Drms = string.Join(",", drms),
			Status = status ?? "",
			Key = string.IsNullOrEmpty(key) ? null : key,
			SerialId = string.IsNullOrEmpty(serialId) ? null : serialId,
			// For standalone game items there is no wrapping bundle, so the item is its own bid.
			BundleId = string.IsNullOrEmpty(bundleItemId) ? id ?? "" : bundleItemId,
			SerialExpiry = serialExpiry
		};
	}

	/// <summary>
	/// Initialise Fanatical for this bot: load credentials, validate via refresh-auth, fetch the
	/// order list, fetch any new orders + refresh known orders that still have unrevealed items,
	/// merge into the cache, and persist. Steam-side processing happens later via
	/// <see cref="ProcessFanaticalKeys"/>, gated by license-list arrival.
	/// </summary>
	private static async Task InitFanaticalAsync(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties) {
		FanaticalBotConfig? config = ParseFanaticalBotConfig(bot.BotName, additionalConfigProperties);

		if (config == null || !config.Enabled) {
			return;
		}

		string cachePath = Path.Combine(ArchiSteamFarm.SharedInfo.ConfigDirectory, $"HumbleRedeemer-Fanatical-{bot.BotName}.cache");
		FanaticalBotCache cache = await FanaticalBotCache.CreateOrLoad(cachePath).ConfigureAwait(false);

		FanaticalWebHandler handler = new(cache, bot.BotName, config.BlacklistedOrderIds, config.Proxy);

		bool credentialsLoaded = await handler.LoadCredentialsAsync(config.AuthToken, config.AnonId).ConfigureAwait(false);

		if (!credentialsLoaded) {
			ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Fanatical: no auth token configured. Set FanaticalAuthToken (from JSON.parse(localStorage.bsauth).token in your browser) in the bot config to enable. FanaticalAnonId (from JSON.parse(localStorage.bsanonymous).id) is optional but recommended.");
			handler.Dispose();
			return;
		}

		bool authOk = await handler.RefreshAuthAsync().ConfigureAwait(false);

		if (!authOk) {
			ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] Fanatical: auth token rejected. Re-paste a fresh value of localStorage.bsauth.token into FanaticalAuthToken.");
			handler.Dispose();
			return;
		}

		List<string>? allOrderIds = await handler.GetOrderIdsAsync().ConfigureAwait(false);

		if (allOrderIds == null) {
			ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] Fanatical: could not list orders, aborting init");
			handler.Dispose();
			return;
		}

		HashSet<string> knownIds = new(cache.KnownOrderIds, StringComparer.Ordinal);
		List<string> newIds = allOrderIds.Where(id => !knownIds.Contains(id)).ToList();
		List<FanaticalKeyInfo> mergedKeys = new(cache.CachedKeys);

		// Re-fetch known orders that still have items waiting to be revealed — newly revealed keys
		// will appear in the API response and we want to pick them up without manual restart.
		HashSet<string> ordersWithPending = new(
			mergedKeys
				.Where(k => string.IsNullOrEmpty(k.Key) && !string.Equals(k.Status, "refunded", StringComparison.OrdinalIgnoreCase))
				.Select(k => k.OrderId),
			StringComparer.Ordinal
		);

		List<string> refreshIds = allOrderIds.Where(id => knownIds.Contains(id) && ordersWithPending.Contains(id)).ToList();
		List<string> idsToFetch = newIds.Concat(refreshIds).ToList();

		if (idsToFetch.Count > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical: fetching {newIds.Count} new + {refreshIds.Count} pending-key orders (out of {allOrderIds.Count} total)");

			Dictionary<string, JsonElement> fetched = await handler.GetOrdersAsync(idsToFetch).ConfigureAwait(false);

			foreach ((string orderId, JsonElement orderData) in fetched) {
				List<FanaticalKeyInfo> orderKeys = ExtractKeysFromOrder(bot.BotName, orderId, orderData);
				ReconcileOrderKeys(mergedKeys, orderId, orderKeys);
			}

			cache.KnownOrderIds = new List<string>(allOrderIds);
			cache.CachedKeys = mergedKeys;
			await cache.SaveAsync().ConfigureAwait(false);
		} else {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical: no new or pending-key orders, using {mergedKeys.Count} cached keys");
		}

		FanaticalConfigs[bot] = config;
		FanaticalCaches[bot] = cache;
		FanaticalHandlers[bot] = handler;
		FanaticalKeys[bot] = mergedKeys;

		// Optionally unlock keys via the API before reporting the summary so it reflects what was
		// revealed. Steam forwarding still happens later from OnLicenseList via ProcessFanaticalKeys.
		// interactive: true — this is the startup pass, so prompting for an emailed code is allowed.
		await AttemptFanaticalRevealAsync(bot, true).ConfigureAwait(false);

		LogFanaticalSummary(bot, mergedKeys, config);
	}

	/// <summary>
	/// Reconciles the cache for a single freshly-fetched order: the fresh fetch is authoritative for
	/// the order it came from, so cached, still-unrevealed entries for that order that no longer
	/// appear in the fresh data are pruned. These are items now filtered out as having no key to
	/// reveal (comics / books / DRM-free downloads) that older plugin versions had cached — without
	/// pruning they'd linger forever as phantom "unrevealed" items and keep the order flagged as
	/// pending, causing endless re-fetches. Entries with a revealed <c>Key</c> are never pruned, so a
	/// transient parse hiccup that yields an empty fresh list can't drop already-redeemed keys.
	/// After pruning, <see cref="MergeKeys"/> applies the fresh data.
	/// </summary>
	private static void ReconcileOrderKeys(List<FanaticalKeyInfo> existing, string orderId, List<FanaticalKeyInfo> fresh) {
		HashSet<string> freshItemIds = new(fresh.Select(k => k.ItemId), StringComparer.Ordinal);

		existing.RemoveAll(k =>
			string.Equals(k.OrderId, orderId, StringComparison.Ordinal)
			&& string.IsNullOrEmpty(k.Key)
			&& !freshItemIds.Contains(k.ItemId));

		MergeKeys(existing, fresh);
	}

	/// <summary>
	/// Merges fresh key data into the existing cache list, matching by <c>(OrderId, ItemId)</c>.
	/// Status / key / drm fields are overwritten from the fresh data so newly-revealed keys are
	/// picked up; the <c>SteamRedeemAttempted</c> flag is preserved from the cache.
	/// </summary>
	private static void MergeKeys(List<FanaticalKeyInfo> existing, IReadOnlyCollection<FanaticalKeyInfo> fresh) {
		Dictionary<(string, string), FanaticalKeyInfo> index = existing
			.GroupBy(k => (k.OrderId, k.ItemId))
			.ToDictionary(g => g.Key, g => g.First());

		foreach (FanaticalKeyInfo info in fresh) {
			(string OrderId, string ItemId) compoundKey = (info.OrderId, info.ItemId);

			if (index.TryGetValue(compoundKey, out FanaticalKeyInfo? old)) {
				old.Name = info.Name;
				old.Slug = info.Slug;
				old.DrmSteam = info.DrmSteam;
				old.Drms = info.Drms;
				old.Status = info.Status;
				old.Key = info.Key;
				old.SerialId = info.SerialId;
				old.BundleId = info.BundleId;
				old.SerialExpiry = info.SerialExpiry;
				// SteamRedeemAttempted intentionally preserved from the cached entry
			} else {
				existing.Add(info);
				index[compoundKey] = info;
			}
		}
	}

	private static void LogFanaticalSummary(Bot bot, List<FanaticalKeyInfo> keys, FanaticalBotConfig config) {
		int total = keys.Count;
		int revealed = keys.Count(k => !string.IsNullOrEmpty(k.Key));
		int unrevealed = keys.Count(k => string.IsNullOrEmpty(k.Key) && !string.Equals(k.Status, "refunded", StringComparison.OrdinalIgnoreCase));
		int refunded = keys.Count(k => string.Equals(k.Status, "refunded", StringComparison.OrdinalIgnoreCase));
		int steamRevealed = keys.Count(k => k.DrmSteam && !string.IsNullOrEmpty(k.Key));
		int steamUnrevealed = keys.Count(k => k.DrmSteam && string.IsNullOrEmpty(k.Key));
		int steamPending = keys.Count(k => k.DrmSteam && !string.IsNullOrEmpty(k.Key) && !k.SteamRedeemAttempted);

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] === Fanatical Summary ===");
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Total items: {total} (revealed: {revealed}, unrevealed: {unrevealed}, refunded: {refunded})");
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Steam items: revealed {steamRevealed}, unrevealed {steamUnrevealed}, pending Steam submission {steamPending}");

		if (steamUnrevealed > 0) {
			ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Fanatical: {steamUnrevealed} Steam keys are not yet revealed. Fanatical sends a one-time email code to reveal each key — open the order in your browser and enter the code. Revealed keys persist in the API and will be picked up automatically on the next pass.");
		}

		if (!config.RedeemOnSteam && steamPending > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical: {steamPending} revealed Steam keys would be activated if FanaticalRedeemOnSteam were enabled.");
		}
	}

	/// <summary>
	/// When <c>FanaticalAttemptReveal</c> is enabled, probes Fanatical's reveal endpoint to unlock
	/// keys without manual browser action. Reveals one unrevealed Steam item first to learn whether
	/// an emailed verification code is required:
	/// <list type="bullet">
	///   <item>No code needed — every remaining unrevealed Steam item is revealed the same way.</item>
	///   <item>Code needed and ASF has an attached console (not headless) and this is the interactive
	///     startup pass — the user is prompted for the code Fanatical just emailed, it is exchanged
	///     for an <c>atok</c> via <c>/api/user/atok/code</c>, and every item is then revealed with
	///     that token.</item>
	///   <item>Code needed but headless / non-interactive — stops after the single probe (which
	///     already sent one email) and falls back to manual reveal; not repeated this session.</item>
	/// </list>
	/// Freshly-revealed keys are persisted to the cache for the later Steam-forward pass.
	/// <paramref name="interactive"/> gates the console prompt so background retry-timer passes never
	/// block on <c>Console.ReadLine</c> when nobody is watching.
	/// </summary>
	private static async Task AttemptFanaticalRevealAsync(Bot bot, bool interactive) {
		if (!FanaticalConfigs.TryGetValue(bot, out FanaticalBotConfig? config) || !config.AttemptReveal) {
			return;
		}

		// A previous probe this session already told us an emailed code is required — don't trigger
		// another verification email every retry cycle.
		if (FanaticalRevealEmailRequired.ContainsKey(bot)) {
			return;
		}

		if (!FanaticalHandlers.TryGetValue(bot, out FanaticalWebHandler? handler)) {
			return;
		}

		if (!FanaticalKeys.TryGetValue(bot, out List<FanaticalKeyInfo>? keys) || keys.Count == 0) {
			return;
		}

		if (!FanaticalCaches.TryGetValue(bot, out FanaticalBotCache? cache)) {
			return;
		}

		// Revealable = Steam item, not yet revealed, not refunded, and Fanatical has allocated the
		// ids the reveal endpoint needs (serialId / bundle id / product id / item id).
		List<FanaticalKeyInfo> candidates = keys
			.Where(k => k.DrmSteam
				&& string.IsNullOrEmpty(k.Key)
				&& !string.Equals(k.Status, "refunded", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrEmpty(k.SerialId)
				&& !string.IsNullOrEmpty(k.BundleId)
				&& !string.IsNullOrEmpty(k.InternalId)
				&& !string.IsNullOrEmpty(k.ItemId))
			.ToList();

		if (candidates.Count == 0) {
			return;
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical: attempting to reveal {candidates.Count} unrevealed Steam key(s) via the API...");

		bool cacheUpdated = false;
		int revealed = 0;

		// Step 1: probe the first item with an empty atok to learn whether an emailed code is needed.
		FanaticalKeyInfo first = candidates[0];
		FanaticalRevealResult probe = await handler.RedeemKeyAsync(first.OrderId, first.BundleId, first.InternalId, first.SerialId!, first.ItemId).ConfigureAwait(false);

		// The verification token applied to every reveal below — empty when no code was required.
		string atok = "";

		switch (probe.Outcome) {
			case FanaticalRevealOutcome.Revealed:
				first.Key = probe.Key;
				first.Status = "revealed";
				revealed++;
				cacheUpdated = true;
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical: revealed key for '{first.Name}'");
				break;

			case FanaticalRevealOutcome.EmailRequired:
				bool isHeadless = ASF.GlobalConfig?.Headless ?? true;

				if (isHeadless || !interactive) {
					FanaticalRevealEmailRequired[bot] = true;
					ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Fanatical: reveal requires an emailed verification code (sent for '{first.Name}'). {(isHeadless ? "ASF is headless so it cannot be entered" : "Will not prompt during a background retry pass")} — reveal remaining keys manually in your browser; they will be picked up automatically. Skipping further reveal attempts this session.");
					return;
				}

				// Interactive console attached: ask for the code Fanatical just emailed and exchange
				// it for an atok that authorises the reveals.
				ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Fanatical: reveal requires an emailed verification code (sent for '{first.Name}').");
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Please enter the verification code from the Fanatical email:");

				string? code = Console.ReadLine()?.Trim();

				if (string.IsNullOrEmpty(code)) {
					FanaticalRevealEmailRequired[bot] = true;
					ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] Fanatical: no verification code entered. Skipping further reveal attempts this session.");
					return;
				}

				string? exchanged = await handler.SubmitAtokCodeAsync(code).ConfigureAwait(false);

				if (string.IsNullOrEmpty(exchanged)) {
					FanaticalRevealEmailRequired[bot] = true;
					ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] Fanatical: verification code was rejected. Skipping further reveal attempts this session.");
					return;
				}

				atok = exchanged;

				// Re-run the first reveal, now with the token.
				FanaticalRevealResult retry = await handler.RedeemKeyAsync(first.OrderId, first.BundleId, first.InternalId, first.SerialId!, first.ItemId, atok).ConfigureAwait(false);

				if (retry.Outcome != FanaticalRevealOutcome.Revealed) {
					FanaticalRevealEmailRequired[bot] = true;
					ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] Fanatical: reveal failed even with the verification code. Skipping further reveal attempts this session.");
					return;
				}

				first.Key = retry.Key;
				first.Status = "revealed";
				revealed++;
				cacheUpdated = true;
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical: verification accepted, revealed key for '{first.Name}'");
				break;

			case FanaticalRevealOutcome.Failed:
				// Couldn't even probe — don't guess about the rest; let a later pass retry.
				ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Fanatical: reveal attempt failed for '{first.Name}', skipping reveal this pass");
				return;
		}

		// Step 2: reveal the remaining items with the determined token.
		bool stop = false;

		for (int i = 1; i < candidates.Count && !stop; i++) {
			await Task.Delay(500).ConfigureAwait(false);

			FanaticalKeyInfo info = candidates[i];
			FanaticalRevealResult result = await handler.RedeemKeyAsync(info.OrderId, info.BundleId, info.InternalId, info.SerialId!, info.ItemId, atok).ConfigureAwait(false);

			switch (result.Outcome) {
				case FanaticalRevealOutcome.Revealed:
					info.Key = result.Key;
					info.Status = "revealed";
					revealed++;
					cacheUpdated = true;
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical: revealed key for '{info.Name}'");
					break;
				case FanaticalRevealOutcome.EmailRequired:
					// Unexpected — the token should have carried the whole batch. Stop rather than
					// trigger more emails.
					FanaticalRevealEmailRequired[bot] = true;
					stop = true;
					ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Fanatical: reveal unexpectedly asked for another code at '{info.Name}', stopping. Reveal the rest manually in your browser.");
					break;
				case FanaticalRevealOutcome.Failed:
					ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Fanatical: reveal attempt failed for '{info.Name}', skipping");
					break;
			}
		}

		if (cacheUpdated) {
			cache.CachedKeys = keys;
			await cache.SaveAsync().ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical: revealed {revealed} key(s) via the API");
		}
	}

	/// <summary>
	/// Forwards revealed Fanatical Steam keys to Steam via the shared
	/// <see cref="TryRedeemKeyOnSteamAsync"/>. Skips items already attempted, items with non-Steam
	/// DRM, and items whose slug is in <c>FanaticalSkipSteamForSlugs</c>. Stops the batch on the
	/// first <see cref="SteamRedeemOutcome.RateLimited"/> response — the retry timer picks up the
	/// remainder.
	/// </summary>
	private static async Task ProcessFanaticalKeys(Bot bot) {
		if (!FanaticalConfigs.TryGetValue(bot, out FanaticalBotConfig? config)) {
			return;
		}

		if (!config.RedeemOnSteam) {
			return;
		}

		if (!FanaticalKeys.TryGetValue(bot, out List<FanaticalKeyInfo>? keys) || keys.Count == 0) {
			return;
		}

		if (!FanaticalCaches.TryGetValue(bot, out FanaticalBotCache? cache)) {
			return;
		}

		// Skip if another pass is already running for this bot (a re-fired LicenseListCallback, or an
		// overlapping retry-timer tick). The in-flight pass already covers the current candidates;
		// without this guard concurrent passes re-forward the same key before SteamRedeemAttempted lands.
		SemaphoreSlim processLock = FanaticalProcessLocks.GetOrAdd(bot, static _ => new SemaphoreSlim(1, 1));

		if (!await processLock.WaitAsync(0).ConfigureAwait(false)) {
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Fanatical: Steam forwarding already in progress, skipping this pass");
			return;
		}

		try {
			HashSet<string> skipSlugs = new(config.SkipSteamForSlugs, StringComparer.OrdinalIgnoreCase);

			List<FanaticalKeyInfo> candidates = keys
				.Where(k => k.DrmSteam
					&& !string.IsNullOrEmpty(k.Key)
					&& !k.SteamRedeemAttempted
					&& !string.Equals(k.Status, "refunded", StringComparison.OrdinalIgnoreCase)
					&& !skipSlugs.Contains(k.Slug))
				.ToList();

			if (candidates.Count == 0) {
				return;
			}

			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical: forwarding {candidates.Count} revealed Steam keys to Steam...");

			int redeemed = 0;
			int skippedRateLimit = 0;
			bool rateLimited = false;
			bool cacheUpdated = false;

			foreach (FanaticalKeyInfo info in candidates) {
				if (rateLimited) {
					skippedRateLimit++;
					continue;
				}

				SteamRedeemOutcome outcome = await TryRedeemKeyOnSteamAsync(bot, info.Key!, info.Name, 0).ConfigureAwait(false);

				switch (outcome) {
					case SteamRedeemOutcome.Terminal:
						info.SteamRedeemAttempted = true;
						redeemed++;
						cacheUpdated = true;
						break;
					case SteamRedeemOutcome.RateLimited:
						rateLimited = true;
						skippedRateLimit++;
						break;
				}

				await Task.Delay(500).ConfigureAwait(false);
			}

			string suffix = skippedRateLimit > 0
				? $", {skippedRateLimit} skipped due to Steam rate limit (will retry on next timer cycle)"
				: "";
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical Steam results: {redeemed} forwarded{suffix}");

			if (cacheUpdated) {
				cache.CachedKeys = keys;
				await cache.SaveAsync().ConfigureAwait(false);
			}
		} finally {
			processLock.Release();
		}
	}

	/// <summary>
	/// Schedules the periodic Fanatical retry pass. Disposes any existing timer for the bot first.
	/// </summary>
	private static void StartFanaticalRetryTimer(Bot bot) {
		if (!FanaticalConfigs.TryGetValue(bot, out FanaticalBotConfig? config) || !config.AutoRetry) {
			return;
		}

		if (FanaticalRetryTimers.TryRemove(bot, out Timer? existing)) {
			existing.Dispose();
		}

		TimeSpan interval = TimeSpan.FromMinutes(config.RedeemRetryIntervalMinutes);

		Timer timer = new(
			_ => _ = Task.Run(async () => await RetryFanaticalAsync(bot).ConfigureAwait(false)),
			null,
			interval,
			interval
		);

		FanaticalRetryTimers[bot] = timer;
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical retry timer started (interval: {config.RedeemRetryIntervalMinutes} minutes)");
	}

	/// <summary>
	/// Retry pass: re-list orders, fetch any new ones plus orders that still have pending items
	/// (so newly-revealed keys are discovered), refresh the cache, and re-run Steam submissions
	/// for anything still pending.
	/// </summary>
	private static async Task RetryFanaticalAsync(Bot bot) {
		if (!FanaticalHandlers.TryGetValue(bot, out FanaticalWebHandler? handler)) {
			return;
		}

		if (!FanaticalCaches.TryGetValue(bot, out FanaticalBotCache? cache)) {
			return;
		}

		if (!FanaticalConfigs.TryGetValue(bot, out FanaticalBotConfig? config)) {
			return;
		}

		// Refresh auth opportunistically — cheap and avoids token expiry mid-cycle.
		await handler.RefreshAuthAsync().ConfigureAwait(false);

		List<string>? allOrderIds = await handler.GetOrderIdsAsync().ConfigureAwait(false);

		if (allOrderIds == null) {
			return;
		}

		List<FanaticalKeyInfo> mergedKeys = FanaticalKeys.GetValueOrDefault(bot) ?? new List<FanaticalKeyInfo>(cache.CachedKeys);

		HashSet<string> knownIds = new(cache.KnownOrderIds, StringComparer.Ordinal);
		List<string> newIds = allOrderIds.Where(id => !knownIds.Contains(id)).ToList();

		HashSet<string> ordersWithPending = new(
			mergedKeys
				.Where(k => string.IsNullOrEmpty(k.Key) && !string.Equals(k.Status, "refunded", StringComparison.OrdinalIgnoreCase))
				.Select(k => k.OrderId),
			StringComparer.Ordinal
		);

		List<string> refreshIds = allOrderIds.Where(id => knownIds.Contains(id) && ordersWithPending.Contains(id)).ToList();
		List<string> idsToFetch = newIds.Concat(refreshIds).ToList();

		if (idsToFetch.Count > 0) {
			Dictionary<string, JsonElement> fetched = await handler.GetOrdersAsync(idsToFetch).ConfigureAwait(false);

			foreach ((string orderId, JsonElement orderData) in fetched) {
				List<FanaticalKeyInfo> orderKeys = ExtractKeysFromOrder(bot.BotName, orderId, orderData);
				ReconcileOrderKeys(mergedKeys, orderId, orderKeys);
			}

			cache.KnownOrderIds = new List<string>(allOrderIds);
			cache.CachedKeys = mergedKeys;
			await cache.SaveAsync().ConfigureAwait(false);
			FanaticalKeys[bot] = mergedKeys;
		}

		// Try to unlock any still-locked keys via the API (no-op if disabled or an emailed code was
		// already found to be required this session), then forward whatever is now revealed to Steam.
		// interactive: false — runs on a background timer thread, so never block on console input.
		await AttemptFanaticalRevealAsync(bot, false).ConfigureAwait(false);

		await ProcessFanaticalKeys(bot).ConfigureAwait(false);

		// Stop the timer once everything that *can* be redeemed has been attempted (i.e. there are
		// no revealed-Steam-keys still pending Steam submission). Unrevealed-on-Fanatical items
		// (status: fulfilled) require user email-code action — keep the timer running so we
		// pick them up automatically once the user reveals them.
		bool hasPendingSteam = mergedKeys.Any(k => k.DrmSteam && !string.IsNullOrEmpty(k.Key) && !k.SteamRedeemAttempted);
		bool hasUnrevealed = mergedKeys.Any(k => k.DrmSteam && string.IsNullOrEmpty(k.Key) && !string.Equals(k.Status, "refunded", StringComparison.OrdinalIgnoreCase));

		if (!hasPendingSteam && !hasUnrevealed) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical: nothing left to do, stopping retry timer");

			if (FanaticalRetryTimers.TryRemove(bot, out Timer? timer)) {
				await timer.DisposeAsync().ConfigureAwait(false);
			}
		}
	}
}
