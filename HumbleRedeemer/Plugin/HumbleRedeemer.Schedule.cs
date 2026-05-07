using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;

namespace HumbleRedeemer;

internal sealed partial class HumbleRedeemer {
	/// <summary>
	/// Per-bot one-shot timers that fire at the next "Humble Choice release moment"
	/// (first Tuesday of the month at 10:00 America/Los_Angeles) and trigger an immediate
	/// AutoPay + reveal/redeem pass so new monthly keys are picked up without waiting
	/// for the periodic retry timer or an ASF restart.
	/// </summary>
	private static readonly ConcurrentDictionary<Bot, Timer> BotChoiceReleaseTimers = new();

	/// <summary>
	/// Returns the next Humble Choice release moment in UTC: the first Tuesday of the
	/// current or next month at 10:00 America/Los_Angeles. Pacific Time observes DST
	/// (PST = UTC-8, PDT = UTC-7); <see cref="TimeZoneInfo"/> resolves the right offset
	/// for the candidate date.
	/// </summary>
	private static DateTime ComputeNextChoiceReleaseUtc(DateTime nowUtc) {
		TimeZoneInfo pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
		DateTime nowPt = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, pacific);
		DateTime candidate = FirstTuesdayAt10(nowPt.Year, nowPt.Month);

		if (candidate <= nowPt) {
			DateTime nextMonth = new DateTime(nowPt.Year, nowPt.Month, 1).AddMonths(1);
			candidate = FirstTuesdayAt10(nextMonth.Year, nextMonth.Month);
		}

		return TimeZoneInfo.ConvertTimeToUtc(candidate, pacific);
	}

	private static DateTime FirstTuesdayAt10(int year, int month) {
		DateTime first = new(year, month, 1);
		int daysToAdd = ((int) DayOfWeek.Tuesday - (int) first.DayOfWeek + 7) % 7;
		return first.AddDays(daysToAdd).AddHours(10);
	}

	/// <summary>
	/// Schedules (or re-schedules) the next monthly Choice-release check for the given bot.
	/// Safe to call repeatedly — any existing timer is disposed first. No-op when the option
	/// is disabled.
	/// </summary>
	private static void ScheduleChoiceReleaseCheck(Bot bot) {
		if (!BotConfigs.TryGetValue(bot, out HumbleBundleBotConfig? config) || !config.ScheduleChoiceCheck) {
			return;
		}

		// Replace any existing timer
		if (BotChoiceReleaseTimers.TryRemove(bot, out Timer? existing)) {
			existing.Dispose();
		}

		DateTime nowUtc = DateTime.UtcNow;
		DateTime nextUtc = ComputeNextChoiceReleaseUtc(nowUtc);
		TimeSpan delay = nextUtc - nowUtc;

		// Defensive: if we somehow computed a time in the past or extremely close, pad it.
		if (delay < TimeSpan.FromMinutes(1)) {
			delay = TimeSpan.FromMinutes(1);
		}

		Timer timer = new(
			_ => _ = Task.Run(async () => await OnChoiceReleaseTime(bot).ConfigureAwait(false)),
			null,
			delay,
			Timeout.InfiniteTimeSpan
		);

		BotChoiceReleaseTimers[bot] = timer;
		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] Next Humble Choice release check scheduled for {nextUtc:yyyy-MM-dd HH:mm} UTC ({delay.TotalHours:F1}h from now)");
	}

	/// <summary>
	/// Fires when the scheduled Choice-release moment arrives. Runs AutoPay (if configured)
	/// and a full reveal/redeem retry pass, then schedules the next month.
	/// </summary>
	private static async Task OnChoiceReleaseTime(Bot bot) {
		if (!BotConfigs.TryGetValue(bot, out HumbleBundleBotConfig? config) || !config.Enabled) {
			return;
		}

		if (!BotHandlers.TryGetValue(bot, out HumbleBundleWebHandler? webHandler) ||
			!BotCaches.TryGetValue(bot, out HumbleBundleBotCache? botCache)) {
			ASF.ArchiLogger.LogGenericWarning($"[{bot.BotName}] Choice release timer fired but bot state is gone — skipping and rescheduling");
			ScheduleChoiceReleaseCheck(bot);
			return;
		}

		ASF.ArchiLogger.LogGenericInfo($"[{bot.BotName}] === Humble Choice release moment — checking for new month ===");

		try {
			// AutoPay (idempotent — has its own once-per-UTC-day guard via LastAutoPayDate).
			await TryAutoPayCurrentMonthAsync(bot, botCache, webHandler, config).ConfigureAwait(false);

			// Re-fetch orders, process new ones, and run the redeem pipeline.
			await RetryRedeemAvailableKeys(bot).ConfigureAwait(false);
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{bot.BotName}] Choice release check failed");
		}

		// Always reschedule for next month — even if this run errored, we still want the cron
		// to keep firing. ScheduleChoiceReleaseCheck no-ops if the option got toggled off.
		ScheduleChoiceReleaseCheck(bot);
	}
}
