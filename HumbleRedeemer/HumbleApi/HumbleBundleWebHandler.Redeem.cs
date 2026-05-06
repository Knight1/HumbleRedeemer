using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

/// <summary>
/// Result of a Humble Bundle key redemption attempt. <see cref="Key"/> is non-null on success
/// (the revealed Steam key, or the gift URL when called with <c>gift=true</c>); on failure the
/// <see cref="ErrorType"/> and <see cref="ErrorMessage"/> fields hold the values Humble returned
/// (e.g. <c>error="keys_depleted_email"</c> / <c>error_msg="Keys are temporarily exhausted ..."</c>).
/// </summary>
internal sealed record HumbleRedeemResult(string? Key, string? ErrorType, string? ErrorMessage) {
	internal static readonly HumbleRedeemResult NotLoggedIn = new(null, "not_logged_in", "Not logged in to HumbleBundle");
	internal static readonly HumbleRedeemResult Transport = new(null, "transport", "HTTP request failed");
	internal static readonly HumbleRedeemResult ParseError = new(null, "parse_error", "Could not parse Humble response");
	internal static readonly HumbleRedeemResult MissingKey = new(null, "missing_key", "Key missing from successful response");
	internal static readonly HumbleRedeemResult MissingGiftKey = new(null, "missing_giftkey", "Gift key missing from successful response");
}

internal sealed partial class HumbleBundleWebHandler {
	/// <summary>
	/// Redeem a key from HumbleBundle. Returns a <see cref="HumbleRedeemResult"/> with either
	/// the revealed key (or gift URL when <paramref name="gift"/> is true) or the error type
	/// reported by Humble (e.g. <c>keys_depleted_email</c>) so callers can categorise the
	/// failure precisely.
	/// </summary>
	internal async Task<HumbleRedeemResult> RedeemKeyAsync(string machineName, string gameKey, int keyIndex, bool gift = false) {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return HumbleRedeemResult.NotLoggedIn;
		}

		try {
			// Build form body matching the TypeScript reference:
			// keytype=${machine_name}&key=${category_id/gamekey}&keyindex=${keyindex}[&gift=true]
			string body = $"keytype={Uri.EscapeDataString(machineName)}&key={Uri.EscapeDataString(gameKey)}&keyindex={keyIndex}";

			if (gift) {
				body += "&gift=true";
			}

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Redeeming key: machineName={machineName}, gameKey={gameKey}, keyIndex={keyIndex}, gift={gift}");

			HttpResponseMessage response = await SendAsync(() => {
				HttpRequestMessage req = new(HttpMethod.Post, HumblerRedeemKeyPath) {
					Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
				};

				AddAjaxHeaders(req);

				return req;
			}).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				string errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to redeem key for '{machineName}': {response.StatusCode}");
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Redeem error response: {errorBody[..Math.Min(500, errorBody.Length)]}");
				return new HumbleRedeemResult(null, $"http_{(int) response.StatusCode}", response.StatusCode.ToString());
			}

			string jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Redeem response: {jsonResponse[..Math.Min(500, jsonResponse.Length)]}");

			// Parse response JSON using ASF's ToJsonObject (reflection-based serialization is disabled)
			JsonElement responseData;

			try {
				responseData = jsonResponse.ToJsonObject<JsonElement>();
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse redeem response JSON");
				return HumbleRedeemResult.ParseError;
			}

			if (responseData.ValueKind != JsonValueKind.Object) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Unexpected redeem response format");
				return HumbleRedeemResult.ParseError;
			}

			// Check for success/error fields in the response
			bool? success = null;
			string? errorType = null;
			string? errorMsg = null;

			foreach (JsonProperty prop in responseData.EnumerateObject()) {
				switch (prop.Name) {
					case "success":
						success = prop.Value.ValueKind == JsonValueKind.True;
						break;
					case "error" when prop.Value.ValueKind == JsonValueKind.String:
						errorType = prop.Value.GetString();
						break;
					case "error_msg" when prop.Value.ValueKind == JsonValueKind.String:
						errorMsg = prop.Value.GetString();
						break;
				}
			}

			// Handle explicit failure response
			if (success == false) {
				ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Redeem failed for '{machineName}': {errorType ?? "unknown"} - {errorMsg ?? "no message"}");
				return new HumbleRedeemResult(null, errorType ?? "unknown", errorMsg);
			}

			if (gift) {
				// Gift mode: extract giftkey and build gift URL
				foreach (JsonProperty prop in responseData.EnumerateObject()) {
					if (prop.Name.Equals("giftkey", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String) {
						string? giftKey = prop.Value.GetString();

						if (!string.IsNullOrEmpty(giftKey)) {
							string giftUrl = $"{BaseUrl}{GiftPath}?key={Uri.EscapeDataString(giftKey)}";
							ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Gift URL generated for '{machineName}': {giftUrl}");
							return new HumbleRedeemResult(giftUrl, null, null);
						}
					}
				}

				ASF.ArchiLogger.LogGenericError($"[{BotName}] Gift key not found in redeem response for '{machineName}'");
				return HumbleRedeemResult.MissingGiftKey;
			}

			// Normal mode: extract the key string
			foreach (JsonProperty prop in responseData.EnumerateObject()) {
				if (prop.Name.Equals("key", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String) {
					string? key = prop.Value.GetString();

					if (!string.IsNullOrEmpty(key)) {
						ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Successfully redeemed key for '{machineName}'");
						return new HumbleRedeemResult(key, null, null);
					}
				}
			}

			ASF.ArchiLogger.LogGenericError($"[{BotName}] Key not found in redeem response for '{machineName}'");
			return HumbleRedeemResult.MissingKey;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to redeem key for '{machineName}'");
			return HumbleRedeemResult.Transport;
		}
	}
}
