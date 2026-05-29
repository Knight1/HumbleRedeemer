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
	/// Attempts to reveal an order item's key via <c>/api/user/orders/redeem</c>. With an empty
	/// <paramref name="atok"/>, Fanatical responds either with the key string directly, or with
	/// <c>{"key":"email"}</c> when it requires an emailed verification code; the caller can exchange
	/// that code for an <c>atok</c> via <see cref="SubmitAtokCodeAsync"/> and pass it back here to
	/// complete the reveal.
	/// </summary>
	internal async Task<FanaticalRevealResult> RedeemKeyAsync(string orderId, string bundleId, string productId, string serialId, string itemId, string atok = "") {
		ArgumentException.ThrowIfNullOrEmpty(orderId);
		ArgumentException.ThrowIfNullOrEmpty(itemId);

		if (!HasCredentials) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Cannot reveal Fanatical key — no credentials");
			return FanaticalRevealResult.Failed;
		}

		try {
			// Body matches the browser's reveal request. An empty atok makes Fanatical tell us whether
			// an emailed code is required; a non-empty atok (from SubmitAtokCodeAsync) completes a
			// reveal that previously demanded one.
			string body = "{"
				+ $"\"oid\":{JsonString(orderId)},"
				+ $"\"bid\":{JsonString(bundleId)},"
				+ $"\"pid\":{JsonString(productId)},"
				+ $"\"serialId\":{JsonString(serialId)},"
				+ $"\"iid\":{JsonString(itemId)},"
				+ $"\"atok\":{JsonString(atok)}"
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

	/// <summary>
	/// Exchanges the verification code Fanatical emailed (after a reveal returned
	/// <see cref="FanaticalRevealOutcome.EmailRequired"/>) for an <c>atok</c> token via
	/// <c>/api/user/atok/code</c>. The response is a bare JSON string (the token). Returns null on
	/// failure or if the code was rejected.
	/// </summary>
	internal async Task<string?> SubmitAtokCodeAsync(string code) {
		ArgumentException.ThrowIfNullOrEmpty(code);

		if (!HasCredentials) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Cannot submit Fanatical verification code — no credentials");
			return null;
		}

		try {
			string body = $"{{\"code\":{JsonString(code)}}}";

			HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ApiAtokCodePath) {
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			}).ConfigureAwait(false);

			string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Fanatical verification code rejected: {response.StatusCode}");
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] atok/code body: {responseBody[..Math.Min(300, responseBody.Length)]}");
				return null;
			}

			try {
				JsonElement json = responseBody.ToJsonObject<JsonElement>();

				if (json.ValueKind == JsonValueKind.String) {
					string? atok = json.GetString();

					return string.IsNullOrEmpty(atok) ? null : atok;
				}
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse Fanatical atok/code response");
			}

			return null;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Fanatical atok/code threw");
			return null;
		}
	}

	/// <summary>Encodes a value as a JSON string literal (quotes + escaping). Null becomes <c>""</c>.</summary>
	private static string JsonString(string? value) => $"\"{JsonEncodedText.Encode(value ?? string.Empty)}\"";
}
