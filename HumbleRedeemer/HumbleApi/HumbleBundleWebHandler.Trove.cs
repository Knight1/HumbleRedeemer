using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

internal sealed class TroveGameInfo {
	internal string GameMachineName { get; set; } = "";
	internal string HumanName { get; set; } = "";
	internal string DownloadMachineName { get; set; } = "";
	internal string Filename { get; set; } = "";
}

internal sealed partial class HumbleBundleWebHandler {
	/// <summary>
	/// Fetch all Trove games by iterating chunks until an empty response is returned.
	/// Returns one TroveGameInfo per game (first available platform download).
	/// </summary>
	internal async Task<List<TroveGameInfo>?> GetAllTroveGamesAsync() {
		if (!IsLoggedIn) {
			return null;
		}

		List<TroveGameInfo> result = new();
		int chunkIndex = 0;

		try {
			while (true) {
				string url = $"/api/v1/trove/chunk?property=popularity&direction=desc&index={chunkIndex}";
				using HttpRequestMessage request = new(HttpMethod.Get, url);
				request.Headers.Add("X-Requested-With", "XMLHttpRequest");
				HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

				if (!response.IsSuccessStatusCode) {
					ASF.ArchiLogger.LogGenericError($"[{BotName}] Trove chunk {chunkIndex} request failed: {response.StatusCode}");
					break;
				}

				string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				JsonElement data;

				try {
					data = json.ToJsonObject<JsonElement>();
				} catch (Exception ex) {
					ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse Trove chunk {chunkIndex}");
					break;
				}

				// Empty array means no more chunks
				if (data.ValueKind != JsonValueKind.Array) {
					break;
				}

				int countBefore = result.Count;

				foreach (JsonElement game in data.EnumerateArray()) {
					if (game.ValueKind != JsonValueKind.Object) {
						continue;
					}

					string gameMachineName = "";
					string humanName = "";
					string downloadMachineName = "";
					string filename = "";

					foreach (JsonProperty prop in game.EnumerateObject()) {
						switch (prop.Name) {
							case "machine_name" when prop.Value.ValueKind == JsonValueKind.String:
								gameMachineName = prop.Value.GetString() ?? "";
								break;
							case "human-name" when prop.Value.ValueKind == JsonValueKind.String:
								humanName = prop.Value.GetString() ?? "";
								break;
							case "downloads" when prop.Value.ValueKind == JsonValueKind.Object:
								// Pick the first available platform download
								foreach (JsonProperty platform in prop.Value.EnumerateObject()) {
									if (platform.Value.ValueKind != JsonValueKind.Object) {
										continue;
									}

									foreach (JsonProperty dlProp in platform.Value.EnumerateObject()) {
										switch (dlProp.Name) {
											case "machine_name" when dlProp.Value.ValueKind == JsonValueKind.String:
												downloadMachineName = dlProp.Value.GetString() ?? "";
												break;
											case "url" when dlProp.Value.ValueKind == JsonValueKind.Object:
												foreach (JsonProperty urlProp in dlProp.Value.EnumerateObject()) {
													if (urlProp.Name.Equals("web", StringComparison.OrdinalIgnoreCase) &&
													    urlProp.Value.ValueKind == JsonValueKind.String) {
														filename = urlProp.Value.GetString() ?? "";
													}
												}

												break;
										}
									}

									// Stop after the first platform that has both fields
									if (!string.IsNullOrEmpty(downloadMachineName) && !string.IsNullOrEmpty(filename)) {
										break;
									}
								}

								break;
						}
					}

					if (!string.IsNullOrEmpty(gameMachineName) && !string.IsNullOrEmpty(downloadMachineName) && !string.IsNullOrEmpty(filename)) {
						result.Add(new TroveGameInfo {
							GameMachineName = gameMachineName,
							HumanName = humanName,
							DownloadMachineName = downloadMachineName,
							Filename = filename
						});
					}
				}

				// No new games parsed — stop iterating
				if (result.Count == countBefore) {
					break;
				}

				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Trove chunk {chunkIndex}: {result.Count - countBefore} games");
				chunkIndex++;
			}
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to fetch Trove games");
			return null;
		}

		return result;
	}

	/// <summary>
	/// Register a Trove game download in the account by calling the sign URL endpoint.
	/// This claims the game without downloading the file.
	/// Returns true on success.
	/// </summary>
	internal async Task<bool> ClaimTroveGameAsync(string downloadMachineName, string filename) {
		if (!IsLoggedIn) {
			return false;
		}

		try {
			string body = $"machine_name={Uri.EscapeDataString(downloadMachineName)}&filename={Uri.EscapeDataString(filename)}";

			using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/user/download/sign") {
				Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
			};

			request.Headers.Add("X-Requested-With", "XMLHttpRequest");

			HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Trove claim failed for '{downloadMachineName}': {response.StatusCode}");
				return false;
			}

			return true;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Error claiming Trove game '{downloadMachineName}'");
			return false;
		}
	}
}
