using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Composition;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;

namespace HumbleRedeemer;

#pragma warning disable CA1812 // ASF uses this class during runtime
[Export(typeof(IPlugin))]
[UsedImplicitly]
internal sealed class HumbleRedeemer : IBot, IBotModules, IGitHubPluginUpdates {
	public string Name => nameof(HumbleRedeemer);
	public string RepositoryName => "Knight1/HumbleRedeemer";
	public Version Version => typeof(HumbleRedeemer).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	private static readonly ConcurrentDictionary<Bot, HumbleBundleWebHandler> BotHandlers = new();

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
				// Extract Steam keys from tpkd_dict.all_tpks
				List<string> steamKeys = new();
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

							// Extract key_type, redeemed_key_val, and human_name by enumerating
							string? keyTypeStr = null;
							string? redeemedKeyVal = null;
							string? humanName = null;

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
								}
							}

							// Only process Steam keys
							if (!string.Equals(keyTypeStr, "steam", StringComparison.OrdinalIgnoreCase)) {
								continue;
							}

							if (string.IsNullOrEmpty(redeemedKeyVal)) {
								continue;
							}

							steamKeys.Add(redeemedKeyVal);
							orderHasKeys = true;

							ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Found Steam key for '{humanName ?? "Unknown"}': {redeemedKeyVal}");
						}

						if (orderHasKeys) {
							ordersWithKeys++;
						}
					} catch (Exception ex) {
						ASF.ArchiLogger.LogGenericException(ex, $"[{bot.BotName}] Failed to parse tpkd_dict for order {orderKey}");
					}
				}

				ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Found {steamKeys.Count} Steam keys across {ordersWithKeys} orders (out of {allOrders.Count} fetched)");

				// TODO: Redeem Steam keys via ASF bot
			}
		}

		// Store the handler for later use
		BotHandlers.TryAdd(bot, webHandler);
	}

	public Task OnBotDestroy(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		// Cleanup bot handler
		if (BotHandlers.TryRemove(bot, out HumbleBundleWebHandler? webHandler)) {
			webHandler?.Dispose();
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] HumbleBundle handler disposed");
		}

		return Task.CompletedTask;
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
