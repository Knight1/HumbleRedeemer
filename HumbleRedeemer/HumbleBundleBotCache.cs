using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ArchiSteamFarm.Helpers;
using ArchiSteamFarm.Helpers.Json;

namespace HumbleRedeemer;

internal sealed class HumbleBundleBotCache : SerializableFile {
	[JsonInclude]
	[JsonPropertyName("Cookies")]
	internal List<SavedCookie> Cookies { get; private set; } = new();

	[JsonInclude]
	[JsonPropertyName("LastLogin")]
	internal DateTime? LastLogin { get; set; }

	[JsonInclude]
	[JsonPropertyName("CachedGameKeys")]
	internal List<string> CachedGameKeys { get; set; } = new();

	[JsonInclude]
	[JsonPropertyName("CachedTpks")]
	internal List<HumbleTpkInfo> CachedTpks { get; set; } = new();

	private HumbleBundleBotCache(string filePath) : this() {
		ArgumentException.ThrowIfNullOrEmpty(filePath);

		FilePath = filePath;
	}

	[JsonConstructor]
	private HumbleBundleBotCache() { }

	protected override Task Save() => Save(this);

	internal Task SaveAsync() => Save();

	internal static async Task<HumbleBundleBotCache> CreateOrLoad(string filePath) {
		ArgumentException.ThrowIfNullOrEmpty(filePath);

		if (!File.Exists(filePath)) {
			return new HumbleBundleBotCache(filePath);
		}

		HumbleBundleBotCache? cache;

		try {
			string json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
			cache = json.ToJsonObject<HumbleBundleBotCache>();
		} catch (Exception) {
			// If file is corrupted, create new cache
			return new HumbleBundleBotCache(filePath);
		}

		if (cache == null) {
			return new HumbleBundleBotCache(filePath);
		}

		cache.FilePath = filePath;

		return cache;
	}

	internal sealed class SavedCookie {
		[JsonInclude]
		[JsonPropertyName("Name")]
		public string Name { get; set; } = "";

		[JsonInclude]
		[JsonPropertyName("Value")]
		public string Value { get; set; } = "";

		[JsonInclude]
		[JsonPropertyName("Domain")]
		public string Domain { get; set; } = "";

		[JsonInclude]
		[JsonPropertyName("Path")]
		public string Path { get; set; } = "/";

		[JsonInclude]
		[JsonPropertyName("Expires")]
		public DateTime? Expires { get; set; }

		[JsonInclude]
		[JsonPropertyName("Secure")]
		public bool Secure { get; set; }

		[JsonInclude]
		[JsonPropertyName("HttpOnly")]
		public bool HttpOnly { get; set; }

		[JsonConstructor]
		public SavedCookie() { }
	}
}
