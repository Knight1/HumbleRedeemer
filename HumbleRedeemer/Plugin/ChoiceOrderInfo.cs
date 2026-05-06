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

	[JsonConstructor]
	internal ChoiceOrderInfo() { }
}
