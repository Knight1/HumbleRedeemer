using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

internal sealed class VaultGameInfo {
	internal string GameMachineName { get; set; } = "";
	internal string HumanName { get; set; } = "";
	internal string DownloadMachineName { get; set; } = "";
	internal string Filename { get; set; } = "";
	internal string Platform { get; set; } = "";
}

internal sealed partial class HumbleBundleWebHandler {
	/// <summary>
	/// Fetch all Humble Vault games by paginating the catalog endpoint until an empty page is returned.
	/// Returns one VaultGameInfo per game per platform (windows, mac, linux).
	/// </summary>
	internal async Task<List<VaultGameInfo>?> GetAllVaultGamesAsync() {
		if (!IsLoggedIn) {
			return null;
		}

		List<VaultGameInfo> result = new();
		int pageIndex = 0;

		try {
			while (true) {
				string url = $"{ClientCatalogPath}?property=start&direction=desc&index={pageIndex}";
				HttpResponseMessage response = await SendAsync(() => {
					HttpRequestMessage req = new(HttpMethod.Get, url);
					req.Headers.Add("X-Requested-With", "XMLHttpRequest");
					return req;
				}).ConfigureAwait(false);

				if (!response.IsSuccessStatusCode) {
					ASF.ArchiLogger.LogGenericError($"[{BotName}] Vault catalog page {pageIndex} request failed: {response.StatusCode}");
					break;
				}

				string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				JsonElement data;

				try {
					data = json.ToJsonObject<JsonElement>();
				} catch (Exception ex) {
					ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse Vault catalog page {pageIndex}");
					break;
				}

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
					List<(string Platform, string DownloadMachineName, string Filename)> platformDownloads = new();

					foreach (JsonProperty prop in game.EnumerateObject()) {
						switch (prop.Name) {
							case "machine_name" when prop.Value.ValueKind == JsonValueKind.String:
								gameMachineName = prop.Value.GetString() ?? "";
								break;
							case "human-name" when prop.Value.ValueKind == JsonValueKind.String:
								humanName = prop.Value.GetString() ?? "";
								break;
							case "downloads" when prop.Value.ValueKind == JsonValueKind.Object:
								// Collect all available platform downloads (windows, mac, linux)
								foreach (JsonProperty platform in prop.Value.EnumerateObject()) {
									if (platform.Value.ValueKind != JsonValueKind.Object) {
										continue;
									}

									string platDownloadMachineName = "";
									string platFilename = "";

									foreach (JsonProperty dlProp in platform.Value.EnumerateObject()) {
										switch (dlProp.Name) {
											case "machine_name" when dlProp.Value.ValueKind == JsonValueKind.String:
												platDownloadMachineName = dlProp.Value.GetString() ?? "";
												break;
											case "url" when dlProp.Value.ValueKind == JsonValueKind.Object:
												foreach (JsonProperty urlProp in dlProp.Value.EnumerateObject()) {
													if (urlProp.Name.Equals("web", StringComparison.OrdinalIgnoreCase) &&
													    urlProp.Value.ValueKind == JsonValueKind.String) {
														platFilename = urlProp.Value.GetString() ?? "";
													}
												}

												break;
										}
									}

									if (!string.IsNullOrEmpty(platDownloadMachineName) && !string.IsNullOrEmpty(platFilename)) {
										platformDownloads.Add((platform.Name, platDownloadMachineName, platFilename));
									}
								}

								break;
						}
					}

					if (!string.IsNullOrEmpty(gameMachineName)) {
						foreach ((string platform, string dlMachineName, string dlFilename) in platformDownloads) {
							result.Add(new VaultGameInfo {
								GameMachineName = gameMachineName,
								HumanName = humanName,
								DownloadMachineName = dlMachineName,
								Filename = dlFilename,
								Platform = platform
							});
						}
					}
				}

				// Empty page — no more games
				if (result.Count == countBefore) {
					break;
				}

				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Vault catalog page {pageIndex}: {result.Count - countBefore} platform downloads");
				pageIndex++;
			}
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to fetch Vault games");
			return null;
		}

		return result;
	}
}
