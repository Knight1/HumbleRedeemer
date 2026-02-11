using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

internal sealed partial class HumbleBundleWebHandler {
	/// <summary>
	/// Redeem a key from HumbleBundle. Returns the Steam key string on success, or null on failure.
	/// When gift is true, returns the gift URL instead.
	/// </summary>
	internal async Task<string?> RedeemKeyAsync(string machineName, string gameKey, int keyIndex, bool gift = false) {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return null;
		}

		try {
			// Build form body matching the TypeScript reference:
			// keytype=${machine_name}&key=${category_id/gamekey}&keyindex=${keyindex}[&gift=true]
			string body = $"keytype={Uri.EscapeDataString(machineName)}&key={Uri.EscapeDataString(gameKey)}&keyindex={keyIndex}";

			if (gift) {
				body += "&gift=true";
			}

			using HttpRequestMessage request = new(HttpMethod.Post, "/humbler/redeemkey") {
				Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
			};

			// Add required headers
			request.Headers.Add("Referer", $"{BaseUrl}/home/library");
			request.Headers.Add("Origin", BaseUrl);

			// Add CSRF prevention token from csrf_cookie if available
			Uri baseUri = new(BaseUrl);
			CookieCollection cookies = CookieContainer.GetCookies(baseUri);

			foreach (Cookie cookie in cookies) {
				if (cookie.Name.Equals("csrf_cookie", StringComparison.OrdinalIgnoreCase)) {
					request.Headers.Add("csrf-prevention-token", cookie.Value);
					break;
				}
			}

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Redeeming key: machineName={machineName}, gameKey={gameKey}, keyIndex={keyIndex}, gift={gift}");

			HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				string errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to redeem key for '{machineName}': {response.StatusCode}");
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Redeem error response: {errorBody[..Math.Min(500, errorBody.Length)]}");
				return null;
			}

			string jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Redeem response: {jsonResponse[..Math.Min(500, jsonResponse.Length)]}");

			// Parse response JSON using ASF's ToJsonObject (reflection-based serialization is disabled)
			JsonElement responseData;

			try {
				responseData = jsonResponse.ToJsonObject<JsonElement>();
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse redeem response JSON");
				return null;
			}

			if (responseData.ValueKind != JsonValueKind.Object) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Unexpected redeem response format");
				return null;
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
				return null;
			}

			if (gift) {
				// Gift mode: extract giftkey and build gift URL
				foreach (JsonProperty prop in responseData.EnumerateObject()) {
					if (prop.Name.Equals("giftkey", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String) {
						string? giftKey = prop.Value.GetString();

						if (!string.IsNullOrEmpty(giftKey)) {
							string giftUrl = $"https://www.humblebundle.com/gift?key={Uri.EscapeDataString(giftKey)}";
							ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Gift URL generated for '{machineName}': {giftUrl}");
							return giftUrl;
						}
					}
				}

				ASF.ArchiLogger.LogGenericError($"[{BotName}] Gift key not found in redeem response for '{machineName}'");
				return null;
			}

			// Normal mode: extract the key string
			foreach (JsonProperty prop in responseData.EnumerateObject()) {
				if (prop.Name.Equals("key", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String) {
					string? key = prop.Value.GetString();

					if (!string.IsNullOrEmpty(key)) {
						ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Successfully redeemed key for '{machineName}'");
						return key;
					}
				}
			}

			ASF.ArchiLogger.LogGenericError($"[{BotName}] Key not found in redeem response for '{machineName}'");
			return null;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to redeem key for '{machineName}'");
			return null;
		}
	}
}
