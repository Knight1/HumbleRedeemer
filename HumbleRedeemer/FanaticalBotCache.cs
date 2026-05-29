using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ArchiSteamFarm.Helpers;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

internal sealed class FanaticalBotCache : SerializableFile {
	/// <summary>Bearer token currently in use (refreshed automatically via /api/user/refresh-auth).</summary>
	[JsonInclude]
	[JsonPropertyName("AuthToken")]
	internal string? AuthToken { get; set; }

	/// <summary>Anonymous-id sent as the <c>anonid</c> header. Comes from <c>localStorage.bsanonymous</c> on first run.</summary>
	[JsonInclude]
	[JsonPropertyName("AnonId")]
	internal string? AnonId { get; set; }

	/// <summary>Optional expiry of the cached <see cref="AuthToken"/> if Fanatical's refresh response includes one.</summary>
	[JsonInclude]
	[JsonPropertyName("TokenExpires")]
	internal DateTime? TokenExpires { get; set; }

	/// <summary>UTC timestamp of the last successful auth refresh — used to throttle refresh attempts.</summary>
	[JsonInclude]
	[JsonPropertyName("LastTokenRefresh")]
	internal DateTime? LastTokenRefresh { get; set; }

	/// <summary>All order IDs the plugin has seen for this bot. Subsequent runs only fetch IDs not in this set.</summary>
	[JsonInclude]
	[JsonPropertyName("KnownOrderIds")]
	internal List<string> KnownOrderIds { get; set; } = new();

	/// <summary>Per-key state (one entry per game item across all orders). Persists revealed key strings + Steam-redemption flag.</summary>
	[JsonInclude]
	[JsonPropertyName("CachedKeys")]
	internal List<FanaticalKeyInfo> CachedKeys { get; set; } = new();

	private FanaticalBotCache(string filePath) : this() {
		ArgumentException.ThrowIfNullOrEmpty(filePath);

		FilePath = filePath;
	}

	[JsonConstructor]
	private FanaticalBotCache() { }

	protected override Task Save() => Save(this);

	internal Task SaveAsync() => Save();

	internal static async Task<FanaticalBotCache> CreateOrLoad(string filePath) {
		ArgumentException.ThrowIfNullOrEmpty(filePath);

		if (!File.Exists(filePath)) {
			return new FanaticalBotCache(filePath);
		}

		FanaticalBotCache? cache;

		try {
			string json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
			cache = json.ToJsonObject<FanaticalBotCache>();
		} catch (Exception) {
			return new FanaticalBotCache(filePath);
		}

		if (cache == null) {
			return new FanaticalBotCache(filePath);
		}

		cache.FilePath = filePath;

		return cache;
	}
}
