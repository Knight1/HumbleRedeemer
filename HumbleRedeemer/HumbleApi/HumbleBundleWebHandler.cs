using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace HumbleRedeemer;

internal sealed partial class HumbleBundleWebHandler : IDisposable {
	private const string BaseUrl = "https://www.humblebundle.com";

	private readonly CookieContainer CookieContainer;
	private readonly HttpClient HttpClient;
	private readonly SocketsHttpHandler HttpHandler;
	private readonly SemaphoreSlim LoginSemaphore = new(1, 1);
	private readonly HumbleBundleBotCache BotCache;
	private readonly string BotName;
	private readonly HashSet<string> ConfiguredBlacklistedGameKeys;

	private bool IsLoggedIn;

	internal HumbleBundleWebHandler(HumbleBundleBotCache botCache, string botName, IEnumerable<string>? blacklistedGameKeys = null) {
		ArgumentNullException.ThrowIfNull(botCache);
		ArgumentException.ThrowIfNullOrEmpty(botName);

		BotCache = botCache;
		BotName = botName;
		ConfiguredBlacklistedGameKeys = blacklistedGameKeys != null
			? new HashSet<string>(blacklistedGameKeys, StringComparer.OrdinalIgnoreCase)
			: new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		CookieContainer = new CookieContainer();

		HttpHandler = new SocketsHttpHandler {
			AutomaticDecompression = DecompressionMethods.All,
			CookieContainer = CookieContainer,
			AllowAutoRedirect = true,
			MaxConnectionsPerServer = 10,
			PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15)
		};

		HttpClient = new HttpClient(HttpHandler) {
			BaseAddress = new Uri(BaseUrl),
			DefaultRequestVersion = HttpVersion.Version30,
			Timeout = TimeSpan.FromSeconds(30)
		};

		HttpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
		//HttpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
		//HttpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
	}

	public void Dispose() {
		LoginSemaphore.Dispose();
		HttpClient.Dispose();
		HttpHandler.Dispose();
	}
}
