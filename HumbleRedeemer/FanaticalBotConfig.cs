using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace HumbleRedeemer;

public sealed class FanaticalBotConfig {
	/// <summary>
	/// Master switch for the Fanatical integration on this bot. When false, all other
	/// <c>Fanatical*</c> options are ignored and no Fanatical request is made.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("FanaticalEnabled")]
	public bool Enabled { get; set; }

	/// <summary>
	/// Bearer token from the browser's <c>localStorage.bsauth</c> entry — specifically the value of
	/// <c>JSON.parse(localStorage.bsauth).token</c>. Required because Fanatical's login flow is
	/// reCAPTCHA-protected and cannot be driven headlessly. Once supplied, the token is cached and
	/// refreshed automatically via Fanatical's <c>/api/user/refresh-auth</c> endpoint, so this
	/// option only needs to be set on first run (or when the cached token expires beyond refresh).
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("FanaticalAuthToken")]
	public string? AuthToken { get; set; }

	/// <summary>
	/// Anonymous-id from the browser's <c>localStorage.bsanonymous</c> entry — specifically the
	/// value of <c>JSON.parse(localStorage.bsanonymous).id</c>. Sent as the <c>anonid</c> header
	/// alongside the auth token; Fanatical rejects requests without it. Cached after first use.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("FanaticalAnonId")]
	public string? AnonId { get; set; }

	/// <summary>
	/// When true, every revealed Steam key found in Fanatical orders is forwarded to Steam via
	/// ASF's <c>Bot.Actions.RedeemKey</c>. Keys that are not yet revealed (status <c>fulfilled</c>
	/// — Fanatical sends an email code to unlock them, which the plugin cannot intercept) are
	/// listed in the log so the user knows to reveal them manually in the browser. Once revealed,
	/// the key persists in the orders API and the plugin will pick it up on the next pass.
	/// Steam's activation rate limit is shared with Humble redemptions and respected automatically.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("FanaticalRedeemOnSteam")]
	public bool RedeemOnSteam { get; set; } = false;

	/// <summary>
	/// When true, the plugin probes Fanatical's reveal endpoint to unlock keys automatically: it
	/// attempts to reveal one unrevealed Steam key with an empty verification token. If Fanatical
	/// returns the key directly (no emailed code required for this account/session), the plugin
	/// reveals every remaining unrevealed Steam key the same way and forwards them to Steam (subject
	/// to <see cref="RedeemOnSteam"/>). If Fanatical instead demands an emailed verification code —
	/// which the plugin cannot intercept — it stops after that single probe (which itself triggers
	/// one email) and falls back to the manual-reveal flow. Default: false.
	/// Note: each reveal that needs a code makes Fanatical send you an email, so the probe is
	/// attempted at most once per session.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("FanaticalAttemptReveal")]
	public bool AttemptReveal { get; set; } = false;

	/// <summary>
	/// When true, only items whose <c>drm.steam</c> flag is true are tracked. When false, items
	/// for other DRM platforms (Origin, Epic, GOG, etc.) are also cached but never forwarded to
	/// Steam — useful purely for visibility / inventory purposes.
	/// Default: true (this is an ASF plugin, Steam keys are the primary interest).
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("FanaticalSteamKeysOnly")]
	public bool SteamKeysOnly { get; set; } = true;

	/// <summary>
	/// Interval in minutes between automatic retry passes. Each pass re-lists Fanatical orders
	/// (so newly-revealed keys are discovered) and re-attempts Steam submissions for any keys
	/// that previously hit a transient failure or rate limit. Defaults to 60 to amortise Steam's
	/// "1 activation per 3 minutes" cooldown.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("FanaticalRedeemRetryIntervalMinutes")]
	public int RedeemRetryIntervalMinutes { get; set; } = 60;

	/// <summary>
	/// When true, a periodic timer (<see cref="RedeemRetryIntervalMinutes"/>) re-fetches Fanatical
	/// orders and re-attempts any Steam submissions that previously failed transiently. Disable
	/// to make the Fanatical pass a one-shot per session.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("FanaticalAutoRetry")]
	public bool AutoRetry { get; set; } = true;

	/// <summary>
	/// Fanatical order IDs (the 24-char hex string in the <c>/orders/{id}</c> URL) to skip during
	/// order fetching. Use this to permanently exclude orders that are problematic or that you
	/// don't want the plugin to touch.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("FanaticalBlacklistedOrderIds")]
	public Collection<string> BlacklistedOrderIds { get; } = new();

	/// <summary>
	/// Fanatical game slugs (the URL-friendly identifier from the order item, e.g.
	/// <c>easy-delivery-co</c>) whose keys should NEVER be forwarded to Steam, even if revealed.
	/// Useful when you want the key in your cache for gifting/trading but don't want it activated
	/// on this bot's Steam account.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("FanaticalSkipSteamForSlugs")]
	public Collection<string> SkipSteamForSlugs { get; } = new();

	/// <summary>
	/// Optional HTTP/SOCKS5 proxy for Fanatical requests. Independent of
	/// <c>HumbleBundleProxy</c> — Fanatical and Humble use separate handlers, so each can be
	/// pointed at a different proxy (or none).
	/// Examples: "socks5://127.0.0.1:1080", "http://user:pass@proxy.example.com:8080"
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("FanaticalProxy")]
	public string? Proxy { get; set; }

	[JsonConstructor]
	public FanaticalBotConfig() { }
}
