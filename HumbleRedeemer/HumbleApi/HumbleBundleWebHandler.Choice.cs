using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

/// <summary>
/// Data models for Humble Choice
/// </summary>
internal sealed class ChoiceGameTpkd {
	internal string MachineName { get; set; } = "";
	internal string KeyType { get; set; } = "";
	internal string HumanName { get; set; } = "";
	internal bool IsExpired { get; set; }
	internal bool SoldOut { get; set; }
	internal uint SteamAppId { get; set; }
	internal string? RedeemedKeyVal { get; set; }
}

internal sealed class ChoiceGameData {
	internal string Title { get; set; } = "";
	internal List<ChoiceGameTpkd> Tpkds { get; set; } = [];
}

internal sealed class ChoicePageResult {
	internal string GameKey { get; set; } = "";
	internal bool CanRedeemGames { get; set; }
	internal bool ProductIsChoiceless { get; set; }
	internal string Title { get; set; } = "";
	internal List<string> DisplayOrder { get; set; } = [];
	internal Dictionary<string, ChoiceGameData> GameData { get; set; } = [];
	internal HashSet<string> ContentChoicesMade { get; set; } = [];
}

internal sealed class ChoiceRedemptionResult {
	internal string GameName { get; set; } = "";
	internal string MachineName { get; set; } = "";
	internal string KeyType { get; set; } = "";
	internal string? Key { get; set; }
	internal string ChoiceTitle { get; set; } = "";
	internal string? Error { get; set; }
}

internal sealed partial class HumbleBundleWebHandler {
	[GeneratedRegex(@"<script[^>]*id=[""']webpack-monthly-product-data[""'][^>]*>(.*?)</script>", RegexOptions.Singleline)]
	private static partial Regex WebpackMonthlyProductDataRegex();

	/// <summary>
	/// Fetch the Humble Choice page HTML and parse the embedded JSON data
	/// </summary>
	internal async Task<ChoicePageResult?> FetchChoicePageDataAsync(string choiceUrl) {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return null;
		}

		try {
			using HttpRequestMessage request = new(HttpMethod.Get, $"/membership/{choiceUrl}");
			HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to fetch choice page for '{choiceUrl}': {response.StatusCode}");
				return null;
			}

			string html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			// Extract JSON from <script id="webpack-monthly-product-data">
			Match match = WebpackMonthlyProductDataRegex().Match(html);

			if (!match.Success || match.Groups.Count < 2) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Could not find choice data in page for '{choiceUrl}'");
				return null;
			}

			string jsonContent = match.Groups[1].Value;

			// Parse JSON manually (ASF runtime constraint)
			JsonElement pageData;

			try {
				pageData = jsonContent.ToJsonObject<JsonElement>();
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse choice page JSON for '{choiceUrl}'");
				return null;
			}

			return ParseChoicePageData(pageData);
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to fetch choice page for '{choiceUrl}'");
			return null;
		}
	}

	/// <summary>
	/// Parse choice page JSON data manually
	/// </summary>
	private static ChoicePageResult? ParseChoicePageData(JsonElement pageData) {
		if (pageData.ValueKind != JsonValueKind.Object) {
			return null;
		}

		ChoicePageResult result = new();
		JsonElement? contentChoiceOptions = null;

		// Extract top-level fields
		foreach (JsonProperty prop in pageData.EnumerateObject()) {
			switch (prop.Name) {
				case "productIsChoiceless":
					result.ProductIsChoiceless = prop.Value.ValueKind == JsonValueKind.True;
					break;
				case "contentChoiceOptions" when prop.Value.ValueKind == JsonValueKind.Object:
					contentChoiceOptions = prop.Value;
					break;
			}
		}

		if (!contentChoiceOptions.HasValue) {
			return null;
		}

		JsonElement? contentChoiceData = null;
		JsonElement? contentChoicesMade = null;

		// Parse contentChoiceOptions
		foreach (JsonProperty prop in contentChoiceOptions.Value.EnumerateObject()) {
			switch (prop.Name) {
				case "gamekey" when prop.Value.ValueKind == JsonValueKind.String:
					result.GameKey = prop.Value.GetString() ?? "";
					break;
				case "canRedeemGames":
					result.CanRedeemGames = prop.Value.ValueKind == JsonValueKind.True;
					break;
				case "title" when prop.Value.ValueKind == JsonValueKind.String:
					result.Title = prop.Value.GetString() ?? "";
					break;
				case "contentChoiceData" when prop.Value.ValueKind == JsonValueKind.Object:
					contentChoiceData = prop.Value;
					break;
				case "contentChoicesMade" when prop.Value.ValueKind == JsonValueKind.Object:
					contentChoicesMade = prop.Value;
					break;
			}
		}

		// Parse contentChoiceData
		if (contentChoiceData.HasValue) {
			foreach (JsonProperty prop in contentChoiceData.Value.EnumerateObject()) {
				switch (prop.Name) {
					case "display_order" when prop.Value.ValueKind == JsonValueKind.Array:
						foreach (JsonElement id in prop.Value.EnumerateArray()) {
							if (id.ValueKind == JsonValueKind.String) {
								string? idStr = id.GetString();
								if (!string.IsNullOrEmpty(idStr)) {
									result.DisplayOrder.Add(idStr);
								}
							}
						}
						break;
					case "game_data" when prop.Value.ValueKind == JsonValueKind.Object:
						foreach (JsonProperty gameEntry in prop.Value.EnumerateObject()) {
							string gameId = gameEntry.Name;
							if (gameEntry.Value.ValueKind == JsonValueKind.Object) {
								ChoiceGameData? gameData = ParseChoiceGameData(gameEntry.Value);
								if (gameData != null) {
									result.GameData[gameId] = gameData;
								}
							}
						}
						break;
				}
			}
		}

		// Parse contentChoicesMade
		if (contentChoicesMade.HasValue) {
			foreach (JsonProperty parentEntry in contentChoicesMade.Value.EnumerateObject()) {
				if (parentEntry.Value.ValueKind == JsonValueKind.Object) {
					foreach (JsonProperty prop in parentEntry.Value.EnumerateObject()) {
						if (prop.Name == "choices_made" && prop.Value.ValueKind == JsonValueKind.Array) {
							foreach (JsonElement choiceId in prop.Value.EnumerateArray()) {
								if (choiceId.ValueKind == JsonValueKind.String) {
									string? idStr = choiceId.GetString();
									if (!string.IsNullOrEmpty(idStr)) {
										result.ContentChoicesMade.Add(idStr);
									}
								}
							}
						}
					}
				}
			}
		}

		return result;
	}

	/// <summary>
	/// Parse a single game's data from choice page
	/// </summary>
	private static ChoiceGameData? ParseChoiceGameData(JsonElement gameDataElement) {
		if (gameDataElement.ValueKind != JsonValueKind.Object) {
			return null;
		}

		ChoiceGameData gameData = new();

		foreach (JsonProperty prop in gameDataElement.EnumerateObject()) {
			switch (prop.Name) {
				case "title" when prop.Value.ValueKind == JsonValueKind.String:
					gameData.Title = prop.Value.GetString() ?? "";
					break;
				case "tpkds" when prop.Value.ValueKind == JsonValueKind.Array:
					foreach (JsonElement tpkdElement in prop.Value.EnumerateArray()) {
						if (tpkdElement.ValueKind == JsonValueKind.Object) {
							ChoiceGameTpkd? tpkd = ParseChoiceGameTpkd(tpkdElement);
							if (tpkd != null) {
								gameData.Tpkds.Add(tpkd);
							}
						}
					}
					break;
			}
		}

		return gameData;
	}

	/// <summary>
	/// Parse a single TPKD from choice page
	/// </summary>
	private static ChoiceGameTpkd? ParseChoiceGameTpkd(JsonElement tpkdElement) {
		if (tpkdElement.ValueKind != JsonValueKind.Object) {
			return null;
		}

		ChoiceGameTpkd tpkd = new();

		foreach (JsonProperty prop in tpkdElement.EnumerateObject()) {
			switch (prop.Name) {
				case "machine_name" when prop.Value.ValueKind == JsonValueKind.String:
					tpkd.MachineName = prop.Value.GetString() ?? "";
					break;
				case "key_type" when prop.Value.ValueKind == JsonValueKind.String:
					tpkd.KeyType = prop.Value.GetString() ?? "";
					break;
				case "human_name" when prop.Value.ValueKind == JsonValueKind.String:
					tpkd.HumanName = prop.Value.GetString() ?? "";
					break;
				case "is_expired":
					tpkd.IsExpired = prop.Value.ValueKind == JsonValueKind.True;
					break;
				case "sold_out":
					tpkd.SoldOut = prop.Value.ValueKind == JsonValueKind.True;
					break;
				case "steam_app_id" when prop.Value.ValueKind == JsonValueKind.Number:
					if (uint.TryParse(prop.Value.GetRawText(), out uint appId)) {
						tpkd.SteamAppId = appId;
					}
					break;
				case "redeemed_key_val" when prop.Value.ValueKind == JsonValueKind.String:
					tpkd.RedeemedKeyVal = prop.Value.GetString();
					break;
			}
		}

		return tpkd;
	}

	/// <summary>
	/// POST to /humbler/choosecontent to select games for redemption
	/// </summary>
	internal async Task<bool> ChooseContentAsync(string gameKey, string parentIdentifier, List<string> identifiers) {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return false;
		}

		if (identifiers.Count == 0) {
			return true; // Nothing to choose
		}

		try {
			// Build form body
			string body = $"gamekey={Uri.EscapeDataString(gameKey)}&parent_identifier={Uri.EscapeDataString(parentIdentifier)}";

			foreach (string id in identifiers) {
				body += $"&chosen_identifiers[]={Uri.EscapeDataString(id)}";
			}

			using HttpRequestMessage request = new(HttpMethod.Post, "/humbler/choosecontent") {
				Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
			};

			// Add required headers
			request.Headers.Add("Referer", $"{BaseUrl}/home/library");
			request.Headers.Add("Origin", BaseUrl);

			// Add CSRF prevention token
			Uri baseUri = new(BaseUrl);
			CookieCollection cookies = CookieContainer.GetCookies(baseUri);

			foreach (Cookie cookie in cookies) {
				if (cookie.Name.Equals("csrf_cookie", StringComparison.OrdinalIgnoreCase)) {
					request.Headers.Add("csrf-prevention-token", cookie.Value);
					break;
				}
			}

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Choosing content: gamekey={gameKey}, parent={parentIdentifier}, identifiers={string.Join(", ", identifiers)}");

			HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				string errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to choose content: {response.StatusCode}");
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Choose content error response: {errorBody[..Math.Min(500, errorBody.Length)]}");
				return false;
			}

			string jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			// Parse response
			JsonElement responseData;

			try {
				responseData = jsonResponse.ToJsonObject<JsonElement>();
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse choose content response");
				return false;
			}

			if (responseData.ValueKind != JsonValueKind.Object) {
				return false;
			}

			bool? success = null;
			bool hasDummyError = false;

			foreach (JsonProperty prop in responseData.EnumerateObject()) {
				switch (prop.Name) {
					case "success":
						success = prop.Value.ValueKind == JsonValueKind.True;
						break;
					case "errors" when prop.Value.ValueKind == JsonValueKind.Object:
						// Check for "dummy" error (already made this choice)
						foreach (JsonProperty errorProp in prop.Value.EnumerateObject()) {
							if (errorProp.Name == "dummy") {
								hasDummyError = true;
								break;
							}
						}
						break;
				}
			}

			// Success or "already made this choice" are both OK
			if (success == true || hasDummyError) {
				ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Successfully chose {identifiers.Count} games");
				return true;
			}

			ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Choose content failed: {jsonResponse[..Math.Min(200, jsonResponse.Length)]}");
			return false;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to choose content");
			return false;
		}
	}

	/// <summary>
	/// Process a single Humble Choice order: choose all games and redeem their keys
	/// </summary>
	internal async Task<List<ChoiceRedemptionResult>> ProcessChoiceOrderAsync(string gameKey, string choiceUrl, string humanName) {
		List<ChoiceRedemptionResult> results = [];

		ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Processing Choice: {humanName}");

		ChoicePageResult? pageData = await FetchChoicePageDataAsync(choiceUrl).ConfigureAwait(false);

		if (pageData == null) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to fetch choice page data for '{humanName}'");
			return results;
		}

		if (!pageData.ProductIsChoiceless) {
			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Skipping '{humanName}' (old-style choice, not supported)");
			return results;
		}

		if (!pageData.CanRedeemGames) {
			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Skipping '{humanName}' (cannot redeem games)");
			return results;
		}

		// Filter game IDs to only those with redeemable keys
		List<string> gameIds = pageData.DisplayOrder
			.Where(id => pageData.GameData.ContainsKey(id) && pageData.GameData[id].Tpkds.Count > 0)
			.ToList();

		if (gameIds.Count == 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] No games to redeem for '{humanName}'");
			return results;
		}

		// Determine which games need to be chosen (not already chosen and have redeemable keys)
		List<string> unchosenIds = gameIds
			.Where(id => !pageData.ContentChoicesMade.Contains(id))
			.Where(id => pageData.GameData[id].Tpkds.Any(t =>
				!t.KeyType.EndsWith("_keyless", StringComparison.OrdinalIgnoreCase) &&
				string.IsNullOrEmpty(t.RedeemedKeyVal) &&
				!t.IsExpired &&
				!t.SoldOut))
			.ToList();

		// Choose content for unchosen games
		if (unchosenIds.Count > 0) {
			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Choosing {unchosenIds.Count} games for '{humanName}'");
			bool chooseSuccess = await ChooseContentAsync(pageData.GameKey, "initial", unchosenIds).ConfigureAwait(false);

			if (!chooseSuccess) {
				ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Failed to choose content for '{humanName}', will still attempt redemption");
			}
		}

		// Redeem each key
		foreach (string gameId in gameIds) {
			if (!pageData.GameData.TryGetValue(gameId, out ChoiceGameData? game)) {
				continue;
			}

			foreach (ChoiceGameTpkd tpkd in game.Tpkds) {
				// Skip if already redeemed
				if (!string.IsNullOrEmpty(tpkd.RedeemedKeyVal)) {
					results.Add(new ChoiceRedemptionResult {
						GameName = game.Title,
						MachineName = tpkd.MachineName,
						KeyType = tpkd.KeyType,
						Key = tpkd.RedeemedKeyVal,
						ChoiceTitle = pageData.Title
					});
					continue;
				}

				// Skip keyless
				if (tpkd.KeyType.EndsWith("_keyless", StringComparison.OrdinalIgnoreCase)) {
					continue;
				}

				// Skip expired/sold out
				if (tpkd.IsExpired || tpkd.SoldOut) {
					results.Add(new ChoiceRedemptionResult {
						GameName = game.Title,
						MachineName = tpkd.MachineName,
						KeyType = tpkd.KeyType,
						ChoiceTitle = pageData.Title,
						Error = tpkd.IsExpired ? "Expired" : "Sold out"
					});
					continue;
				}

				// Redeem the key
				ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Redeeming Choice game: {game.Title}");
				string? key = await RedeemKeyAsync(tpkd.MachineName, pageData.GameKey, 0, false).ConfigureAwait(false);

				results.Add(new ChoiceRedemptionResult {
					GameName = game.Title,
					MachineName = tpkd.MachineName,
					KeyType = tpkd.KeyType,
					Key = key,
					ChoiceTitle = pageData.Title,
					Error = string.IsNullOrEmpty(key) ? "Redemption failed" : null
				});

				// Small delay between redemptions
				await Task.Delay(500).ConfigureAwait(false);
			}
		}

		return results;
	}
}
