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

	[JsonConstructor]
	public HumbleBundleBotConfig() { }
}
