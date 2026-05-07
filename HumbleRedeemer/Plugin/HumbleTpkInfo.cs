using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HumbleRedeemer;

/// <summary>
/// Holds parsed TPK (third-party key) data from a Humble Bundle order. Persisted in the per-bot
/// cache as part of <see cref="HumbleBundleBotCache.CachedTpks"/>.
/// </summary>
internal sealed class HumbleTpkInfo {
	[JsonInclude]
	[JsonPropertyName("GameKey")]
	internal string GameKey { get; set; } = "";

	[JsonInclude]
	[JsonPropertyName("HumanName")]
	internal string HumanName { get; set; } = "";

	[JsonInclude]
	[JsonPropertyName("MachineName")]
	internal string MachineName { get; set; } = "";

	/// <summary>
	/// Humble's <c>key_type</c> for this TPK — typically <c>steam</c>, or one of the
	/// <c>*_keyless</c> variants (<c>epic_keyless</c> / <c>gog_keyless</c> / <c>blizzard_keyless</c> /
	/// <c>origin_keyless</c>). Empty for legacy cache entries written before this field existed
	/// (those are all <c>steam</c> in practice — the parser used to filter to that single type).
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("KeyType")]
	internal string KeyType { get; set; } = "";

	[JsonInclude]
	[JsonPropertyName("SteamAppId")]
	internal uint SteamAppId { get; set; }

	[JsonInclude]
	[JsonPropertyName("RedeemedKeyVal")]
	internal string? RedeemedKeyVal { get; set; }

	[JsonInclude]
	[JsonPropertyName("IsExpired")]
	internal bool IsExpired { get; set; }

	[JsonInclude]
	[JsonPropertyName("ExpiryDate")]
	internal DateTime? ExpiryDate { get; set; }

	[JsonInclude]
	[JsonPropertyName("SoldOut")]
	internal bool SoldOut { get; set; }

	[JsonInclude]
	[JsonPropertyName("KeyIndex")]
	internal int KeyIndex { get; set; }

	[JsonInclude]
	[JsonPropertyName("IsGift")]
	internal bool IsGift { get; set; }

	[JsonInclude]
	[JsonPropertyName("DisallowedCountries")]
	internal List<string> DisallowedCountries { get; set; } = [];

	[JsonInclude]
	[JsonPropertyName("ExclusiveCountries")]
	internal List<string> ExclusiveCountries { get; set; } = [];

	/// <summary>
	/// Set to true once we've received a terminal response from Steam's RegisterCDKey
	/// (success, already-owned, region locked, bad code, etc.) so we don't keep retrying
	/// on every restart. Transient failures (timeout/rate limit) leave this false so the
	/// retry timer can pick them up again.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("SteamRedeemAttempted")]
	internal bool SteamRedeemAttempted { get; set; }

	/// <summary>
	/// True for TPKs that originated from a Humble Choice month rather than a regular order.
	/// Choice TPKs always carry <see cref="SteamAppId"/> = 0 (the Choice page does not reliably
	/// expose Steam app IDs), so the "no app id" predicates used by Compare / Retry must NOT
	/// treat them as non-Steam vouchers — otherwise <c>HumbleBundleSkipUnknownAppIds = true</c>
	/// hides legitimate Choice retry candidates from the unrevealed count and the retry loop.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("IsChoiceTpk")]
	internal bool IsChoiceTpk { get; set; }

	[JsonConstructor]
	internal HumbleTpkInfo() { }
}
