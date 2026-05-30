using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;
using SteamKit2;

namespace HumbleRedeemer;

#pragma warning disable CA1812 // ASF uses this class during runtime
[Export(typeof(IPlugin))]
[UsedImplicitly]
internal sealed partial class HumbleRedeemer : IBot, IBotModules, IBotSteamClient, IBotConnection, IGitHubPluginUpdates {
	public string Name => nameof(HumbleRedeemer);
	public string RepositoryName => "Knight1/HumbleRedeemer";
	public Version Version => typeof(HumbleRedeemer).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	private static readonly ConcurrentDictionary<Bot, HumbleBundleWebHandler> BotHandlers = new();
	private static readonly ConcurrentDictionary<Bot, string> BotCountryCodes = new();
	private static readonly ConcurrentDictionary<Bot, List<HumbleTpkInfo>> BotHumbleTpks = new();
	private static readonly ConcurrentDictionary<Bot, List<ChoiceOrderInfo>> BotChoiceOrders = new();
	private static readonly ConcurrentDictionary<Bot, bool> BotComparisonDone = new();
	private static readonly ConcurrentDictionary<Bot, HumbleBundleBotCache> BotCaches = new();
	private static readonly ConcurrentDictionary<Bot, System.Threading.Timer> BotRedeemTimers = new();
	private static readonly ConcurrentDictionary<Bot, HumbleBundleBotConfig> BotConfigs = new();

	/// <summary>Game keys paid via AutoPayMonthly this session, used to control reveal behavior.</summary>
	private static readonly ConcurrentDictionary<Bot, HashSet<string>> BotPaidGameKeys = new();

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} plugin loaded!");

		return Task.CompletedTask;
	}

	public async Task OnBotInit(Bot bot) {
		// This is called when a bot is initialized
		ArgumentNullException.ThrowIfNull(bot);

		ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Bot initialized");

		await Task.CompletedTask.ConfigureAwait(false);
	}

	public async Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		ArgumentNullException.ThrowIfNull(bot);

		// Parse bot-specific configuration
		HumbleBundleBotConfig? config = ParseBotConfig(bot.BotName, additionalConfigProperties);

		if (config == null || !config.Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] HumbleBundle integration is disabled");
			// Fanatical runs independently — a bot configured for Fanatical only (HumbleBundleEnabled=false)
			// still needs its init pass.
			await InitFanaticalAsync(bot, additionalConfigProperties).ConfigureAwait(false);
			return;
		}

		// Load bot cache
		string cacheFilePath = Path.Combine(ArchiSteamFarm.SharedInfo.ConfigDirectory, $"HumbleRedeemer-{bot.BotName}.cache");
		HumbleBundleBotCache botCache = await HumbleBundleBotCache.CreateOrLoad(cacheFilePath).ConfigureAwait(false);

		// Create web handler for this bot
		HumbleBundleWebHandler webHandler = new(botCache, bot.BotName, config.BlacklistedGameKeys, config.Proxy);

		// Try to load saved cookies first
		bool cookiesLoaded = await webHandler.LoadCookiesAsync().ConfigureAwait(false);

		if (!cookiesLoaded) {
			// No valid saved session — credentials are required for a fresh login
			if (string.IsNullOrEmpty(config.Username)) {
				ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] HumbleBundle username not configured. Add HumbleBundleUsername to bot config.");
				webHandler.Dispose();
				return;
			}

			string? password = config.Password;

			if (string.IsNullOrEmpty(password)) {
				bool isHeadless = ASF.GlobalConfig?.Headless ?? true;

				if (isHeadless) {
					ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] HumbleBundle credentials not configured. Add HumbleBundleUsername and HumbleBundlePassword to bot config.");
					webHandler.Dispose();
					return;
				}

				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Please enter your HumbleBundle password:");
				password = Console.ReadLine()?.Trim();

				if (string.IsNullOrEmpty(password)) {
					ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] No password entered.");
					webHandler.Dispose();
					return;
				}
			}

			// No valid saved session, perform login
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] No valid HumbleBundle session found, attempting login...");

			bool loginSuccess = await webHandler.LoginAsync(
				config.Username,
				password,
				config.TwoFactorCode,
				bot
			).ConfigureAwait(false);

			if (!loginSuccess) {
				ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] Failed to login to HumbleBundle. Please check your credentials and/or 2FA Code.");
				webHandler.Dispose();

				return;
			}
		} else {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Restored HumbleBundle session from cache");
		}

		// Auto-pay current month before fetching orders so the new gamekey is in the list
		await TryAutoPayCurrentMonthAsync(bot, botCache, webHandler, config).ConfigureAwait(false);

		// Claim all Vault games if configured
		await ClaimAllVaultGamesAsync(bot, botCache, webHandler, config).ConfigureAwait(false);

		// Test API by fetching order keys
		List<string>? orderKeys = await webHandler.GetOrderKeysAsync().ConfigureAwait(false);

		if (orderKeys != null && orderKeys.Count > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Successfully authenticated to HumbleBundle. Found {orderKeys.Count} orders.");

			// Load cached TPK data and determine which orders are new
			HashSet<string> cachedGameKeys = new(botCache.CachedGameKeys, StringComparer.OrdinalIgnoreCase);
			List<HumbleTpkInfo> steamTpks = new(botCache.CachedTpks);
			List<ChoiceOrderInfo> choiceOrders = new(botCache.CachedChoiceOrders);
			HashSet<string> knownChoiceGameKeys = new(choiceOrders.Select(c => c.GameKey), StringComparer.OrdinalIgnoreCase);
			List<string> newGameKeys = orderKeys.Where(key => !cachedGameKeys.Contains(key)).ToList();
			bool choiceCacheUpdated = false;

			if (steamTpks.Count > 0) {
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Loaded {steamTpks.Count} cached Steam TPKs from {cachedGameKeys.Count} orders");
			}

			if (choiceOrders.Count > 0) {
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Loaded {choiceOrders.Count} cached Humble Choice orders");
			}

			// Migration path: cache predates CachedChoiceOrders. Re-fetch cached orders once
			// to populate the Choice metadata so ProcessChoiceOrders has something to act on.
			if (choiceOrders.Count == 0 && cachedGameKeys.Count > 0) {
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Choice metadata missing from cache — re-fetching {cachedGameKeys.Count} cached orders to discover Humble Choice orders (one-time migration)...");
				List<string> alreadyCachedKeys = [.. orderKeys.Where(cachedGameKeys.Contains)];
				Dictionary<string, JsonElement>? rediscovered = await webHandler.GetAllOrdersIndividuallyAsync(alreadyCachedKeys).ConfigureAwait(false);

				if (rediscovered != null) {
					foreach ((string orderKey, JsonElement orderData) in rediscovered) {
						ChoiceOrderInfo? choiceInfo = ExtractChoiceOrderInfo(orderKey, orderData);
						if (choiceInfo != null && knownChoiceGameKeys.Add(choiceInfo.GameKey)) {
							choiceOrders.Add(choiceInfo);
						}
					}
				}

				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Migration complete: discovered {choiceOrders.Count} Humble Choice orders");
				choiceCacheUpdated = choiceOrders.Count > 0;
			}

			if (newGameKeys.Count > 0) {
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Found {newGameKeys.Count} new orders to fetch (out of {orderKeys.Count} total)");

				// Fetch only new orders individually
				Dictionary<string, JsonElement>? newOrders = await webHandler.GetAllOrdersIndividuallyAsync(newGameKeys).ConfigureAwait(false);

				if (newOrders != null && newOrders.Count > 0) {
					int newTpkCount = 0;
					int newChoiceCount = 0;

					foreach ((string orderKey, JsonElement orderData) in newOrders) {
						List<HumbleTpkInfo> orderTpks = ExtractSteamTpksFromOrder(bot.BotName, orderKey, orderData);
						steamTpks.AddRange(orderTpks);
						newTpkCount += orderTpks.Count;

						// Check if this is a Choice order — dedupe against the loaded set so a
						// migration discovery + new-order fetch in the same run can't double-add.
						ChoiceOrderInfo? choiceInfo = ExtractChoiceOrderInfo(orderKey, orderData);
						if (choiceInfo != null && knownChoiceGameKeys.Add(choiceInfo.GameKey)) {
							choiceOrders.Add(choiceInfo);
							newChoiceCount++;
						}
					}

					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Found {newTpkCount} new Steam TPKs from {newOrders.Count} new orders");

					if (newChoiceCount > 0) {
						ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Found {newChoiceCount} new Humble Choice orders");
						choiceCacheUpdated = true;
					}

					// Update cache with all known gamekeys and TPKs
					botCache.CachedGameKeys = new List<string>(orderKeys);
					botCache.CachedTpks = steamTpks;
					await botCache.SaveAsync().ConfigureAwait(false);
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Saved {steamTpks.Count} TPKs and {orderKeys.Count} gamekeys to cache");
				}
			} else {
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] No new orders found, using {steamTpks.Count} cached Steam TPKs");
			}

			// Re-check the CURRENT Choice month for newly-added keys. Humble occasionally appends
			// keys to a month after we've already cached it (e.g. a late "playtest" key), and since
			// a cached month is otherwise never re-fetched those additions would be missed. Only the
			// current month (one order) is re-fetched, so startups stay cheap. New TPKs are added
			// without disturbing existing entries (revealed keys / SteamRedeemAttempted preserved).
			int currentMonthNewTpks = await RefreshCurrentChoiceMonthAsync(bot, webHandler, choiceOrders, newGameKeys, steamTpks).ConfigureAwait(false);

			if (currentMonthNewTpks > 0) {
				botCache.CachedTpks = steamTpks;
				await botCache.SaveAsync().ConfigureAwait(false);
			}

			// Persist Choice metadata so subsequent restarts can call ProcessChoiceOrders
			// even when there are no new orders this session.
			if (choiceCacheUpdated) {
				botCache.CachedChoiceOrders = choiceOrders;
				await botCache.SaveAsync().ConfigureAwait(false);
			}

			// ALWAYS surface the loaded Choice list — not only when new ones were just found —
			// so ProcessChoiceOrders runs on every startup as long as the user has any Choice
			// month at all.
			if (choiceOrders.Count > 0) {
				BotChoiceOrders[bot] = choiceOrders;
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Tracking {choiceOrders.Count} Humble Choice orders for processing");
			}

			if (steamTpks.Count > 0) {
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Total: {steamTpks.Count} Steam TPKs across {orderKeys.Count} orders");
				BotHumbleTpks[bot] = steamTpks;
			}
		}

		// Store the config and cache for this bot
		BotConfigs[bot] = config;
		BotCaches[bot] = botCache;

		// Store the handler for later use
		BotHandlers.TryAdd(bot, webHandler);

		// If enabled, schedule the next monthly Humble Choice release check (first Tuesday at
		// 19:00 Europe/Berlin). No-op when the option is off.
		ScheduleChoiceReleaseCheck(bot);

		await InitFanaticalAsync(bot, additionalConfigProperties).ConfigureAwait(false);
	}

	public async Task OnBotDestroy(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		// Cleanup bot handler and per-bot data
		if (BotHandlers.TryRemove(bot, out HumbleBundleWebHandler? webHandler)) {
			webHandler?.Dispose();
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] HumbleBundle handler disposed");
		}

		if (BotRedeemTimers.TryRemove(bot, out System.Threading.Timer? timer)) {
			await timer.DisposeAsync().ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Redeem retry timer disposed");
		}

		if (BotChoiceReleaseTimers.TryRemove(bot, out System.Threading.Timer? choiceTimer)) {
			await choiceTimer.DisposeAsync().ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Choice release timer disposed");
		}

		if (FanaticalHandlers.TryRemove(bot, out FanaticalWebHandler? fanaticalHandler)) {
			fanaticalHandler.Dispose();
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Fanatical handler disposed");
		}

		if (FanaticalRetryTimers.TryRemove(bot, out System.Threading.Timer? fanaticalTimer)) {
			await fanaticalTimer.DisposeAsync().ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Fanatical retry timer disposed");
		}

		BotCountryCodes.TryRemove(bot, out _);
		BotHumbleTpks.TryRemove(bot, out _);
		BotChoiceOrders.TryRemove(bot, out _);
		BotComparisonDone.TryRemove(bot, out _);
		BotConfigs.TryRemove(bot, out _);
		BotCaches.TryRemove(bot, out _);
		BotPaidGameKeys.TryRemove(bot, out _);
		BotSteamRedeemRateLimitedUntil.TryRemove(bot, out _);
		FanaticalConfigs.TryRemove(bot, out _);
		FanaticalCaches.TryRemove(bot, out _);
		FanaticalKeys.TryRemove(bot, out _);
		FanaticalRevealEmailRequired.TryRemove(bot, out _);

		if (FanaticalProcessLocks.TryRemove(bot, out System.Threading.SemaphoreSlim? fanaticalProcessLock)) {
			fanaticalProcessLock.Dispose();
		}
	}

	public Task OnBotSteamCallbacksInit(Bot bot, CallbackManager callbackManager) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(callbackManager);

		// Register for LoggedOnCallback to capture IPCountryCode
		callbackManager.Subscribe<SteamUser.LoggedOnCallback>(callback => OnSteamLoggedOn(bot, callback));

		// Register for LicenseListCallback to trigger comparison after OwnedPackages is populated
		callbackManager.Subscribe<SteamApps.LicenseListCallback>(callback => OnLicenseList(bot));

		return Task.CompletedTask;
	}

	public Task<IReadOnlyCollection<ClientMsgHandler>?> OnBotSteamHandlersInit(Bot bot) => Task.FromResult<IReadOnlyCollection<ClientMsgHandler>?>(null);

	public Task OnBotDisconnected(Bot bot, EResult reason) {
		ArgumentNullException.ThrowIfNull(bot);

		// Reset comparison flag so it runs again on reconnect
		BotComparisonDone.TryRemove(bot, out _);

		return Task.CompletedTask;
	}

	public Task OnBotLoggedOn(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		return Task.CompletedTask;
	}
}
#pragma warning restore CA1812 // ASF uses this class during runtime
