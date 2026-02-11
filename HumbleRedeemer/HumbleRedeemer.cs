using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Composition;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// Holds parsed TPK (third-party key) data from a Humble Bundle order.
/// </summary>
internal sealed class HumbleTpkInfo {
	internal required string HumanName { get; init; }
	internal required string MachineName { get; init; }
	internal uint SteamAppId { get; init; }
	internal string? RedeemedKeyVal { get; init; }
	internal bool IsExpired { get; init; }
	internal bool SoldOut { get; init; }
	internal IReadOnlyList<string> DisallowedCountries { get; init; } = [];
	internal IReadOnlyList<string> ExclusiveCountries { get; init; } = [];
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
	private static readonly ConcurrentDictionary<Bot, bool> BotComparisonDone = new();

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
		HumbleBundleWebHandler webHandler = new(botCache, bot.BotName);

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

		// Test API by fetching order keys
		List<string>? orderKeys = await webHandler.GetOrderKeysAsync().ConfigureAwait(false);

		if (orderKeys != null && orderKeys.Count > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Successfully authenticated to HumbleBundle. Found {orderKeys.Count} orders.");

			// Fetch all orders individually (reliable, but slower than bulk API)
			Dictionary<string, JsonElement>? allOrders = await webHandler.GetAllOrdersIndividuallyAsync(orderKeys).ConfigureAwait(false);

			if (allOrders != null && allOrders.Count > 0) {
				// Extract Steam TPK data from tpkd_dict.all_tpks
				List<HumbleTpkInfo> steamTpks = new();
				int ordersWithKeys = 0;

				foreach ((string orderKey, JsonElement orderData) in allOrders) {
					try {
						if (orderData.ValueKind != JsonValueKind.Object) {
							continue;
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
							continue;
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
							continue;
						}

						bool orderHasKeys = false;

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
							bool isExpired = false;
							bool soldOut = false;
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
									case "is_expired":
										isExpired = prop.Value.ValueKind == JsonValueKind.True;
										break;
									case "sold_out":
										soldOut = prop.Value.ValueKind == JsonValueKind.True;
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

							steamTpks.Add(new HumbleTpkInfo {
								HumanName = humanName ?? "Unknown",
								MachineName = machineName ?? "unknown",
								SteamAppId = steamAppId,
								RedeemedKeyVal = redeemedKeyVal,
								IsExpired = isExpired,
								SoldOut = soldOut,
								DisallowedCountries = disallowedCountries,
								ExclusiveCountries = exclusiveCountries
							});

							orderHasKeys = true;
						}

						if (orderHasKeys) {
							ordersWithKeys++;
						}
					} catch (Exception ex) {
						ASF.ArchiLogger.LogGenericException(ex, $"[{bot.BotName}] Failed to parse tpkd_dict for order {orderKey}");
					}
				}

				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Found {steamTpks.Count} Steam TPKs across {ordersWithKeys} orders (out of {allOrders.Count} fetched)");

				// Store TPK data for comparison when Steam login completes
				BotHumbleTpks[bot] = steamTpks;
			}
		}

		// Store the handler for later use
		BotHandlers.TryAdd(bot, webHandler);
	}

	public Task OnBotDestroy(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		// Cleanup bot handler and per-bot data
		if (BotHandlers.TryRemove(bot, out HumbleBundleWebHandler? webHandler)) {
			webHandler?.Dispose();
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] HumbleBundle handler disposed");
		}

		BotCountryCodes.TryRemove(bot, out _);
		BotHumbleTpks.TryRemove(bot, out _);
		BotComparisonDone.TryRemove(bot, out _);

		return Task.CompletedTask;
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

			// Check country restrictions
			if (!string.IsNullOrEmpty(countryCode)) {
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
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] === Keys will NOT be redeemed automatically ===");

		await Task.CompletedTask.ConfigureAwait(false);
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
				}
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{botName}] Failed to parse HumbleBundle config property: {configProperty}");
			}
		}

		return config;
	}
}
#pragma warning restore CA1812 // ASF uses this class during runtime
