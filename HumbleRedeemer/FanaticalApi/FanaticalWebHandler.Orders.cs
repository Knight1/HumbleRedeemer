using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

internal sealed partial class FanaticalWebHandler {
	/// <summary>
	/// Lists all order IDs for the logged-in user. Fanatical returns an array of order summaries
	/// at <c>/api/user/orders</c>; this method extracts each <c>_id</c> field.
	/// </summary>
	internal async Task<List<string>?> GetOrderIdsAsync() {
		if (!HasCredentials) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Cannot list Fanatical orders — no credentials");
			return null;
		}

		try {
			HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ApiUserOrdersPath)).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to list Fanatical orders: {response.StatusCode}");
				return null;
			}

			string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
			List<JsonElement>? orders;

			try {
				orders = body.ToJsonObject<List<JsonElement>>();
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse Fanatical orders list");
				return null;
			}

			if (orders == null) {
				return new List<string>();
			}

			List<string> ids = new();

			foreach (JsonElement order in orders) {
				if (order.ValueKind != JsonValueKind.Object) {
					continue;
				}

				foreach (JsonProperty prop in order.EnumerateObject()) {
					if (prop.Name.Equals("_id", StringComparison.Ordinal) && prop.Value.ValueKind == JsonValueKind.String) {
						string? id = prop.Value.GetString();

						if (!string.IsNullOrEmpty(id)) {
							ids.Add(id);
						}

						break;
					}
				}
			}

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Fanatical: found {ids.Count} order IDs");
			return ids;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to list Fanatical orders");
			return null;
		}
	}

	/// <summary>
	/// Fetches a single order's details (items + bundles + games) at <c>/api/user/orders/{id}</c>.
	/// Returns the parsed root JSON element or null on failure.
	/// </summary>
	internal async Task<JsonElement?> GetOrderAsync(string orderId) {
		ArgumentException.ThrowIfNullOrEmpty(orderId);

		if (!HasCredentials) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Cannot fetch Fanatical order — no credentials");
			return null;
		}

		try {
			HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{ApiUserOrderPath}{orderId}")).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to fetch Fanatical order {orderId}: {response.StatusCode}");
				return null;
			}

			string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			try {
				return body.ToJsonObject<JsonElement>();
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse Fanatical order {orderId}");
				return null;
			}
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to fetch Fanatical order {orderId}");
			return null;
		}
	}

	/// <summary>
	/// Fetches all listed orders (skipping configured-blacklisted IDs). Used both at startup —
	/// on the new-orders subset — and on retry passes to refresh already-known orders so newly
	/// revealed keys are picked up.
	/// </summary>
	internal async Task<Dictionary<string, JsonElement>> GetOrdersAsync(IReadOnlyCollection<string> orderIds) {
		ArgumentNullException.ThrowIfNull(orderIds);

		Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);

		if (orderIds.Count == 0) {
			return result;
		}

		int success = 0;
		int fail = 0;
		int blacklisted = 0;
		int i = 0;

		foreach (string orderId in orderIds) {
			i++;

			if (ConfiguredBlacklistedOrderIds.Contains(orderId)) {
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Fanatical: skipping blacklisted order {orderId}");
				blacklisted++;
				continue;
			}

			JsonElement? order = await GetOrderAsync(orderId).ConfigureAwait(false);

			if (!order.HasValue) {
				fail++;
				continue;
			}

			result[orderId] = order.Value;
			success++;

			if (i < orderIds.Count) {
				await Task.Delay(75).ConfigureAwait(false);
			}
		}

		ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Fanatical: fetched {success} orders ({fail} failed, {blacklisted} blacklisted)");
		return result;
	}
}
