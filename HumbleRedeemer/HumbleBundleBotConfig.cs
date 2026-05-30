using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace HumbleRedeemer;

public sealed class HumbleBundleBotConfig {
	/// <summary>
	/// Master switch for the plugin on this bot. When false, all other Humble Bundle options are
	/// ignored and no login or order fetch is attempted.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleEnabled")]
	public bool Enabled { get; set; }

	/// <summary>
	/// Humble Bundle account email. Required for the initial login; not needed once a valid
	/// session cookie is cached.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleUsername")]
	public string? Username { get; set; }

	/// <summary>
	/// Humble Bundle account password. If omitted and ASF is not running headless, the plugin
	/// prompts for it on the console. Not needed once a valid session cookie is cached.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundlePassword")]
	public string? Password { get; set; }

	/// <summary>
	/// Optional 2FA shared-secret or one-time code. If omitted and 2FA is required, the plugin
	/// prompts for the 6-digit code on the console.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleTwoFactorCode")]
	public string? TwoFactorCode { get; set; }

	/// <summary>
	/// Interval in minutes between automatic retry passes for keys that couldn't be redeemed
	/// (sold-out, transient failure, Steam rate-limit). Defaults to 60 — long enough to amortise
	/// Steam's "1 activation per 3 minutes" cooldown after the initial 30-package burst.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleRedeemRetryIntervalMinutes")]
	public int RedeemRetryIntervalMinutes { get; set; } = 60;

	/// <summary>
	/// When true, region restrictions on Humble keys (<c>disallowed_countries</c> /
	/// <c>exclusive_countries</c>) are ignored — the key is both revealed AND forwarded to Steam.
	/// Use with care: redeeming a region-locked key on the wrong account can lead to a refund
	/// being refused or, in rare cases, a Steam restriction.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleIgnoreStoreLocation")]
	public bool IgnoreStoreLocation { get; set; } = false;

	/// <summary>
	/// When true, a periodic timer (<see cref="RedeemRetryIntervalMinutes"/>) re-attempts any
	/// keys that previously failed to reveal or redeem. Disable to make the plugin a one-shot
	/// per session.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleAutoRetry")]
	public bool AutoRetry { get; set; } = true;

	/// <summary>
	/// When true, games already in the bot's Steam library are still revealed on Humble — but as
	/// gift links rather than direct keys, so the resulting URL can be shared.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleUseGiftLinkForOwned")]
	public bool UseGiftLinkForOwned { get; set; } = false;

	/// <summary>
	/// When true, only keys that have an explicit expiration date are eligible for reveal. Keys
	/// that never expire are left untouched. Useful when prioritising at-risk keys before they
	/// disappear.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleRedeemOnlyWithExpiration")]
	public bool RedeemOnlyWithExpiration { get; set; } = false;

	/// <summary>
	/// Humble order game-keys (the opaque per-order identifier in the Humble URL) to skip during
	/// order fetching. Use this to permanently exclude problematic orders.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleBlacklistedGameKeys")]
	public Collection<string> BlacklistedGameKeys { get; } = new();

	/// <summary>
	/// Steam App IDs that should never be redeemed for this bot (neither revealed on Humble nor
	/// submitted to Steam). Common use case: avoid claiming free-to-play apps you don't want.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleBlacklistedAppIds")]
	public Collection<uint> BlacklistedAppIds { get; } = new();

	/// <summary>
	/// Steam App IDs whose keys should be revealed on Humble (so the key string is available)
	/// but never forwarded to Steam — typically because you intend to gift or trade the key.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleRedeemButNotToSteamAppIds")]
	public Collection<uint> RedeemButNotToSteamAppIds { get; } = new();

	/// <summary>
	/// When true, TPKs without a known Steam App ID are skipped entirely. When false (default),
	/// they are revealed on Humble but never forwarded to Steam — many such codes are non-Steam
	/// vouchers (e.g. "Get One Month of IGN Plus") and submitting them would burn a slot in
	/// Steam's activation rate limit for no benefit.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleSkipUnknownAppIds")]
	public bool SkipUnknownAppIds { get; set; } = false;

	/// <summary>
	/// When true, keys are revealed on Humble even if region restrictions would normally block
	/// them — but are NOT submitted to Steam (since Steam would reject them). The revealed key
	/// can then be gifted or traded to someone in the allowed region.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleIgnoreStoreLocationButRedeem")]
	public bool IgnoreStoreLocationButRedeem { get; set; } = false;

	/// <summary>
	/// Optional HTTP/SOCKS5 proxy for Humble Bundle requests.
	/// Cloudflare blocks datacenter IPs on POST endpoints (/humbler/*).
	/// Use a residential proxy to bypass this.
	/// Examples: "socks5://127.0.0.1:1080", "http://user:pass@proxy.example.com:8080"
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleProxy")]
	public string? Proxy { get; set; }

	/// <summary>
	/// When true, the plugin checks for an unpaid Humble Choice month at startup and pays for
	/// it automatically (at most once per UTC day). Use with care — this charges your saved
	/// payment method.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleAutoPayMonthly")]
	public bool AutoPayMonthly { get; set; } = false;

	/// <summary>
	/// Pair with <see cref="AutoPayMonthly"/>: pay for the month, but do NOT reveal any keys
	/// from it. Use when you want to lock in this month's catalogue without committing to
	/// activations yet.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundlePayMonthlyButNotReveal")]
	public bool PayMonthlyButNotReveal { get; set; } = false;

	/// <summary>
	/// Pair with <see cref="AutoPayMonthly"/>: pay and reveal the keys, but do NOT submit them
	/// to Steam. Useful when you want to keep the keys available for gifting / trading.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundlePayMonthlyRevealButNotToSteam")]
	public bool PayMonthlyRevealButNotToSteam { get; set; } = false;

	/// <summary>
	/// When true, every game in the Humble Vault is registered to the account so it remains
	/// accessible even after the subscription ends. Already-claimed games are tracked in the
	/// per-bot cache, so subsequent runs only claim newly added titles.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleClaimVaultGames")]
	public bool ClaimVaultGames { get; set; } = false;

	/// <summary>
	/// When true, schedule an in-process one-shot timer that fires on the first Tuesday of every
	/// month at 10:00 America/Los_Angeles (Pacific Time — Humble Choice's monthly
	/// release time, DST-aware via manual US Pacific DST rules) and runs the AutoPay + reveal/redeem
	/// pipeline so the new month's keys are picked up without waiting for the next ASF restart
	/// or retry-timer cycle.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleScheduleChoiceCheck")]
	public bool ScheduleChoiceCheck { get; set; } = false;

	/// <summary>
	/// When true, revealed Steam keys are immediately submitted to Steam via ASF's native
	/// <c>Bot.Actions.RedeemKey</c>. Gift-link reveals and keys flagged via the
	/// <c>…ButNotToSteam</c> options are never forwarded to Steam regardless of this setting.
	/// On the first run after enabling, previously-revealed keys for games not yet in the
	/// Steam library are also submitted. Steam's activation rate limit (30 keys, then 1 every
	/// 3 minutes) is detected and respected automatically.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleRedeemOnSteam")]
	public bool RedeemOnSteam { get; set; } = false;

	/// <summary>
	/// When true, also claim Humble Choice games whose only key type is <c>epic_keyless</c>.
	/// These games have no redeemable key string — selecting them on Humble auto-links the game
	/// to the Humble account's connected Epic Games account. To avoid wasting a Choice slot on a
	/// game that cannot be claimed, the plugin only acts on this when the Humble account's
	/// <c>userOptions.has_epic_account_id</c> is true (i.e. an Epic account is actually linked).
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleRedeemEpicKeyless")]
	public bool RedeemEpicKeyless { get; set; } = false;

	/// <summary>
	/// When true, also claim Humble Choice games whose only key type is <c>gog_keyless</c>.
	/// These games have no redeemable key string — selecting them on Humble auto-links the game
	/// to the Humble account's connected GOG account. To avoid wasting a Choice slot on a game
	/// that cannot be claimed, the plugin only acts on this when the Humble account exposes
	/// both <c>userOptions.gog_account_id</c> and <c>userOptions.gog_username</c>.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleRedeemGogKeyless")]
	public bool RedeemGogKeyless { get; set; } = false;

	/// <summary>
	/// When true, also claim Humble Choice games whose only key type is <c>blizzard_keyless</c>
	/// (e.g. Diablo IV). Same flow as Epic/GOG — selecting on Humble auto-links the game to the
	/// Humble account's connected Battle.net account. Only acts when the Choice page's
	/// <c>userOptions.has_battlenet_link</c> is true.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleRedeemBlizzardKeyless")]
	public bool RedeemBlizzardKeyless { get; set; } = false;

	/// <summary>
	/// When true, also claim Humble Choice games whose only key type is <c>origin_keyless</c>.
	/// Same flow as the other keyless options — selecting on Humble auto-links the game to the
	/// Humble account's connected EA / Origin account. Only acts when the Choice page's
	/// <c>userOptions.origin_is_linked</c> is true.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("HumbleBundleRedeemOriginKeyless")]
	public bool RedeemOriginKeyless { get; set; } = false;

	[JsonConstructor]
	public HumbleBundleBotConfig() { }
}
