using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using ArchiSteamFarm.Core;

namespace HumbleRedeemer;

internal sealed partial class HumbleBundleWebHandler : IDisposable {
	private const string BaseUrl = "https://www.humblebundle.com";

	// Page paths
	private const string LoginPath = "/login";
	private const string ProcessLoginPath = "/processlogin";
	private const string HomeLibraryPath = "/home/library";

	// API paths
	private const string ApiUserOrderPath = "/api/v1/user/order";
	private const string ApiOrderPath = "/api/v1/order/";
	private const string ApiOrdersPath = "/api/v1/orders";
	private const string ApiSubscriptionProductsPath = "/api/v1/subscriptions/humble_monthly/subscription_products_with_gamekeys/";
	private const string ApiUserDownloadSignPath = "/api/v1/user/download/sign";

	// Membership paths
	private const string MembershipPath = "/membership/";
	private const string MembershipPayEarlyPath = "/membership/payearly";
	private const string MembershipPayEarlyStatusPath = "/membership/payearlystatus/";

	// Humbler paths
	private const string HumblerRedeemKeyPath = "/humbler/redeemkey";
	private const string HumblerChooseContentPath = "/humbler/choosecontent";

	// Client paths
	private const string ClientCatalogPath = "/client/catalog";

	// Gift path
	private const string GiftPath = "/gift";

	private readonly CookieContainer CookieContainer;
	private readonly HttpClient HttpClient;
	private readonly SocketsHttpHandler HttpHandler;
	private readonly SemaphoreSlim LoginSemaphore = new(1, 1);
	private readonly HumbleBundleBotCache BotCache;
	private readonly string BotName;
	private readonly HashSet<string> ConfiguredBlacklistedGameKeys;

	private bool IsCloudflareBlocked;
	private bool IsLoggedIn;

	internal HumbleBundleWebHandler(HumbleBundleBotCache botCache, string botName, IEnumerable<string>? blacklistedGameKeys = null, string? proxyUrl = null) {
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

		if (!string.IsNullOrEmpty(proxyUrl)) {
			HttpHandler.Proxy = new WebProxy(new Uri(proxyUrl));
			HttpHandler.UseProxy = true;
			ASF.ArchiLogger.LogGenericInfo($"[{botName}] Using proxy for HumbleBundle requests");
		}

		HttpClient = new HttpClient(HttpHandler) {
			BaseAddress = new Uri(BaseUrl),
			DefaultRequestVersion = HttpVersion.Version20,
			Timeout = TimeSpan.FromSeconds(30)
		};

		HttpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36");
		HttpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
		HttpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br, zstd");
		HttpClient.DefaultRequestHeaders.Add("Sec-CH-UA", "\"Chromium\";v=\"147\", \"Google Chrome\";v=\"147\", \"Not-A.Brand\";v=\"24\"");
		HttpClient.DefaultRequestHeaders.Add("Sec-CH-UA-Mobile", "?0");
		HttpClient.DefaultRequestHeaders.Add("Sec-CH-UA-Platform", "\"Windows\"");
	}

	/// <summary>
	/// Adds standard browser navigation headers for GET requests that load HTML pages.
	/// </summary>
	private static void AddNavigationHeaders(HttpRequestMessage request) {
		request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
		request.Headers.Add("Sec-Fetch-Site", "same-origin");
		request.Headers.Add("Sec-Fetch-Mode", "navigate");
		request.Headers.Add("Sec-Fetch-Dest", "document");
		request.Headers.Add("Sec-Fetch-User", "?1");
		request.Headers.Add("Upgrade-Insecure-Requests", "1");
	}

	/// <summary>
	/// Adds standard AJAX headers for POST/XHR requests, including CSRF token from cookies.
	/// </summary>
	private void AddAjaxHeaders(HttpRequestMessage request, string? refererPath = null) {
		request.Headers.Add("Accept", "application/json, text/javascript, */*; q=0.01");
		request.Headers.Add("X-Requested-With", "XMLHttpRequest");
		request.Headers.Add("Sec-Fetch-Site", "same-origin");
		request.Headers.Add("Sec-Fetch-Mode", "cors");
		request.Headers.Add("Sec-Fetch-Dest", "empty");
		request.Headers.Add("Referer", $"{BaseUrl}{refererPath ?? HomeLibraryPath}");
		request.Headers.Add("Origin", BaseUrl);

		Uri baseUri = new(BaseUrl);

		foreach (Cookie cookie in CookieContainer.GetCookies(baseUri)) {
			if (cookie.Name.Equals("csrf_cookie", StringComparison.OrdinalIgnoreCase)) {
				request.Headers.Add("csrf-prevention-token", cookie.Value);
				break;
			}
		}
	}

	public void Dispose() {
		LoginSemaphore.Dispose();
		HttpClient.Dispose();
		HttpHandler.Dispose();
	}
}
