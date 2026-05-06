using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using SteamKit2;
using SteamKit2.Internal;

namespace HumbleRedeemer;

internal sealed partial class HumbleRedeemer {
	/// <summary>
	/// UTC timestamp until which Steam key redemption is paused for the bot because Steam returned
	/// <see cref="EPurchaseResultDetail.RateLimited"/>. Steam's published limit is 30 packages, then
	/// 1 package per 3 minutes. We back off for at least 3 minutes per hit; the periodic retry
	/// timer (default 60 min) is the long-term mechanism for catching up.
	/// </summary>
	private static readonly ConcurrentDictionary<Bot, DateTime> BotSteamRedeemRateLimitedUntil = new();

	private static readonly TimeSpan SteamRateLimitBackoff = TimeSpan.FromMinutes(3);

	private enum SteamRedeemOutcome {
		/// <summary>Success or permanent failure (already-owned, region-locked, bad code) — caller should mark the TPK as attempted.</summary>
		Terminal,

		/// <summary>Transient failure (network timeout, etc.) — leave for the next retry cycle.</summary>
		Transient,

		/// <summary>Steam said we are rate-limited — caller should stop submitting further keys this batch.</summary>
		RateLimited
	}

	private static void OnSteamLoggedOn(Bot bot, SteamUser.LoggedOnCallback callback) {
		if (callback.Result != EResult.OK) {
			return;
		}

		if (!BotConfigs.ContainsKey(bot)) {
			return;
		}

		// Store IP country code as fallback
		if (!string.IsNullOrEmpty(callback.IPCountryCode)) {
			BotCountryCodes[bot] = callback.IPCountryCode;
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Steam IP country code (fallback): {callback.IPCountryCode}");
		}

		// Fetch the actual store/wallet country via unified service
		_ = Task.Run(async () => await FetchStoreCountry(bot).ConfigureAwait(false));
	}

	private static async Task FetchStoreCountry(Bot bot) {
		if (!BotConfigs.ContainsKey(bot)) {
			return;
		}

		try {
			SteamUnifiedMessages? unifiedMessages = bot.GetHandler<SteamUnifiedMessages>();

			if (unifiedMessages == null) {
				ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] SteamUnifiedMessages handler not available");
				return;
			}

			UserAccount userAccountService = unifiedMessages.CreateService<UserAccount>();

			// Try GetClientWalletDetails first - response contains wallet_country_code and user_country_code
			try {
				CUserAccount_GetClientWalletDetails_Request walletRequest = new();
				SteamUnifiedMessages.ServiceMethodResponse<CUserAccount_GetWalletDetails_Response> walletResponse =
					await userAccountService.GetClientWalletDetails(walletRequest).ToLongRunningTask().ConfigureAwait(false);

				if (walletResponse.Result == EResult.OK) {
					string? walletCountry = walletResponse.Body.wallet_country_code;
					string? userCountry = walletResponse.Body.user_country_code;

					ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] Wallet details - wallet_country: {walletCountry ?? "null"}, user_country: {userCountry ?? "null"}");

					string? country = walletCountry ?? userCountry;

					if (!string.IsNullOrEmpty(country)) {
						BotCountryCodes[bot] = country;
						ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Steam wallet/store country: {country}");
						return;
					}
				}
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] GetClientWalletDetails failed: {ex.Message}");
			}

			// Fallback to GetUserCountry
			try {
				CUserAccount_GetUserCountry_Request countryRequest = new();
				SteamUnifiedMessages.ServiceMethodResponse<CUserAccount_GetUserCountry_Response> countryResponse =
					await userAccountService.GetUserCountry(countryRequest).ToLongRunningTask().ConfigureAwait(false);

				if (countryResponse.Result == EResult.OK) {
					string? country = countryResponse.Body.country;

					if (!string.IsNullOrEmpty(country)) {
						BotCountryCodes[bot] = country;
						ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Steam user country: {country}");
						return;
					}
				}
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] GetUserCountry failed: {ex.Message}");
			}

			// If both failed, IP country code fallback is already stored
			BotCountryCodes.TryGetValue(bot, out string? fallback);
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Using IP country code fallback: {fallback ?? "unknown"}");
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{bot.BotName}] Failed to fetch store country");
		}
	}

	private static void OnLicenseList(Bot bot) {
		// Run comparison in background to avoid blocking the callback chain
		_ = Task.Run(async () => {
			// Small delay to ensure ASF finishes processing the license list and populating OwnedPackages
			await Task.Delay(2000).ConfigureAwait(false);
			await CompareHumbleBundleWithSteamLibrary(bot).ConfigureAwait(false);
		});
	}

	/// <summary>
	/// Submits a revealed Steam key to Steam via ASF's native <c>Bot.Actions.RedeemKey</c>,
	/// honoring a per-bot rate-limit backoff. Returns:
	/// <list type="bullet">
	///   <item><see cref="SteamRedeemOutcome.Terminal"/> — success or permanent failure (already-owned,
	///     region-locked, bad code). Caller should set <c>SteamRedeemAttempted = true</c>.</item>
	///   <item><see cref="SteamRedeemOutcome.Transient"/> — network timeout / no-detail. Leave the
	///     attempt flag alone so the retry timer can try again later.</item>
	///   <item><see cref="SteamRedeemOutcome.RateLimited"/> — Steam refused due to its 30-then-1-per-3-min
	///     limit (or we already know we're in backoff). Caller should stop submitting further keys
	///     this batch; the retry timer's 60-minute interval is the long-term recovery path.</item>
	/// </list>
	/// </summary>
	private static async Task<SteamRedeemOutcome> TryRedeemKeyOnSteamAsync(Bot bot, string key, string humanName, uint appId) {
		string label = appId > 0 ? $"'{humanName}' (AppID: {appId})" : $"'{humanName}'";

		// Short-circuit if we already know we're in backoff.
		if (BotSteamRedeemRateLimitedUntil.TryGetValue(bot, out DateTime until) && until > DateTime.UtcNow) {
			TimeSpan remaining = until - DateTime.UtcNow;
			ASF.ArchiLogger.LogGenericDebug($"[{bot.BotName}] STEAM REDEEM SKIPPED (rate-limited for {remaining.TotalMinutes:F1}m more): {label}");
			return SteamRedeemOutcome.RateLimited;
		}

		try {
			CStore_RegisterCDKey_Response? response = await bot.Actions.RedeemKey(key).ConfigureAwait(false);

			if (response == null) {
				ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] STEAM REDEEM TIMEOUT: {label}");
				return SteamRedeemOutcome.Transient;
			}

			if (response.purchase_receipt_info == null) {
				ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] STEAM REDEEM BAD RESPONSE: {label}");
				return SteamRedeemOutcome.Transient;
			}

			EResult result = (EResult) response.purchase_receipt_info.purchase_status;
			EPurchaseResultDetail detail = (EPurchaseResultDetail) response.purchase_result_details;

			string? grantedItems = response.purchase_receipt_info.line_items.Count > 0
				? string.Join(", ", response.purchase_receipt_info.line_items.Select(li => li.line_item_description))
				: null;

			switch (detail) {
				case EPurchaseResultDetail.NoDetail:
				case EPurchaseResultDetail.AlreadyPurchased:
					ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] STEAM REDEEMED: {label} => {result}/{detail}{(grantedItems != null ? $" [{grantedItems}]" : "")}");
					return SteamRedeemOutcome.Terminal;
				case EPurchaseResultDetail.Timeout:
					ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] STEAM REDEEM TRANSIENT FAILURE: {label} => {result}/{detail}");
					return SteamRedeemOutcome.Transient;
				case EPurchaseResultDetail.RateLimited:
					DateTime backoffUntil = DateTime.UtcNow.Add(SteamRateLimitBackoff);
					BotSteamRedeemRateLimitedUntil[bot] = backoffUntil;
					ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] STEAM REDEEM RATE LIMITED: {label} => {result}/{detail}. Pausing Steam submissions for {SteamRateLimitBackoff.TotalMinutes:F0}m (retry timer will resume later).");
					return SteamRedeemOutcome.RateLimited;
				default:
					ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] STEAM REDEEM FAILED: {label} => {result}/{detail}");
					return SteamRedeemOutcome.Terminal;
			}
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{bot.BotName}] STEAM REDEEM EXCEPTION: {label}");
			return SteamRedeemOutcome.Transient;
		}
	}
}
