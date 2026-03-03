using System;
using System.Net.Http;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;

namespace HumbleRedeemer;

internal sealed partial class HumbleBundleWebHandler {
	/// <summary>
	/// Register a Humble Vault game to the account by calling the sign URL endpoint.
	/// This claims the game without downloading the file.
	/// Returns true on success.
	/// </summary>
	internal async Task<bool> ClaimVaultGameAsync(string downloadMachineName, string filename) {
		if (!IsLoggedIn) {
			return false;
		}

		try {
			string body = $"machine_name={Uri.EscapeDataString(downloadMachineName)}&filename={Uri.EscapeDataString(filename)}";

			HttpResponseMessage response = await SendAsync(() => {
				HttpRequestMessage req = new(HttpMethod.Post, ApiUserDownloadSignPath) {
					Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
				};

				req.Headers.Add("X-Requested-With", "XMLHttpRequest");
				return req;
			}).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Vault claim failed for '{downloadMachineName}': {response.StatusCode}");
				return false;
			}

			return true;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Error claiming Vault game '{downloadMachineName}'");
			return false;
		}
	}
}
