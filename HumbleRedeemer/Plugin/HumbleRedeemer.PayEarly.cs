using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;

namespace HumbleRedeemer;

internal sealed partial class HumbleRedeemer {
	/// <summary>
	/// If AutoPayMonthly is enabled, find and pay for the current unpaid Humble Choice month.
	/// Stores the resulting gamekey in BotPaidGameKeys so downstream processing can apply
	/// PayMonthlyButNotReveal / PayMonthlyRevealButNotToSteam config flags.
	/// </summary>
	private static async Task TryAutoPayCurrentMonthAsync(Bot bot, HumbleBundleBotCache botCache, HumbleBundleWebHandler webHandler, HumbleBundleBotConfig config) {
		if (!config.AutoPayMonthly) {
			return;
		}

		// Only attempt payment once per UTC calendar day to avoid charging on every restart
		if (botCache.LastAutoPayDate.HasValue && botCache.LastAutoPayDate.Value.Date == DateTime.UtcNow.Date) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] AutoPayMonthly already attempted today ({botCache.LastAutoPayDate.Value:yyyy-MM-dd}), skipping");
			return;
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Checking for unpaid Humble Choice month...");

		UnpaidMonthInfo? unpaidMonth = await webHandler.GetCurrentUnpaidMonthAsync().ConfigureAwait(false);

		if (unpaidMonth == null) {
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] No unpaid month found (or not a subscriber)");
			botCache.LastAutoPayDate = DateTime.UtcNow;
			await botCache.SaveAsync().ConfigureAwait(false);
			return;
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Found unpaid month: {unpaidMonth.HumanName} ({unpaidMonth.MachineName})");

		string? jobId = await webHandler.PayEarlyAsync(unpaidMonth.MachineName, unpaidMonth.ChoiceUrl).ConfigureAwait(false);

		if (jobId == null) {
			ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] Failed to initiate payment for {unpaidMonth.HumanName}");
			return;
		}

		if (jobId.Length == 0) {
			// Payment was already in progress (initiated externally or by a previous run)
			ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] {unpaidMonth.HumanName} payment already in progress, skipping");
			botCache.LastAutoPayDate = DateTime.UtcNow;
			await botCache.SaveAsync().ConfigureAwait(false);
			return;
		}

		string? gameKey = await webHandler.PollPayEarlyStatusAsync(jobId).ConfigureAwait(false);

		if (string.IsNullOrEmpty(gameKey)) {
			ASF.ArchiLogger.LogGenericError($"[{bot.BotName}] Payment for {unpaidMonth.HumanName} did not complete");
			return;
		}

		string mode = config.PayMonthlyButNotReveal ? "not revealing keys" :
			config.PayMonthlyRevealButNotToSteam ? "revealing keys (not for Steam)" : "revealing keys";

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Successfully paid for {unpaidMonth.HumanName} ({mode}), gamekey: {gameKey}");

		BotPaidGameKeys[bot] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { gameKey };

		botCache.LastAutoPayDate = DateTime.UtcNow;
		await botCache.SaveAsync().ConfigureAwait(false);
	}
}
