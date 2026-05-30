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
	/// (PST = UTC-8, PDT = UTC-7); the offset is resolved with manual US DST rules rather
	/// than <c>TimeZoneInfo</c>, which is stripped from ASF's trimmed runtime (loading it
	/// throws <c>TypeLoadException</c> for <c>System.TimeZoneInfo</c>).
	/// </summary>
	private static DateTime ComputeNextChoiceReleaseUtc(DateTime nowUtc) {
		// Approximate Pacific "now" (fixed -8) is precise enough to choose the candidate month;
		// the exact wall-clock candidate is converted back to UTC with the correct DST offset below.
		DateTime approxNowPt = nowUtc.AddHours(-8);
		DateTime candidatePt = FirstTuesdayAt10(approxNowPt.Year, approxNowPt.Month);

		if (candidatePt <= approxNowPt) {
			DateTime nextMonth = new DateTime(approxNowPt.Year, approxNowPt.Month, 1).AddMonths(1);
			candidatePt = FirstTuesdayAt10(nextMonth.Year, nextMonth.Month);
		}

		// Pacific wall-clock → UTC: add 7h during PDT, 8h during PST.
		int offsetHours = IsPacificDaylight(candidatePt) ? 7 : 8;

		return candidatePt.AddHours(offsetHours);
	}

	private static DateTime FirstTuesdayAt10(int year, int month) {
		DateTime first = new(year, month, 1);
		int daysToAdd = ((int) DayOfWeek.Tuesday - (int) first.DayOfWeek + 7) % 7;
		return first.AddDays(daysToAdd).AddHours(10);
	}

	/// <summary>
	/// True if the given Pacific wall-clock time falls in US Daylight Saving Time (PDT, UTC-7).
	/// DST runs from 02:00 on the 2nd Sunday of March to 02:00 on the 1st Sunday of November.
	/// Implemented manually because <see cref="TimeZoneInfo"/> is unavailable in ASF's runtime.
	/// </summary>
	private static bool IsPacificDaylight(DateTime pt) {
		DateTime dstStart = NthSundayOfMonth(pt.Year, 3, 2).AddHours(2);
		DateTime dstEnd = NthSundayOfMonth(pt.Year, 11, 1).AddHours(2);

		return pt >= dstStart && pt < dstEnd;
	}

	private static DateTime NthSundayOfMonth(int year, int month, int n) {
		DateTime first = new(year, month, 1);
		int daysToFirstSunday = ((int) DayOfWeek.Sunday - (int) first.DayOfWeek + 7) % 7;

		return first.AddDays(daysToFirstSunday + (7 * (n - 1)));
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
