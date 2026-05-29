using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

internal sealed partial class FanaticalWebHandler {
	/// <summary>
	/// Loads the auth token + anonid for this handler. Priority order:
	/// <list type="number">
	///   <item>Explicit <paramref name="configToken"/> / <paramref name="configAnonId"/> from bot
	///     config — overrides the cache so users can rotate tokens by editing config.</item>
	///   <item>Values previously persisted in the cache.</item>
	/// </list>
	/// Returns true when both fields are populated. The caller is expected to verify them via
	/// <see cref="RefreshAuthAsync"/> before using them for real requests.
	/// </summary>
	internal async Task<bool> LoadCredentialsAsync(string? configToken, string? configAnonId) {
		await AuthSemaphore.WaitAsync().ConfigureAwait(false);

		try {
			bool tokenChanged = false;

			if (!string.IsNullOrEmpty(configToken) && !string.Equals(configToken, BotCache.AuthToken, StringComparison.Ordinal)) {
				BotCache.AuthToken = configToken;
				BotCache.TokenExpires = null;
				BotCache.LastTokenRefresh = null;
				tokenChanged = true;
			}

			if (!string.IsNullOrEmpty(configAnonId) && !string.Equals(configAnonId, BotCache.AnonId, StringComparison.Ordinal)) {
				BotCache.AnonId = configAnonId;
				tokenChanged = true;
			}

			AuthToken = BotCache.AuthToken;
			AnonId = BotCache.AnonId;

			if (tokenChanged) {
				await BotCache.SaveAsync().ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Updated Fanatical credentials from config");
			}

			return HasCredentials;
		} finally {
			AuthSemaphore.Release();
		}
	}

	/// <summary>
	/// Calls Fanatical's <c>/api/user/refresh-auth</c> to validate (and rotate) the cached token.
	/// On success persists the new token + expiry to the cache. Returns true if the call succeeded
	/// and the token still works.
	/// </summary>
	internal async Task<bool> RefreshAuthAsync() {
		if (!HasCredentials) {
			ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Cannot refresh Fanatical auth — no credentials loaded");
			return false;
		}

		await AuthSemaphore.WaitAsync().ConfigureAwait(false);

		try {
			HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ApiRefreshAuthPath)).ConfigureAwait(false);

			string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Fanatical refresh-auth failed: {response.StatusCode}. Token may be invalid — paste a fresh value of localStorage.bsauth.token into FanaticalAuthToken.");
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] refresh-auth body: {body[..Math.Min(300, body.Length)]}");
				return false;
			}

			// Response shape (best effort): { token: "Bearer ...", expires: "...", authenticated: true }
			try {
				JsonElement json = body.ToJsonObject<JsonElement>();

				if (json.ValueKind == JsonValueKind.Object) {
					string? newToken = null;
					DateTime? newExpires = null;

					foreach (JsonProperty prop in json.EnumerateObject()) {
						switch (prop.Name) {
							case "token" when prop.Value.ValueKind == JsonValueKind.String:
								newToken = prop.Value.GetString();
								break;
							case "expires" when prop.Value.ValueKind == JsonValueKind.String:
								if (DateTime.TryParse(prop.Value.GetString(), out DateTime parsed)) {
									newExpires = parsed;
								}

								break;
						}
					}

					if (!string.IsNullOrEmpty(newToken)) {
						AuthToken = newToken;
						BotCache.AuthToken = newToken;
					}

					if (newExpires.HasValue) {
						BotCache.TokenExpires = newExpires;
					}
				}
			} catch (Exception ex) {
				// Refresh succeeded HTTP-wise but we couldn't parse the body — keep using the
				// existing token since the call returned 2xx, just log for diagnosis.
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Could not parse refresh-auth response (HTTP succeeded, keeping current token): {ex.Message}");
			}

			BotCache.LastTokenRefresh = DateTime.UtcNow;
			await BotCache.SaveAsync().ConfigureAwait(false);

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Fanatical auth token refreshed successfully");
			return true;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Fanatical refresh-auth threw");
			return false;
		} finally {
			AuthSemaphore.Release();
		}
	}
}
