using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

internal sealed class UnpaidMonthInfo {
	internal string MachineName { get; set; } = "";
	internal string HumanName { get; set; } = "";
	internal string ChoiceUrl { get; set; } = "";
}

internal sealed partial class HumbleBundleWebHandler {
	/// <summary>
	/// Determine whether the current Humble Choice month needs to be paid.
	/// The machine_name is derived from the current UTC date (e.g. "february_2026_choice").
	/// The subscription products API only lists already-paid months; if the current month
	/// appears there with a gamekey it is already paid and null is returned.
	/// </summary>
	internal async Task<UnpaidMonthInfo?> GetCurrentUnpaidMonthAsync() {
		if (!IsLoggedIn) {
			return null;
		}

		// Derive identifiers for the current month from the date
		DateTime now = DateTime.UtcNow;
#pragma warning disable CA1308 // Lowercase is required for Humble Bundle API URL/machine_name construction
		string monthName = now.ToString("MMMM", CultureInfo.InvariantCulture).ToLowerInvariant();
#pragma warning restore CA1308
		string currentProductUrlPath = $"{monthName}-{now.Year}";     // e.g. "february-2026"
		string currentMachineName = $"{monthName}_{now.Year}_choice"; // e.g. "february_2026_choice"
		string humanName = $"Humble Choice - {now.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}";

		try {
			// The subscription products API only lists paid months with their gamekeys.
			// If the current month already appears with a gamekey, it is already paid.
			using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/subscriptions/humble_monthly/subscription_products_with_gamekeys/");
			HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

			if (response.IsSuccessStatusCode) {
				string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

				try {
					JsonElement data = json.ToJsonObject<JsonElement>();

					if (IsCurrentMonthAlreadyPaid(data, currentProductUrlPath, currentMachineName)) {
						ASF.ArchiLogger.LogGenericDebug($"[{BotName}] {humanName} is already paid");
						return null;
					}
				} catch {
					// Parsing failure — attempt payment anyway; payearly will fail gracefully
					// if the month is already paid.
				}
			}
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to check subscription products");
		}

		return new UnpaidMonthInfo {
			MachineName = currentMachineName,
			ChoiceUrl = currentProductUrlPath,
			HumanName = humanName
		};
	}

	/// <summary>
	/// Returns true if the subscription products list already contains an entry for the
	/// current month with a non-empty gamekey. Matches on productUrlPath or machine_name.
	/// </summary>
	private static bool IsCurrentMonthAlreadyPaid(JsonElement data, string currentProductUrlPath, string currentMachineName) {
		JsonElement array = default;

		if (data.ValueKind == JsonValueKind.Array) {
			array = data;
		} else if (data.ValueKind == JsonValueKind.Object) {
			foreach (JsonProperty prop in data.EnumerateObject()) {
				if (prop.Value.ValueKind == JsonValueKind.Array) {
					array = prop.Value;
					break;
				}
			}
		}

		if (array.ValueKind != JsonValueKind.Array) {
			return false;
		}

		foreach (JsonElement item in array.EnumerateArray()) {
			if (item.ValueKind != JsonValueKind.Object) {
				continue;
			}

			string productUrlPath = "";
			string machineName = "";
			bool hasGameKey = false;

			foreach (JsonProperty prop in item.EnumerateObject()) {
				switch (prop.Name) {
					case "productUrlPath" when prop.Value.ValueKind == JsonValueKind.String:
						productUrlPath = prop.Value.GetString() ?? "";
						break;
					case "machine_name" when prop.Value.ValueKind == JsonValueKind.String:
						machineName = prop.Value.GetString() ?? "";
						break;
					case "gamekey" when prop.Value.ValueKind == JsonValueKind.String:
						if (!string.IsNullOrEmpty(prop.Value.GetString())) {
							hasGameKey = true;
						}

						break;
				}
			}

			bool matchesCurrentMonth =
				(!string.IsNullOrEmpty(productUrlPath) && productUrlPath.Equals(currentProductUrlPath, StringComparison.OrdinalIgnoreCase)) ||
				(!string.IsNullOrEmpty(machineName) && machineName.Equals(currentMachineName, StringComparison.OrdinalIgnoreCase));

			if (matchesCurrentMonth && hasGameKey) {
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// POST /membership/payearly to initiate early payment for the current month.
	/// The CSRF token is sent as a form field (_le_csrf_token) as required by this endpoint.
	/// Returns the jobId on success, or null on failure.
	/// </summary>
	internal async Task<string?> PayEarlyAsync(string product, string choiceUrl) {
		if (!IsLoggedIn) {
			return null;
		}

		try {
			// Ensure csrf_cookie is present. After session restore from cache only
			// _simpleauth_sess is loaded; JSON API endpoints do not set csrf_cookie.
			// Visiting /membership causes the server to issue a fresh csrf_cookie.
			Uri baseUri = new(BaseUrl);
			bool hasCsrfCookie = false;

			foreach (Cookie c in CookieContainer.GetCookies(baseUri)) {
				if (c.Name.Equals("csrf_cookie", StringComparison.OrdinalIgnoreCase)) {
					hasCsrfCookie = true;
					break;
				}
			}

			if (!hasCsrfCookie) {
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] csrf_cookie not found, fetching /membership to obtain one");
				using HttpRequestMessage pageRequest = new(HttpMethod.Get, $"/membership/{choiceUrl}");
				await HttpClient.SendAsync(pageRequest).ConfigureAwait(false);
			}

			// Read CSRF token from the csrf_cookie
			string csrfToken = "";

			foreach (Cookie cookie in CookieContainer.GetCookies(baseUri)) {
				if (cookie.Name.Equals("csrf_cookie", StringComparison.OrdinalIgnoreCase)) {
					csrfToken = cookie.Value;
					break;
				}
			}

			// payearly requires _le_csrf_token in the form body (unlike redeemkey which uses a header)
			string body = $"_le_csrf_token={Uri.EscapeDataString(csrfToken)}&product={Uri.EscapeDataString(product)}";

			using HttpRequestMessage request = new(HttpMethod.Post, "/membership/payearly") {
				Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
			};

			request.Headers.Add("Referer", $"{BaseUrl}/membership/{choiceUrl}");
			request.Headers.Add("Origin", BaseUrl);
			request.Headers.Add("X-Requested-With", "XMLHttpRequest");

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Initiating early payment for product: {product}");

			HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Pay early request failed: {response.StatusCode}");
				string errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Pay early error response: {errorBody}");
				return null;
			}

			string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
			JsonElement data;

			try {
				data = json.ToJsonObject<JsonElement>();
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse pay early response");
				return null;
			}

			if (data.ValueKind != JsonValueKind.Object) {
				return null;
			}

			bool success = false;
			string? jobId = null;

			foreach (JsonProperty prop in data.EnumerateObject()) {
				switch (prop.Name) {
					case "success":
						success = prop.Value.ValueKind == JsonValueKind.True;
						break;
					case "jobId" when prop.Value.ValueKind == JsonValueKind.String:
						jobId = prop.Value.GetString();
						break;
				}
			}

			if (!success || string.IsNullOrEmpty(jobId)) {
				ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Pay early failed or returned no jobId: {json[..Math.Min(200, json.Length)]}");
				return null;
			}

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Pay early initiated, jobId: {jobId}");
			return jobId;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to pay early");
			return null;
		}
	}

	/// <summary>
	/// Poll /membership/payearlystatus/{jobId} until payment completes.
	/// Uses increasing delays (1s, 2s, 3s, ...) matching the browser reference implementation.
	/// Returns the gamekey on success, or null on timeout/failure.
	/// </summary>
	internal async Task<string?> PollPayEarlyStatusAsync(string jobId, int maxAttempts = 15) {
		for (int attempt = 1; attempt <= maxAttempts; attempt++) {
			await Task.Delay(attempt * 1000).ConfigureAwait(false);

			try {
				using HttpRequestMessage request = new(HttpMethod.Get, $"/membership/payearlystatus/{jobId}");
				request.Headers.Add("X-Requested-With", "XMLHttpRequest");
				HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

				if (!response.IsSuccessStatusCode) {
					ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Pay early status check returned: {response.StatusCode}");
					continue;
				}

				string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				JsonElement data;

				try {
					data = json.ToJsonObject<JsonElement>();
				} catch {
					continue;
				}

				if (data.ValueKind != JsonValueKind.Object) {
					continue;
				}

				bool success = false;
				bool inProgress = false;
				string? gameKey = null;
				string? reason = null;

				foreach (JsonProperty prop in data.EnumerateObject()) {
					switch (prop.Name) {
						case "success":
							success = prop.Value.ValueKind == JsonValueKind.True;
							break;
						case "inProgress":
							inProgress = prop.Value.ValueKind == JsonValueKind.True;
							break;
						case "gamekey" when prop.Value.ValueKind == JsonValueKind.String:
							gameKey = prop.Value.GetString();
							break;
						case "reason" when prop.Value.ValueKind == JsonValueKind.String:
							reason = prop.Value.GetString();
							break;
					}
				}

				if (success && !string.IsNullOrEmpty(gameKey)) {
					ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Payment completed, gamekey: {gameKey}");
					return gameKey;
				}

				if (!inProgress) {
					// Not in progress and not successful — permanent failure
					string failureDetail = !string.IsNullOrEmpty(reason) ? reason : json[..Math.Min(200, json.Length)];
					ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Pay early failed: {failureDetail}");
					return null;
				}

				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Payment still in progress, attempt {attempt}/{maxAttempts}...");
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Error polling pay early status");
			}
		}

		ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Pay early polling timed out after {maxAttempts} attempts");
		return null;
	}
}
