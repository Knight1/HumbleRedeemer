using System;
using System.Text.Json.Serialization;

namespace HumbleRedeemer;

/// <summary>
/// Per-game state extracted from a Fanatical order item. Persisted in <see cref="FanaticalBotCache.CachedKeys"/>.
/// One entry per game item across all orders — bundle / pickAndMix items are flattened during parsing.
/// </summary>
internal sealed class FanaticalKeyInfo {
	/// <summary>The Fanatical order ID (24-char hex from <c>/orders/{id}</c>).</summary>
	[JsonInclude]
	[JsonPropertyName("OrderId")]
	internal string OrderId { get; set; } = "";

	/// <summary>Item ID (<c>iid</c> on the order item).</summary>
	[JsonInclude]
	[JsonPropertyName("ItemId")]
	internal string ItemId { get; set; } = "";

	/// <summary>Internal Mongo-style item id (<c>_id</c>) — used to dedupe items that share the same iid across re-fetches.</summary>
	[JsonInclude]
	[JsonPropertyName("InternalId")]
	internal string InternalId { get; set; } = "";

	[JsonInclude]
	[JsonPropertyName("Name")]
	internal string Name { get; set; } = "";

	/// <summary>URL slug (e.g. <c>easy-delivery-co</c>) — used for slug-based blacklisting.</summary>
	[JsonInclude]
	[JsonPropertyName("Slug")]
	internal string Slug { get; set; } = "";

	/// <summary>True when the item's <c>drm.steam</c> flag is set. Only Steam-DRM keys can be redeemed via ASF.</summary>
	[JsonInclude]
	[JsonPropertyName("DrmSteam")]
	internal bool DrmSteam { get; set; }

	/// <summary>
	/// Comma-separated list of all DRM platforms the item is flagged for (e.g. "steam,epicgames").
	/// Stored verbatim so unfamiliar platforms don't get silently dropped — useful for log/triage.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("Drms")]
	internal string Drms { get; set; } = "";

	/// <summary>
	/// Order-item status as Fanatical reports it: <c>revealed</c> (key visible in API), <c>fulfilled</c>
	/// (key exists but locked behind email-verification reveal flow), <c>refunded</c>, etc.
	/// Only <c>revealed</c> entries carry a non-empty <see cref="Key"/>.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("Status")]
	internal string Status { get; set; } = "";

	/// <summary>
	/// The revealed key string when <see cref="Status"/> is <c>revealed</c>; null/empty otherwise.
	/// Fanatical persists revealed keys indefinitely in the orders API, so we don't have to
	/// (re-)reveal — we just read.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("Key")]
	internal string? Key { get; set; }

	/// <summary>Optional <c>serialExpiry</c> from the order item (when present).</summary>
	[JsonInclude]
	[JsonPropertyName("SerialExpiry")]
	internal DateTime? SerialExpiry { get; set; }

	/// <summary>
	/// The item's <c>serialId</c> — required as the <c>serialId</c> field when calling the
	/// <c>/api/user/orders/redeem</c> reveal endpoint. Present once Fanatical has allocated a
	/// serial for the item (i.e. fulfilled items); absent for pending / not-yet-allocated items.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("SerialId")]
	internal string? SerialId { get; set; }

	/// <summary>
	/// The <c>_id</c> of the top-level order item this game belongs to — sent as the <c>bid</c>
	/// field when calling the reveal endpoint. For bundle / pickAndMix items this is the parent
	/// bundle's <c>_id</c>; for standalone game items it equals the item's own <c>_id</c>.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("BundleId")]
	internal string BundleId { get; set; } = "";

	/// <summary>
	/// Set to true once Steam returned a terminal response (success / already-owned / region-locked /
	/// bad code) so we don't keep retrying every cycle. Transient failures leave this false so the
	/// retry timer can pick them up later.
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("SteamRedeemAttempted")]
	internal bool SteamRedeemAttempted { get; set; }

	[JsonConstructor]
	internal FanaticalKeyInfo() { }
}
