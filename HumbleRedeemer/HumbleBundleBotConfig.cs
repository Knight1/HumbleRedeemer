using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace HumbleRedeemer;

public sealed class HumbleBundleBotConfig {
	[JsonInclude]
	[JsonPropertyName("HumbleBundleEnabled")]
	public bool Enabled { get; set; }

	[JsonInclude]
	[JsonPropertyName("HumbleBundleUsername")]
	public string? Username { get; set; }

	[JsonInclude]
	[JsonPropertyName("HumbleBundlePassword")]
	public string? Password { get; set; }

	[JsonInclude]
	[JsonPropertyName("HumbleBundleTwoFactorCode")]
	public string? TwoFactorCode { get; set; }

	[JsonInclude]
	[JsonPropertyName("HumbleBundleRedeemRetryIntervalMinutes")]
	public int RedeemRetryIntervalMinutes { get; set; } = 60;

	[JsonInclude]
	[JsonPropertyName("HumbleBundleIgnoreStoreLocation")]
	public bool IgnoreStoreLocation { get; set; } = false;

	[JsonInclude]
	[JsonPropertyName("HumbleBundleAutoRetry")]
	public bool AutoRetry { get; set; } = true;

	[JsonInclude]
	[JsonPropertyName("HumbleBundleUseGiftLinkForOwned")]
	public bool UseGiftLinkForOwned { get; set; } = false;

	[JsonInclude]
	[JsonPropertyName("HumbleBundleRedeemOnlyWithExpiration")]
	public bool RedeemOnlyWithExpiration { get; set; } = false;

	[JsonInclude]
	[JsonPropertyName("HumbleBundleBlacklistedGameKeys")]
	public Collection<string> BlacklistedGameKeys { get; } = new();

	[JsonInclude]
	[JsonPropertyName("HumbleBundleBlacklistedAppIds")]
	public Collection<uint> BlacklistedAppIds { get; } = new();

	[JsonInclude]
	[JsonPropertyName("HumbleBundleRedeemButNotToSteamAppIds")]
	public Collection<uint> RedeemButNotToSteamAppIds { get; } = new();

	[JsonInclude]
	[JsonPropertyName("HumbleBundleSkipUnknownAppIds")]
	public bool SkipUnknownAppIds { get; set; } = false;

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

	[JsonInclude]
	[JsonPropertyName("HumbleBundleAutoPayMonthly")]
	public bool AutoPayMonthly { get; set; } = false;

	[JsonInclude]
	[JsonPropertyName("HumbleBundlePayMonthlyButNotReveal")]
	public bool PayMonthlyButNotReveal { get; set; } = false;

	[JsonInclude]
	[JsonPropertyName("HumbleBundlePayMonthlyRevealButNotToSteam")]
	public bool PayMonthlyRevealButNotToSteam { get; set; } = false;

	[JsonInclude]
	[JsonPropertyName("HumbleBundleClaimVaultGames")]
	public bool ClaimVaultGames { get; set; } = false;

	[JsonConstructor]
	public HumbleBundleBotConfig() { }
}
