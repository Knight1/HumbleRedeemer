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

			// Normal mode: extract the key. Direct (Steam/etc.) redemptions return a plain string.
			// Keyless platform claims return a nested object instead — e.g. gog_keyless responds
			// {"key":{"gogUsername":"Knight24","key":"https://www.gog.com/order/status/..."},"success":true}
			// where the inner `key` is a GOG order-status URL, not a redeemable key, and `success:true`
			// already means the game was claimed on the linked account. Both shapes are success — only a
			// genuinely absent/empty key is an error.
			foreach (JsonProperty prop in responseData.EnumerateObject()) {
				if (!prop.Name.Equals("key", StringComparison.OrdinalIgnoreCase)) {
					continue;
				}

				if (prop.Value.ValueKind == JsonValueKind.String) {
					string? key = prop.Value.GetString();

					if (!string.IsNullOrEmpty(key)) {
						ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Successfully redeemed key for '{machineName}'");
						return new HumbleRedeemResult(key, null, null);
					}
				} else if (prop.Value.ValueKind == JsonValueKind.Object) {
					// Keyless claim: pull out the confirmation URL (and account name, when present) for the log.
					string? confirmationUrl = null;
					string? account = null;

					foreach (JsonProperty inner in prop.Value.EnumerateObject()) {
						switch (inner.Name) {
							case "key" when inner.Value.ValueKind == JsonValueKind.String:
								confirmationUrl = inner.Value.GetString();
								break;
							case "gogUsername" when inner.Value.ValueKind == JsonValueKind.String:
								account = inner.Value.GetString();
								break;
						}
					}

					if (!string.IsNullOrEmpty(confirmationUrl)) {
						ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Keyless claim confirmed for '{machineName}'{(account != null ? $" (account: {account})" : "")}: {confirmationUrl}");
						return new HumbleRedeemResult(confirmationUrl, null, null);
					}

					// Object form but no inner URL — still a successful claim (success:true). Let the
					// caller substitute its synthetic keyless confirmation via the missing_key path.
					ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Keyless claim confirmed for '{machineName}' (no confirmation URL returned)");
					return HumbleRedeemResult.MissingKey;
				}
			}

			// success:true but no usable key at all — keyless claims legitimately hit this; the caller
			// recognises missing_key + keyless and substitutes a confirmation, so log at debug not error.
			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] No key string in redeem response for '{machineName}' (treated as keyless claim if applicable)");
			return HumbleRedeemResult.MissingKey;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to redeem key for '{machineName}'");
			return HumbleRedeemResult.Transport;
		}
	}
}
