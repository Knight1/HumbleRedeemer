using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

internal sealed partial class HumbleBundleWebHandler {
	/// <summary>
	/// Hardcoded list of gamekeys to ignore due to Optimizely injection breaking JSON parsing
	/// This is a temporary workaround until HumbleBundle fixes the issue
	/// </summary>
	private static readonly HashSet<string> BlacklistedGameKeys = new(StringComparer.OrdinalIgnoreCase) {
		"7ZB3spP3bqqYDBx7",
		"DREbpFf3aUhAqh5h",
		"F2Pewb6v8YpxrXAr",
		"M4wwetWcsAfENF2r",
		"ME2dAwrvzXsFPD6U",
		"N37brXhAhbP7RVFW",
		"Pfcr8RKDTk23dVFs",
		"PnnkyeSdFbK7CWNZ",
		"RDCNaNpPYrEc73SR",
		"hKdtsxyFhwaWRkNp",
		"sqGdZ83kVAzrXasy",
		"NhThaUB7eycF",
		"WCev2xb7ekHh",
		"-KSebnNc2bbAhExP3",
		"NAt44VcvTZ7mPpGS",
		"m7KerVbswrZdXZd5",
		"KSebnNc2bbAhExP3"
	};

	/// <summary>
	/// Get all order keys from HumbleBundle using the user/order API
	/// </summary>
	internal async Task<List<string>?> GetOrderKeysAsync() {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return null;
		}

		try {
			using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/user/order");
			HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to fetch user orders: {response.StatusCode}");
				return null;
			}

			string jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] /api/v1/user/order response length: {jsonResponse.Length} chars");

			// Parse the response as an array of objects with "gamekey" property
			// Response format: [{"gamekey": "abc123"}, {"gamekey": "def456"}, ...]
			List<JsonElement>? orders = null;

			try {
				orders = jsonResponse.ToJsonObject<List<JsonElement>>();
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse user orders JSON");
				return new List<string>();
			}

			if (orders == null || orders.Count == 0) {
				ASF.ArchiLogger.LogGenericWarning($"[{BotName}] No orders found");
				return new List<string>();
			}

			List<string> gameKeys = new();

			foreach (JsonElement order in orders) {
				if (order.ValueKind != JsonValueKind.Object) {
					continue;
				}

				foreach (JsonProperty prop in order.EnumerateObject()) {
					if (prop.Name.Equals("gamekey", StringComparison.OrdinalIgnoreCase) &&
					    prop.Value.ValueKind == JsonValueKind.String) {
						string? gamekey = prop.Value.GetString();

						if (!string.IsNullOrEmpty(gamekey)) {
							gameKeys.Add(gamekey);
						}

						break;
					}
				}
			}

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Found {gameKeys.Count} order keys");

			return gameKeys;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to get order keys");
			return null;
		}
	}

	/// <summary>
	/// Get order details for a specific game key
	/// </summary>
	internal async Task<string?> GetOrderDetailsAsync(string gameKey) {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return null;
		}

		try {
			using HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/order/{gameKey}?all_tpkds=true");
			HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to fetch order details for {gameKey}: {response.StatusCode}");
				return null;
			}

			return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to get order details for {gameKey}");
			return null;
		}
	}

	/// <summary>
	/// Get all order details individually (fetches each order separately)
	/// This method is slower but more reliable than the bulk API which returns malformed JSON
	/// </summary>
	internal async Task<Dictionary<string, JsonElement>?> GetAllOrdersIndividuallyAsync(List<string> gameKeys) {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return null;
		}

		if (gameKeys == null || gameKeys.Count == 0) {
			ASF.ArchiLogger.LogGenericWarning($"[{BotName}] No gamekeys provided to fetch orders");
			return new Dictionary<string, JsonElement>();
		}

		try {
			Dictionary<string, JsonElement> allOrders = new();
			int successCount = 0;
			int failCount = 0;
			int blacklistedCount = 0;

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Fetching {gameKeys.Count} orders individually...");

			for (int i = 0; i < gameKeys.Count; i++) {
				string gameKey = gameKeys[i];

				// Skip blacklisted gamekeys (Optimizely injection issue)
				if (BlacklistedGameKeys.Contains(gameKey)) {
					ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Skipping blacklisted gamekey: {gameKey} (Optimizely injection issue)");
					blacklistedCount++;
					continue;
				}

				// Log progress every 10 orders
				if ((i + 1) % 10 == 0 || i == 0 || i == gameKeys.Count - 1) {
					ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Progress: {i + 1}/{gameKeys.Count} orders fetched");
				}

				string? jsonResponse = await GetOrderDetailsAsync(gameKey).ConfigureAwait(false);

				if (string.IsNullOrEmpty(jsonResponse)) {
					failCount++;
					continue;
				}

				try {
					JsonElement orderData = jsonResponse.ToJsonObject<JsonElement>();
					allOrders[gameKey] = orderData;
					successCount++;
				} catch (Exception ex) {
					ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse order {gameKey}");

					// Save the full problematic JSON to a debug file
					try {
						string debugFilePath = Path.Combine(Path.GetTempPath(), $"HumbleRedeemer-{BotName}-order-{gameKey}-error.json");
						await File.WriteAllTextAsync(debugFilePath, jsonResponse).ConfigureAwait(false);
						ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Saved problematic JSON to: {debugFilePath}");
					} catch {
						// Fallback: log preview if file write fails
						int previewLength = Math.Min(1000, jsonResponse.Length);
						ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Problematic JSON preview (first {previewLength} chars): {jsonResponse[..previewLength]}");
					}

					// Try to extract and log the specific error line
					if (ex.Message.Contains("LineNumber:", StringComparison.Ordinal)) {
						try {
							// Extract line number from error message
#pragma warning disable SYSLIB1045 // Simple regex in error handling path, not performance critical
							System.Text.RegularExpressions.Match lineMatch = new System.Text.RegularExpressions.Regex(@"LineNumber:\s*(\d+)").Match(ex.Message);
#pragma warning restore SYSLIB1045
							if (lineMatch.Success && int.TryParse(lineMatch.Groups[1].Value, out int errorLine)) {
								string[] lines = jsonResponse.Split('\n');
								if (errorLine > 0 && errorLine <= lines.Length) {
									int contextStart = Math.Max(0, errorLine - 3);
									int contextEnd = Math.Min(lines.Length, errorLine + 2);

									ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Error at line {errorLine}, showing lines {contextStart + 1}-{contextEnd}:");
									for (int lineIdx = contextStart; lineIdx < contextEnd; lineIdx++) {
										string prefix = lineIdx == errorLine - 1 ? ">>> " : "    ";
										ASF.ArchiLogger.LogGenericDebug($"{prefix}{lineIdx + 1}: {lines[lineIdx]}");
									}
								}
							}
						} catch {
							// Ignore errors in error logging
						}
					}

					failCount++;
				}

				// Small delay to avoid rate limiting (50ms between requests)
				if (i < gameKeys.Count - 1) {
					await Task.Delay(50).ConfigureAwait(false);
				}
			}

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Fetch complete: {successCount} succeeded, {failCount} failed, {blacklistedCount} blacklisted");

			return allOrders;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to get all orders individually");
			return null;
		}
	}

	/// <summary>
	/// Get all order details in bulk (batches of 40 gamekeys)
	/// </summary>
	internal async Task<Dictionary<string, JsonElement>?> GetAllOrdersAsync(List<string> gameKeys) {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return null;
		}

		if (gameKeys == null || gameKeys.Count == 0) {
			ASF.ArchiLogger.LogGenericWarning($"[{BotName}] No gamekeys provided to fetch orders");
			return new Dictionary<string, JsonElement>();
		}

		try {
			Dictionary<string, JsonElement> allOrders = new();
			const int batchSize = 40; // HumbleBundle API limit

			// Process gamekeys in batches of 40
			for (int i = 0; i < gameKeys.Count; i += batchSize) {
				List<string> batch = gameKeys.Skip(i).Take(batchSize).ToList();

				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Fetching orders batch {(i / batchSize) + 1}/{(gameKeys.Count + batchSize - 1) / batchSize} ({batch.Count} orders)");

				// Build query string with multiple gamekeys parameters
				string queryString = "all_tpkds=true&" + string.Join("&", batch.Select(key => $"gamekeys={Uri.EscapeDataString(key)}"));
				string requestUrl = $"/api/v1/orders?{queryString}";

				using HttpRequestMessage request = new(HttpMethod.Get, requestUrl);
				HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

				if (!response.IsSuccessStatusCode) {
					ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to fetch orders batch: {response.StatusCode}");
					continue;
				}

				string jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Batch response length: {jsonResponse.Length} chars");

				// Parse the response as a dictionary
				Dictionary<string, JsonElement>? batchOrders = null;

				try {
					batchOrders = jsonResponse.ToJsonObject<Dictionary<string, JsonElement>>();
				} catch (Exception ex) {
					ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse orders batch response");

					// Save the full problematic JSON to a debug file
					try {
						string debugFilePath = Path.Combine(Path.GetTempPath(), $"HumbleRedeemer-{BotName}-batch-{i / batchSize + 1}-error.json");
						await File.WriteAllTextAsync(debugFilePath, jsonResponse).ConfigureAwait(false);
						ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Saved problematic JSON to: {debugFilePath}");
					} catch {
						// Fallback: log preview if file write fails
						int previewLength = Math.Min(2000, jsonResponse.Length);
						ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Problematic JSON preview (first {previewLength} chars): {jsonResponse[..previewLength]}");
					}

					// Try to extract and log the specific error line
					if (ex.Message.Contains("LineNumber:", StringComparison.Ordinal)) {
						try {
							// Extract line number from error message (use instance method, not static)
#pragma warning disable SYSLIB1045 // Simple regex in error handling path, not performance critical
							System.Text.RegularExpressions.Match lineMatch = new System.Text.RegularExpressions.Regex(@"LineNumber:\s*(\d+)").Match(ex.Message);
#pragma warning restore SYSLIB1045
							if (lineMatch.Success && int.TryParse(lineMatch.Groups[1].Value, out int errorLine)) {
								string[] lines = jsonResponse.Split('\n');
								if (errorLine > 0 && errorLine <= lines.Length) {
									int contextStart = Math.Max(0, errorLine - 3);
									int contextEnd = Math.Min(lines.Length, errorLine + 2);

									ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Error at line {errorLine}, showing lines {contextStart + 1}-{contextEnd}:");
									for (int lineIdx = contextStart; lineIdx < contextEnd; lineIdx++) {
										string prefix = lineIdx == errorLine - 1 ? ">>> " : "    ";
										ASF.ArchiLogger.LogGenericDebug($"{prefix}{lineIdx + 1}: {lines[lineIdx]}");
									}
								}
							}
						} catch {
							// Ignore errors in error logging
						}
					}

					continue;
				}

				if (batchOrders != null) {
					foreach ((string gameKey, JsonElement orderData) in batchOrders) {
						allOrders[gameKey] = orderData;
					}

					ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Parsed {batchOrders.Count} orders from batch");
				}
			}

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Successfully fetched {allOrders.Count} total orders");

			return allOrders;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to get all orders");
			return null;
		}
	}
}
