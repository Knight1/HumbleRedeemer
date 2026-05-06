using System.Text.Json.Serialization;

namespace HumbleRedeemer;

/// <summary>
/// Holds information about a Humble Choice order (a paid month in the subscription) that
/// requires per-month interaction with the Choice page to select content and reveal keys.
/// </summary>
internal sealed class ChoiceOrderInfo {
	[JsonInclude]
	[JsonPropertyName("GameKey")]
	internal string GameKey { get; set; } = "";

	[JsonInclude]
	[JsonPropertyName("ChoiceUrl")]
	internal string ChoiceUrl { get; set; } = "";

	[JsonInclude]
	[JsonPropertyName("HumanName")]
	internal string HumanName { get; set; } = "";

	/// <summary>
	/// Set to true after a <c>ProcessChoiceOrders</c> pass produces zero failures for this
	/// order — i.e. all keys are either revealed or known-permanent (sold-out / expired).
	/// Subsequent runs skip this order entirely so we don't re-fetch the choice page or
	/// re-emit "CHOICE REDEEMED" log lines for 179 already-revealed games.
	/// Reset to false if a later pass produces a failure (forces a re-attempt).
	/// </summary>
	[JsonInclude]
	[JsonPropertyName("Completed")]
	internal bool Completed { get; set; }

	[JsonConstructor]
	internal ChoiceOrderInfo() { }
}
