using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using ArchiSteamFarm.Core;

namespace HumbleRedeemer;

internal sealed partial class FanaticalWebHandler : IDisposable {
	private const string BaseUrl = "https://www.fanatical.com";

	// API paths
	private const string ApiUserOrdersPath = "/api/user/orders";
	private const string ApiUserOrderPath = "/api/user/orders/";
	private const string ApiRefreshAuthPath = "/api/user/refresh-auth";
	private const string ApiRedeemPath = "/api/user/orders/redeem";

	private readonly HttpClient HttpClient;
	private readonly SocketsHttpHandler HttpHandler;
	private readonly SemaphoreSlim AuthSemaphore = new(1, 1);
	private readonly FanaticalBotCache BotCache;
	private readonly string BotName;
	private readonly HashSet<string> ConfiguredBlacklistedOrderIds;

	private string? AuthToken;
	private string? AnonId;

	internal bool HasCredentials => !string.IsNullOrEmpty(AuthToken);

	internal FanaticalWebHandler(FanaticalBotCache botCache, string botName, IEnumerable<string>? blacklistedOrderIds = null, string? proxyUrl = null) {
		ArgumentNullException.ThrowIfNull(botCache);
		ArgumentException.ThrowIfNullOrEmpty(botName);

		BotCache = botCache;
		BotName = botName;
		ConfiguredBlacklistedOrderIds = blacklistedOrderIds != null
			? new HashSet<string>(blacklistedOrderIds, StringComparer.OrdinalIgnoreCase)
			: new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		HttpHandler = new SocketsHttpHandler {
			AutomaticDecompression = DecompressionMethods.All,
			AllowAutoRedirect = true,
			MaxConnectionsPerServer = 10,
			PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15)
		};

		if (!string.IsNullOrEmpty(proxyUrl)) {
			HttpHandler.Proxy = new StaticWebProxy(new Uri(proxyUrl));
			HttpHandler.UseProxy = true;
			ASF.ArchiLogger.LogGenericInfo($"[{botName}] Using proxy for Fanatical requests");
		}

		HttpClient = new HttpClient(HttpHandler) {
			BaseAddress = new Uri(BaseUrl),
			DefaultRequestVersion = HttpVersion.Version20,
			Timeout = TimeSpan.FromSeconds(30)
		};

		HttpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36");
		HttpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
		HttpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
	}

	public void Dispose() {
		AuthSemaphore.Dispose();
		HttpClient.Dispose();
		HttpHandler.Dispose();
	}
}
