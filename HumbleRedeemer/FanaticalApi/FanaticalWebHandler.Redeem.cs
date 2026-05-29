using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

/// <summary>
/// Outcome of a Fanatical reveal (<c>/api/user/orders/redeem</c>) attempt.
/// </summary>
internal enum FanaticalRevealOutcome {
	/// <summary>HTTP / transport / parse failure — nothing usable came back.</summary>
	Failed,

	/// <summary>
	/// Fanatical wants an emailed verification code (<c>{"key":"email"}</c>). The plugin cannot
	/// intercept that email, so this is as far as automatic reveal can go for this session.
	/// </summary>
	EmailRequired,

	/// <summary>The reveal succeeded and the response carried the actual key string.</summary>
	Revealed
}

/// <summary>
/// Result of a Fanatical reveal attempt. <see cref="Key"/> is non-null only when
/// <see cref="Outcome"/> is <see cref="FanaticalRevealOutcome.Revealed"/>.
/// </summary>
internal sealed record FanaticalRevealResult(FanaticalRevealOutcome Outcome, string? Key) {
	internal static readonly FanaticalRevealResult Failed = new(FanaticalRevealOutcome.Failed, null);
	internal static readonly FanaticalRevealResult EmailRequired = new(FanaticalRevealOutcome.EmailRequired, null);
}

internal sealed partial class FanaticalWebHandler {
	/// <summary>
	/// Attempts to reveal an order item's key via <c>/api/user/orders/redeem</c> with an empty
	/// <c>atok</c>. Fanatical responds either with the key string directly, or with
	/// <c>{"key":"email"}</c> when it requires an emailed verification code (which the plugin
	/// cannot intercept). The caller uses the latter to decide whether to keep going.
	/// </summary>
	internal async Task<FanaticalRevealResult> RedeemKeyAsync(string orderId, string bundleId, string productId, string serialId, string itemId) {
		ArgumentException.ThrowIfNullOrEmpty(orderId);
		ArgumentException.ThrowIfNullOrEmpty(itemId);

		if (!HasCredentials) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Cannot reveal Fanatical key — no credentials");
			return FanaticalRevealResult.Failed;
		}

		try {
			// Body matches the browser's reveal request; atok is empty so Fanatical tells us whether
			// an emailed code is required rather than us pre-supplying one.
			string body = "{"
				+ $"\"oid\":{JsonString(orderId)},"
				+ $"\"bid\":{JsonString(bundleId)},"
				+ $"\"pid\":{JsonString(productId)},"
				+ $"\"serialId\":{JsonString(serialId)},"
				+ $"\"iid\":{JsonString(itemId)},"
				+ "\"atok\":\"\""
				+ "}";

			HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ApiRedeemPath) {
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			}).ConfigureAwait(false);

			string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Fanatical reveal failed for item {itemId}: {response.StatusCode}");
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] reveal body: {responseBody[..Math.Min(300, responseBody.Length)]}");
				return FanaticalRevealResult.Failed;
			}

			JsonElement json;

			try {
				json = responseBody.ToJsonObject<JsonElement>();
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse Fanatical reveal response for item {itemId}");
				return FanaticalRevealResult.Failed;
			}

			if (json.ValueKind != JsonValueKind.Object) {
				return FanaticalRevealResult.Failed;
			}

			foreach (JsonProperty prop in json.EnumerateObject()) {
				if (prop.Name.Equals("key", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String) {
					string? value = prop.Value.GetString();

					if (string.IsNullOrEmpty(value)) {
						return FanaticalRevealResult.Failed;
					}

					if (value.Equals("email", StringComparison.OrdinalIgnoreCase)) {
						return FanaticalRevealResult.EmailRequired;
					}

					return new FanaticalRevealResult(FanaticalRevealOutcome.Revealed, value);
				}
			}

			return FanaticalRevealResult.Failed;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Fanatical reveal threw for item {itemId}");
			return FanaticalRevealResult.Failed;
		}
	}

	/// <summary>Encodes a value as a JSON string literal (quotes + escaping). Null becomes <c>""</c>.</summary>
	private static string JsonString(string? value) => $"\"{JsonEncodedText.Encode(value ?? string.Empty)}\"";
}
