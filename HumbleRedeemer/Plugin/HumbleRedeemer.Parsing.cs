using System;
using System.Collections.Generic;
using System.Text.Json;
using ArchiSteamFarm.Core;

namespace HumbleRedeemer;

internal sealed partial class HumbleRedeemer {
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

				// Accept Steam keys plus the four keyless platforms we know how to claim
				// (epic_keyless / gog_keyless / blizzard_keyless / origin_keyless). Other
				// types (origin, uplay, blizzard non-keyless, generic vouchers, etc.) are
				// dropped because we have no redemption flow for them.
				bool isAcceptedType = string.Equals(keyTypeStr, "steam", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(keyTypeStr, "epic_keyless", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(keyTypeStr, "gog_keyless", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(keyTypeStr, "blizzard_keyless", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(keyTypeStr, "origin_keyless", StringComparison.OrdinalIgnoreCase);

				if (!isAcceptedType) {
					continue;
				}

				tpks.Add(new HumbleTpkInfo {
					GameKey = orderKey,
					HumanName = humanName ?? "Unknown",
					MachineName = machineName ?? "unknown",
					KeyType = keyTypeStr ?? "",
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
					case "HumbleBundleProxy" when configValue.ValueKind == JsonValueKind.String:
						config.Proxy = configValue.GetString();

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
					case "HumbleBundleClaimVaultGames" when configValue.ValueKind == JsonValueKind.True:
						config.ClaimVaultGames = true;

						break;
					case "HumbleBundleClaimVaultGames" when configValue.ValueKind == JsonValueKind.False:
						config.ClaimVaultGames = false;

						break;
					case "HumbleBundleRedeemOnSteam" when configValue.ValueKind == JsonValueKind.True:
						config.RedeemOnSteam = true;

						break;
					case "HumbleBundleRedeemOnSteam" when configValue.ValueKind == JsonValueKind.False:
						config.RedeemOnSteam = false;

						break;
					case "HumbleBundleScheduleChoiceCheck" when configValue.ValueKind == JsonValueKind.True:
						config.ScheduleChoiceCheck = true;

						break;
					case "HumbleBundleScheduleChoiceCheck" when configValue.ValueKind == JsonValueKind.False:
						config.ScheduleChoiceCheck = false;

						break;
					case "HumbleBundleRedeemEpicKeyless" when configValue.ValueKind == JsonValueKind.True:
						config.RedeemEpicKeyless = true;

						break;
					case "HumbleBundleRedeemEpicKeyless" when configValue.ValueKind == JsonValueKind.False:
						config.RedeemEpicKeyless = false;

						break;
					case "HumbleBundleRedeemGogKeyless" when configValue.ValueKind == JsonValueKind.True:
						config.RedeemGogKeyless = true;

						break;
					case "HumbleBundleRedeemGogKeyless" when configValue.ValueKind == JsonValueKind.False:
						config.RedeemGogKeyless = false;

						break;
					case "HumbleBundleRedeemBlizzardKeyless" when configValue.ValueKind == JsonValueKind.True:
						config.RedeemBlizzardKeyless = true;

						break;
					case "HumbleBundleRedeemBlizzardKeyless" when configValue.ValueKind == JsonValueKind.False:
						config.RedeemBlizzardKeyless = false;

						break;
					case "HumbleBundleRedeemOriginKeyless" when configValue.ValueKind == JsonValueKind.True:
						config.RedeemOriginKeyless = true;

						break;
					case "HumbleBundleRedeemOriginKeyless" when configValue.ValueKind == JsonValueKind.False:
						config.RedeemOriginKeyless = false;

						break;
				}
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{botName}] Failed to parse HumbleBundle config property: {configProperty}");
			}
		}

		return config;
	}
}
