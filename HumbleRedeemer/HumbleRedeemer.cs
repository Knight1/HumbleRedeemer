using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Composition;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Storage;
using JetBrains.Annotations;
using SteamKit2;
using SteamKit2.Internal;

namespace HumbleRedeemer;

/// <summary>
/// Holds information about a Humble Choice order.
/// </summary>
internal sealed class ChoiceOrderInfo {
	[JsonInclude]
	[JsonPropertyName("GameKey")]
	internal string GameKey { get; set; } = "";

	[JsonInclude]
	[JsonPropertyName("ChoiceUrl")]
	internal string ChoiceUrl { get; set; } = "";

	[JsonInclude]
	[JsonPropertyName("HumanName")]
	internal string HumanName { get; set; } = "";

	[JsonConstructor]
	internal ChoiceOrderInfo() { }
}

/// <summary>
/// Holds parsed TPK (third-party key) data from a Humble Bundle order.
/// </summary>
internal sealed class HumbleTpkInfo {
	[JsonInclude]
	[JsonPropertyName("GameKey")]
	internal string GameKey { get; set; } = "";

	[JsonInclude]
	[JsonPropertyName("HumanName")]
	internal string HumanName { get; set; } = "";

	[JsonInclude]
	[JsonPropertyName("MachineName")]
	internal string MachineName { get; set; } = "";

	[JsonInclude]
	[JsonPropertyName("SteamAppId")]
	internal uint SteamAppId { get; set; }

	[JsonInclude]
	[JsonPropertyName("RedeemedKeyVal")]
	internal string? RedeemedKeyVal { get; set; }

	[JsonInclude]
	[JsonPropertyName("IsExpired")]
	internal bool IsExpired { get; set; }

	[JsonInclude]
	[JsonPropertyName("ExpiryDate")]
	internal DateTime? ExpiryDate { get; set; }

	[JsonInclude]
	[JsonPropertyName("SoldOut")]
	internal bool SoldOut { get; set; }

	[JsonInclude]
	[JsonPropertyName("KeyIndex")]
	internal int KeyIndex { get; set; }

	[JsonInclude]
	[JsonPropertyName("IsGift")]
	internal bool IsGift { get; set; }

	[JsonInclude]
	[JsonPropertyName("DisallowedCountries")]
	internal List<string> DisallowedCountries { get; set; } = [];

	[JsonInclude]
	[JsonPropertyName("ExclusiveCountries")]
	internal List<string> ExclusiveCountries { get; set; } = [];

	[JsonConstructor]
	internal HumbleTpkInfo() { }
}

#pragma warning disable CA1812 // ASF uses this class during runtime
[Export(typeof(IPlugin))]
[UsedImplicitly]
internal sealed class HumbleRedeemer : IBot, IBotModules, IBotSteamClient, IBotConnection, IGitHubPluginUpdates {
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
			return;
		}

		if (string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.Password)) {
			ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] HumbleBundle credentials not configured. Add HumbleBundleUsername and HumbleBundlePassword to bot config.");
			return;
		}

		// Load bot cache
		string cacheFilePath = Path.Combine(ArchiSteamFarm.SharedInfo.ConfigDirectory, $"HumbleRedeemer-{bot.BotName}.cache");
		HumbleBundleBotCache botCache = await HumbleBundleBotCache.CreateOrLoad(cacheFilePath).ConfigureAwait(false);

		// Create web handler for this bot
		HumbleBundleWebHandler webHandler = new(botCache, bot.BotName, config.BlacklistedGameKeys);

		// Try to load saved cookies first
		bool cookiesLoaded = await webHandler.LoadCookiesAsync().ConfigureAwait(false);

		if (!cookiesLoaded) {
			// No valid saved session, perform login
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] No valid HumbleBundle session found, attempting login...");

			bool loginSuccess = await webHandler.LoginAsync(
				config.Username,
				config.Password,
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
		await TryAutoPayCurrentMonthAsync(bot, webHandler, config).ConfigureAwait(false);

		// Test API by fetching order keys
		List<string>? orderKeys = await webHandler.GetOrderKeysAsync().ConfigureAwait(false);

		if (orderKeys != null && orderKeys.Count > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Successfully authenticated to HumbleBundle. Found {orderKeys.Count} orders.");

			// Load cached TPK data and determine which orders are new
			HashSet<string> cachedGameKeys = new(botCache.CachedGameKeys, StringComparer.OrdinalIgnoreCase);
			List<HumbleTpkInfo> steamTpks = new(botCache.CachedTpks);
			List<ChoiceOrderInfo> choiceOrders = new();
			List<string> newGameKeys = orderKeys.Where(key => !cachedGameKeys.Contains(key)).ToList();

			if (steamTpks.Count > 0) {
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Loaded {steamTpks.Count} cached Steam TPKs from {cachedGameKeys.Count} orders");
			}

			if (newGameKeys.Count > 0) {
				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Found {newGameKeys.Count} new orders to fetch (out of {orderKeys.Count} total)");

				// Fetch only new orders individually
				Dictionary<string, JsonElement>? newOrders = await webHandler.GetAllOrdersIndividuallyAsync(newGameKeys).ConfigureAwait(false);

				if (newOrders != null && newOrders.Count > 0) {
					int newTpkCount = 0;

					foreach ((string orderKey, JsonElement orderData) in newOrders) {
						List<HumbleTpkInfo> orderTpks = ExtractSteamTpksFromOrder(bot.BotName, orderKey, orderData);
						steamTpks.AddRange(orderTpks);
						newTpkCount += orderTpks.Count;

						// Check if this is a Choice order
						ChoiceOrderInfo? choiceInfo = ExtractChoiceOrderInfo(orderKey, orderData);
						if (choiceInfo != null) {
							choiceOrders.Add(choiceInfo);
						}
					}

					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Found {newTpkCount} new Steam TPKs from {newOrders.Count} new orders");

					if (choiceOrders.Count > 0) {
						ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Found {choiceOrders.Count} Humble Choice orders");
						BotChoiceOrders[bot] = choiceOrders;
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

		BotCountryCodes.TryRemove(bot, out _);
		BotHumbleTpks.TryRemove(bot, out _);
		BotChoiceOrders.TryRemove(bot, out _);
		BotComparisonDone.TryRemove(bot, out _);
		BotConfigs.TryRemove(bot, out _);
		BotCaches.TryRemove(bot, out _);
		BotPaidGameKeys.TryRemove(bot, out _);
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

	private static void OnSteamLoggedOn(Bot bot, SteamUser.LoggedOnCallback callback) {
		if (callback.Result != EResult.OK) {
			return;
		}

		// Store IP country code as fallback
		if (!string.IsNullOrEmpty(callback.IPCountryCode)) {
			BotCountryCodes[bot] = callback.IPCountryCode;
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Steam IP country code (fallback): {callback.IPCountryCode}");
		}

		// Fetch the actual store/wallet country via unified service
		_ = Task.Run(async () => await FetchStoreCountry(bot).ConfigureAwait(false));
	}

	private static async Task FetchStoreCountry(Bot bot) {
		try {
			SteamUnifiedMessages? unifiedMessages = bot.GetHandler<SteamUnifiedMessages>();

			if (unifiedMessages == null) {
				ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] SteamUnifiedMessages handler not available");
				return;
			}

			UserAccount userAccountService = unifiedMessages.CreateService<UserAccount>();

			// Try GetClientWalletDetails first - response contains wallet_country_code and user_country_code
			try {
				CUserAccount_GetClientWalletDetails_Request walletRequest = new();
				SteamUnifiedMessages.ServiceMethodResponse<CUserAccount_GetWalletDetails_Response> walletResponse =
					await userAccountService.GetClientWalletDetails(walletRequest).ToLongRunningTask().ConfigureAwait(false);

				if (walletResponse.Result == EResult.OK) {
					string? walletCountry = walletResponse.Body.wallet_country_code;
					string? userCountry = walletResponse.Body.user_country_code;

					ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Wallet details - wallet_country: {walletCountry ?? "null"}, user_country: {userCountry ?? "null"}");

					string? country = walletCountry ?? userCountry;

					if (!string.IsNullOrEmpty(country)) {
						BotCountryCodes[bot] = country;
						ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Steam wallet/store country: {country}");
						return;
					}
				}
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] GetClientWalletDetails failed: {ex.Message}");
			}

			// Fallback to GetUserCountry
			try {
				CUserAccount_GetUserCountry_Request countryRequest = new();
				SteamUnifiedMessages.ServiceMethodResponse<CUserAccount_GetUserCountry_Response> countryResponse =
					await userAccountService.GetUserCountry(countryRequest).ToLongRunningTask().ConfigureAwait(false);

				if (countryResponse.Result == EResult.OK) {
					string? country = countryResponse.Body.country;

					if (!string.IsNullOrEmpty(country)) {
						BotCountryCodes[bot] = country;
						ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Steam user country: {country}");
						return;
					}
				}
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] GetUserCountry failed: {ex.Message}");
			}

			// If both failed, IP country code fallback is already stored
			BotCountryCodes.TryGetValue(bot, out string? fallback);
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Using IP country code fallback: {fallback ?? "unknown"}");
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{bot.BotName}] Failed to fetch store country");
		}
	}

	private static void OnLicenseList(Bot bot) {
		// Run comparison in background to avoid blocking the callback chain
		_ = Task.Run(async () => {
			// Small delay to ensure ASF finishes processing the license list and populating OwnedPackages
			await Task.Delay(2000).ConfigureAwait(false);
			await CompareHumbleBundleWithSteamLibrary(bot).ConfigureAwait(false);
		});
	}

	private static async Task CompareHumbleBundleWithSteamLibrary(Bot bot) {
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
				ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] NO APP ID: '{gameName}' - cannot check ownership");
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
		// Automatically redeem unrevealed keys that are not owned
		if (availableToRedeem > 0) {
			await RedeemAvailableKeys(bot, humbleTpks, ownedAppIds, countryCode, ignoreStoreLocation, ignoreStoreLocationButRedeem).ConfigureAwait(false);
		}

		// Process Humble Choice orders
		await ProcessChoiceOrders(bot, humbleTpks, ownedAppIds, countryCode, ignoreStoreLocation).ConfigureAwait(false);

		// Start periodic retry timer for keys that couldn't be redeemed (sold out, etc.)
		int remainingUnrevealed = humbleTpks.Count(t =>
			string.IsNullOrEmpty(t.RedeemedKeyVal) && !t.IsExpired && !t.SoldOut && !t.IsGift
			&& t.SteamAppId != 0 && !ownedAppIds.Contains(t.SteamAppId)
			&& IsCountryAllowed(t, countryCode, effectiveIgnoreLocation));

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
		// If SkipUnknownAppIds is true, skip keys without a Steam AppId
		List<(HumbleTpkInfo tpk, bool asGift, bool skipSteam)> toRedeem = new();

		foreach (HumbleTpkInfo tpk in humbleTpks) {
			if (!string.IsNullOrEmpty(tpk.RedeemedKeyVal) || tpk.IsExpired || tpk.SoldOut || tpk.IsGift) {
				continue;
			}

			// Skip if AppId is unknown and the option is enabled
			if (skipUnknownAppIds && tpk.SteamAppId == 0) {
				continue;
			}

			// Skip if current logic requires AppId but it's missing
			if (tpk.SteamAppId == 0) {
				continue;
			}

			// Skip if this came from an auto-paid month and reveal is disabled
			if (payMonthlyButNotReveal && paidGameKeys.Contains(tpk.GameKey)) {
				continue;
			}

			// Skip if AppId is blacklisted
			if (blacklistedAppIds.Contains(tpk.SteamAppId)) {
				continue;
			}

			if (!IsCountryAllowed(tpk, countryCode, effectiveIgnoreLocation)) {
				continue;
			}

			// If RedeemOnlyWithExpiration is enabled, skip keys without an expiration date
			if (redeemOnlyWithExpiration && !tpk.ExpiryDate.HasValue) {
				continue;
			}

			bool isOwned = ownedAppIds.Contains(tpk.SteamAppId);

			// If already owned and not using gift links for owned games, skip
			if (isOwned && !useGiftLinkForOwned) {
				continue;
			}

			// Redeem as gift if already owned and UseGiftLinkForOwned is enabled
			bool redeemAsGift = isOwned && useGiftLinkForOwned;
			// Mark as "not for Steam" if: AppId is in the list, key only passed country check
			// because IgnoreStoreLocationButRedeem is enabled, or it's from an auto-paid month
			// with PayMonthlyRevealButNotToSteam enabled.
			bool isRegionRestricted = !IsCountryAllowed(tpk, countryCode);
			bool skipSteam = redeemButNotToSteamAppIds.Contains(tpk.SteamAppId)
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
		bool cacheUpdated = false;

		foreach ((HumbleTpkInfo tpk, bool asGift, bool skipSteam) in toRedeem) {
			string? key = await webHandler.RedeemKeyAsync(tpk.MachineName, tpk.GameKey, tpk.KeyIndex, asGift).ConfigureAwait(false);

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
			} else {
				ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] FAILED TO REDEEM: '{tpk.HumanName}' (AppID: {tpk.SteamAppId})");
				failed++;
			}

			// Small delay between redemptions to avoid rate limiting
			await Task.Delay(500).ConfigureAwait(false);
		}

		int normalRedeemed = redeemed - redeemedAsGift - revealedNotForSteam;

		if (redeemedAsGift > 0 || revealedNotForSteam > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Redeem results: {normalRedeemed} succeeded, {redeemedAsGift} redeemed as gift links, {revealedNotForSteam} revealed (not for Steam), {failed} failed");
		} else {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Redeem results: {redeemed} succeeded, {failed} failed");
		}

		// Update cache with redeemed keys
		if (cacheUpdated && BotCaches.TryGetValue(bot, out HumbleBundleBotCache? botCache)) {
			botCache.CachedTpks = humbleTpks;
			await botCache.SaveAsync().ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Updated cache with {redeemed} newly redeemed keys");
		}
	}

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
		HashSet<string> paidChoiceKeys = BotPaidGameKeys.TryGetValue(bot, out HashSet<string>? cpgk) ? cpgk : new HashSet<string>();

		int totalRedeemed = 0;
		int totalFailed = 0;
		int totalSkipped = 0;

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
						// Check if we already own this game
						bool alreadyOwned = result.KeyType.Equals("steam", StringComparison.OrdinalIgnoreCase) &&
						                    ownedAppIds.Contains(0); // We don't have AppId from choice result

						HumbleTpkInfo tpk = new() {
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

						// Check if this TPK already exists (by machine name and game key)
						bool alreadyExists = humbleTpks.Any(t =>
							t.MachineName.Equals(tpk.MachineName, StringComparison.OrdinalIgnoreCase) &&
							t.GameKey.Equals(tpk.GameKey, StringComparison.OrdinalIgnoreCase));

						if (!alreadyExists) {
							humbleTpks.Add(tpk);
						}

						if (revealButNotToSteam) {
							ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] CHOICE REVEALED (not for Steam): '{result.GameName}' from {result.ChoiceTitle} => {result.Key}");
						} else {
							ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] CHOICE REDEEMED: '{result.GameName}' from {result.ChoiceTitle} => {result.Key}");
						}

						totalRedeemed++;
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

		// Update cache with redeemed Choice keys
		if (totalRedeemed > 0) {
			botCache.CachedTpks = humbleTpks;
			await botCache.SaveAsync().ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Updated cache with {totalRedeemed} newly redeemed Choice keys");
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

	/// <summary>
	/// If AutoPayMonthly is enabled, find and pay for the current unpaid Humble Choice month.
	/// Stores the resulting gamekey in BotPaidGameKeys so downstream processing can apply
	/// PayMonthlyButNotReveal / PayMonthlyRevealButNotToSteam config flags.
	/// </summary>
	private static async Task TryAutoPayCurrentMonthAsync(Bot bot, HumbleBundleWebHandler webHandler, HumbleBundleBotConfig config) {
		if (!config.AutoPayMonthly) {
			return;
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Checking for unpaid Humble Choice month...");

		UnpaidMonthInfo? unpaidMonth = await webHandler.GetCurrentUnpaidMonthAsync().ConfigureAwait(false);

		if (unpaidMonth == null) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] No unpaid month found (or not a subscriber)");
			return;
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Found unpaid month: {unpaidMonth.HumanName} ({unpaidMonth.MachineName})");

		string? jobId = await webHandler.PayEarlyAsync(unpaidMonth.MachineName, unpaidMonth.ChoiceUrl).ConfigureAwait(false);

		if (string.IsNullOrEmpty(jobId)) {
			ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] Failed to initiate payment for {unpaidMonth.HumanName}");
			return;
		}

		string? gameKey = await webHandler.PollPayEarlyStatusAsync(jobId).ConfigureAwait(false);

		if (string.IsNullOrEmpty(gameKey)) {
			ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] Payment for {unpaidMonth.HumanName} did not complete");
			return;
		}

		string mode = config.PayMonthlyButNotReveal ? "not revealing keys" :
			config.PayMonthlyRevealButNotToSteam ? "revealing keys (not for Steam)" : "revealing keys";

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Successfully paid for {unpaidMonth.HumanName} ({mode}), gamekey: {gameKey}");

		BotPaidGameKeys[bot] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { gameKey };
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

		// Check if there are still unrevealed keys remaining
		int remainingCount = humbleTpks.Count(t =>
			string.IsNullOrEmpty(t.RedeemedKeyVal) && !t.IsExpired && !t.SoldOut && !t.IsGift
			&& t.SteamAppId != 0 && !ownedAppIds.Contains(t.SteamAppId)
			&& IsCountryAllowed(t, countryCode, effectiveIgnoreLocation));

		if (remainingCount == 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] All keys redeemed, stopping retry timer");

			if (BotRedeemTimers.TryRemove(bot, out System.Threading.Timer? timer)) {
				await timer.DisposeAsync().ConfigureAwait(false);
			}
		} else {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] {remainingCount} keys still unredeemed, will retry later");
		}
	}

	private static List<HumbleTpkInfo> ExtractSteamTpksFromOrder(string botName, string orderKey, JsonElement orderData) {
		List<HumbleTpkInfo> tpks = new();

		try {
			if (orderData.ValueKind != JsonValueKind.Object) {
				return tpks;
			}

			// Get tpkd_dict object by enumerating properties
			// (cannot use TryGetProperty - not available in ASF's runtime)
			JsonElement? tpkdDict = null;

			foreach (JsonProperty prop in orderData.EnumerateObject()) {
				if (prop.Name.Equals("tpkd_dict", StringComparison.OrdinalIgnoreCase)) {
					tpkdDict = prop.Value;
					break;
				}
			}

			if (!tpkdDict.HasValue || tpkdDict.Value.ValueKind != JsonValueKind.Object) {
				return tpks;
			}

			// Get all_tpks array from tpkd_dict
			JsonElement? allTpks = null;

			foreach (JsonProperty prop in tpkdDict.Value.EnumerateObject()) {
				if (prop.Name.Equals("all_tpks", StringComparison.OrdinalIgnoreCase)) {
					allTpks = prop.Value;
					break;
				}
			}

			if (!allTpks.HasValue || allTpks.Value.ValueKind != JsonValueKind.Array) {
				return tpks;
			}

			foreach (JsonElement tpk in allTpks.Value.EnumerateArray()) {
				if (tpk.ValueKind != JsonValueKind.Object) {
					continue;
				}

				// Extract all relevant fields by enumerating
				string? keyTypeStr = null;
				string? redeemedKeyVal = null;
				string? humanName = null;
				string? machineName = null;
				uint steamAppId = 0;
				int keyIndex = 0;
				bool isExpired = false;
				DateTime? expiryDate = null;
				bool soldOut = false;
				bool isGift = false;
				List<string> disallowedCountries = new();
				List<string> exclusiveCountries = new();

				foreach (JsonProperty prop in tpk.EnumerateObject()) {
					switch (prop.Name) {
						case "key_type" when prop.Value.ValueKind == JsonValueKind.String:
							keyTypeStr = prop.Value.GetString();
							break;
						case "redeemed_key_val" when prop.Value.ValueKind == JsonValueKind.String:
							redeemedKeyVal = prop.Value.GetString();
							break;
						case "human_name" when prop.Value.ValueKind == JsonValueKind.String:
							humanName = prop.Value.GetString();
							break;
						case "machine_name" when prop.Value.ValueKind == JsonValueKind.String:
							machineName = prop.Value.GetString();
							break;
						case "steam_app_id" when prop.Value.ValueKind == JsonValueKind.Number:
							if (uint.TryParse(prop.Value.GetRawText(), out uint parsedAppId)) {
								steamAppId = parsedAppId;
							}

							break;
						case "keyindex" when prop.Value.ValueKind == JsonValueKind.Number:
							if (int.TryParse(prop.Value.GetRawText(), out int parsedKeyIndex)) {
								keyIndex = parsedKeyIndex;
							}

							break;
						case "is_expired":
							isExpired = prop.Value.ValueKind == JsonValueKind.True;
							break;
						case "expiry_date" when prop.Value.ValueKind == JsonValueKind.String:
							string? expiryStr = prop.Value.GetString();
							if (!string.IsNullOrEmpty(expiryStr) && DateTime.TryParse(expiryStr, out DateTime parsedDate)) {
								expiryDate = parsedDate;
							}

							break;
						case "sold_out":
							soldOut = prop.Value.ValueKind == JsonValueKind.True;
							break;
						case "is_gift":
							isGift = prop.Value.ValueKind == JsonValueKind.True;
							break;
						case "disallowed_countries" when prop.Value.ValueKind == JsonValueKind.Array:
							foreach (JsonElement country in prop.Value.EnumerateArray()) {
								if (country.ValueKind == JsonValueKind.String) {
									string? code = country.GetString();
									if (!string.IsNullOrEmpty(code)) {
										disallowedCountries.Add(code);
									}
								}
							}

							break;
						case "exclusive_countries" when prop.Value.ValueKind == JsonValueKind.Array:
							foreach (JsonElement country in prop.Value.EnumerateArray()) {
								if (country.ValueKind == JsonValueKind.String) {
									string? code = country.GetString();
									if (!string.IsNullOrEmpty(code)) {
										exclusiveCountries.Add(code);
									}
								}
							}

							break;
					}
				}

				// Only process Steam keys
				if (!string.Equals(keyTypeStr, "steam", StringComparison.OrdinalIgnoreCase)) {
					continue;
				}

				tpks.Add(new HumbleTpkInfo {
					GameKey = orderKey,
					HumanName = humanName ?? "Unknown",
					MachineName = machineName ?? "unknown",
					SteamAppId = steamAppId,
					KeyIndex = keyIndex,
					RedeemedKeyVal = redeemedKeyVal,
					IsExpired = isExpired,
					ExpiryDate = expiryDate,
					SoldOut = soldOut,
					IsGift = isGift,
					DisallowedCountries = disallowedCountries,
					ExclusiveCountries = exclusiveCountries
				});
			}
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{botName}] Failed to parse tpkd_dict for order {orderKey}");
		}

		return tpks;
	}

	private static ChoiceOrderInfo? ExtractChoiceOrderInfo(string orderKey, JsonElement orderData) {
		try {
			if (orderData.ValueKind != JsonValueKind.Object) {
				return null;
			}

			// Get product object
			JsonElement? product = null;

			foreach (JsonProperty prop in orderData.EnumerateObject()) {
				if (prop.Name.Equals("product", StringComparison.OrdinalIgnoreCase)) {
					product = prop.Value;
					break;
				}
			}

			if (!product.HasValue || product.Value.ValueKind != JsonValueKind.Object) {
				return null;
			}

			// Check if this is a subscription content order with a choice_url
			string? category = null;
			string? choiceUrl = null;
			string? humanName = null;

			foreach (JsonProperty prop in product.Value.EnumerateObject()) {
				switch (prop.Name) {
					case "category" when prop.Value.ValueKind == JsonValueKind.String:
						category = prop.Value.GetString();
						break;
					case "choice_url" when prop.Value.ValueKind == JsonValueKind.String:
						choiceUrl = prop.Value.GetString();
						break;
					case "human_name" when prop.Value.ValueKind == JsonValueKind.String:
						humanName = prop.Value.GetString();
						break;
				}
			}

			// Only return info if this is a subscription content order with a choice URL
			if (string.Equals(category, "subscriptioncontent", StringComparison.OrdinalIgnoreCase) &&
			    !string.IsNullOrEmpty(choiceUrl)) {
				return new ChoiceOrderInfo {
					GameKey = orderKey,
					ChoiceUrl = choiceUrl,
					HumanName = humanName ?? "Unknown Choice"
				};
			}

			return null;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"Failed to extract choice order info for order {orderKey}");
			return null;
		}
	}

	private static HumbleBundleBotConfig? ParseBotConfig(string botName, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties) {
		if (additionalConfigProperties == null) {
			return null;
		}

		HumbleBundleBotConfig config = new();

		foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
			try {
				switch (configProperty) {
					case "HumbleBundleEnabled" when configValue.ValueKind == JsonValueKind.True:
						config.Enabled = true;

						break;
					case "HumbleBundleEnabled" when configValue.ValueKind == JsonValueKind.False:
						config.Enabled = false;

						break;
					case "HumbleBundleUsername" when configValue.ValueKind == JsonValueKind.String:
						config.Username = configValue.GetString();

						break;
					case "HumbleBundlePassword" when configValue.ValueKind == JsonValueKind.String:
						config.Password = configValue.GetString();

						break;
					case "HumbleBundleTwoFactorCode" when configValue.ValueKind == JsonValueKind.String:
						config.TwoFactorCode = configValue.GetString();

						break;
					case "HumbleBundleRedeemRetryIntervalMinutes" when configValue.ValueKind == JsonValueKind.Number:
						if (int.TryParse(configValue.GetRawText(), out int parsedInterval) && parsedInterval > 0) {
							config.RedeemRetryIntervalMinutes = parsedInterval;
						}

						break;
					case "HumbleBundleIgnoreStoreLocation" when configValue.ValueKind == JsonValueKind.True:
						config.IgnoreStoreLocation = true;

						break;
					case "HumbleBundleIgnoreStoreLocation" when configValue.ValueKind == JsonValueKind.False:
						config.IgnoreStoreLocation = false;

						break;
					case "HumbleBundleAutoRetry" when configValue.ValueKind == JsonValueKind.True:
						config.AutoRetry = true;

						break;
					case "HumbleBundleAutoRetry" when configValue.ValueKind == JsonValueKind.False:
						config.AutoRetry = false;

						break;
					case "HumbleBundleUseGiftLinkForOwned" when configValue.ValueKind == JsonValueKind.True:
						config.UseGiftLinkForOwned = true;

						break;
					case "HumbleBundleUseGiftLinkForOwned" when configValue.ValueKind == JsonValueKind.False:
						config.UseGiftLinkForOwned = false;

						break;
					case "HumbleBundleRedeemOnlyWithExpiration" when configValue.ValueKind == JsonValueKind.True:
						config.RedeemOnlyWithExpiration = true;

						break;
					case "HumbleBundleRedeemOnlyWithExpiration" when configValue.ValueKind == JsonValueKind.False:
						config.RedeemOnlyWithExpiration = false;

						break;
					case "HumbleBundleBlacklistedGameKeys" when configValue.ValueKind == JsonValueKind.Array:
						foreach (JsonElement item in configValue.EnumerateArray()) {
							if (item.ValueKind == JsonValueKind.String) {
								string? gameKey = item.GetString();
								if (!string.IsNullOrEmpty(gameKey)) {
									config.BlacklistedGameKeys.Add(gameKey);
								}
							}
						}

						break;
					case "HumbleBundleBlacklistedAppIds" when configValue.ValueKind == JsonValueKind.Array:
						foreach (JsonElement item in configValue.EnumerateArray()) {
							if (item.ValueKind == JsonValueKind.Number) {
								if (uint.TryParse(item.GetRawText(), out uint appId)) {
									config.BlacklistedAppIds.Add(appId);
								}
							}
						}

						break;
					case "HumbleBundleRedeemButNotToSteamAppIds" when configValue.ValueKind == JsonValueKind.Array:
						foreach (JsonElement item in configValue.EnumerateArray()) {
							if (item.ValueKind == JsonValueKind.Number) {
								if (uint.TryParse(item.GetRawText(), out uint appId)) {
									config.RedeemButNotToSteamAppIds.Add(appId);
								}
							}
						}

						break;
					case "HumbleBundleSkipUnknownAppIds" when configValue.ValueKind == JsonValueKind.True:
						config.SkipUnknownAppIds = true;

						break;
					case "HumbleBundleSkipUnknownAppIds" when configValue.ValueKind == JsonValueKind.False:
						config.SkipUnknownAppIds = false;

						break;
					case "HumbleBundleIgnoreStoreLocationButRedeem" when configValue.ValueKind == JsonValueKind.True:
						config.IgnoreStoreLocationButRedeem = true;

						break;
					case "HumbleBundleIgnoreStoreLocationButRedeem" when configValue.ValueKind == JsonValueKind.False:
						config.IgnoreStoreLocationButRedeem = false;

						break;
					case "HumbleBundleAutoPayMonthly" when configValue.ValueKind == JsonValueKind.True:
						config.AutoPayMonthly = true;

						break;
					case "HumbleBundleAutoPayMonthly" when configValue.ValueKind == JsonValueKind.False:
						config.AutoPayMonthly = false;

						break;
					case "HumbleBundlePayMonthlyButNotReveal" when configValue.ValueKind == JsonValueKind.True:
						config.PayMonthlyButNotReveal = true;

						break;
					case "HumbleBundlePayMonthlyButNotReveal" when configValue.ValueKind == JsonValueKind.False:
						config.PayMonthlyButNotReveal = false;

						break;
					case "HumbleBundlePayMonthlyRevealButNotToSteam" when configValue.ValueKind == JsonValueKind.True:
						config.PayMonthlyRevealButNotToSteam = true;

						break;
					case "HumbleBundlePayMonthlyRevealButNotToSteam" when configValue.ValueKind == JsonValueKind.False:
						config.PayMonthlyRevealButNotToSteam = false;

						break;
				}
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{botName}] Failed to parse HumbleBundle config property: {configProperty}");
			}
		}

		return config;
	}
}
#pragma warning restore CA1812 // ASF uses this class during runtime
