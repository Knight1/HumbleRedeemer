using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;
using ArchiSteamFarm.Web;

namespace HumbleRedeemer;

internal sealed partial class HumbleBundleWebHandler : IDisposable {
	private const string BaseUrl = "https://www.humblebundle.com";

	/// <summary>
	/// Hardcoded list of gamekeys to ignore due to Optimizely injection breaking JSON parsing
	/// This is a temporary workaround until HumbleBundle fixes the issue
	/// </summary>
	private static readonly HashSet<string> BlacklistedGameKeys = new(StringComparer.OrdinalIgnoreCase) {
		"7ZB3spP3bqqYDBx7",
		"DREbpFf3aUhAqh5h",
		"F2Pewb6v8YpxrXAr",
		"M4wwetWcsAfENF2r",
		"ME2dAwrvzXsFPD6U",
		"N37brXhAhbP7RVFW",
		"Pfcr8RKDTk23dVFs",
		"PnnkyeSdFbK7CWNZ",
		"RDCNaNpPYrEc73SR",
		"hKdtsxyFhwaWRkNp",
		"sqGdZ83kVAzrXasy",
		"NhThaUB7eycF",
		"WCev2xb7ekHh",
		"-KSebnNc2bbAhExP3",
		"NAt44VcvTZ7mPpGS",
		"m7KerVbswrZdXZd5",
		"KSebnNc2bbAhExP3"
	};

	private readonly CookieContainer CookieContainer;
	private readonly HttpClient HttpClient;
	private readonly SocketsHttpHandler HttpHandler;
	private readonly SemaphoreSlim LoginSemaphore = new(1, 1);
	private readonly HumbleBundleBotCache BotCache;
	private readonly string BotName;

	private bool IsLoggedIn;

	internal HumbleBundleWebHandler(HumbleBundleBotCache botCache, string botName) {
		ArgumentNullException.ThrowIfNull(botCache);
		ArgumentException.ThrowIfNullOrEmpty(botName);

		BotCache = botCache;
		BotName = botName;

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
		HttpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
		HttpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
	}

	public void Dispose() {
		LoginSemaphore.Dispose();
		HttpClient.Dispose();
		HttpHandler.Dispose();
	}

	/// <summary>
	/// Load saved cookies from bot cache
	/// </summary>
	internal async Task<bool> LoadCookiesAsync() {
		try {
			if (BotCache.Cookies.Count == 0) {
				ASF.ArchiLogger.LogGenericInfo($"[{BotName}] No saved HumbleBundle cookies found in cache");
				return false;
			}

			int loadedCount = 0;
			foreach (HumbleBundleBotCache.SavedCookie savedCookie in BotCache.Cookies) {
				// Only load the essential session cookie (_simpleauth_sess)
				if (!savedCookie.Name.Equals("_simpleauth_sess", StringComparison.OrdinalIgnoreCase)) {
					ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Skipping non-essential cookie: {savedCookie.Name}");
					continue;
				}

				// Check if cookie is expired
				if (savedCookie.Expires.HasValue && savedCookie.Expires.Value < DateTime.UtcNow) {
					ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Session cookie expired on {savedCookie.Expires.Value:yyyy-MM-dd HH:mm:ss} UTC, need to re-login");
					return false;
				}

				Cookie cookie = new(savedCookie.Name, savedCookie.Value, savedCookie.Path, savedCookie.Domain) {
					Secure = savedCookie.Secure,
					HttpOnly = savedCookie.HttpOnly
				};

				if (savedCookie.Expires.HasValue) {
					cookie.Expires = savedCookie.Expires.Value;
				}

				CookieContainer.Add(cookie);
				loadedCount++;
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Loaded session cookie: {savedCookie.Name} (expires: {savedCookie.Expires?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "never"})");
			}

			if (loadedCount == 0) {
				ASF.ArchiLogger.LogGenericWarning($"[{BotName}] No valid session cookie found in cache");
				return false;
			}

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Loaded session cookie from cache");

			// Verify session is still valid
			IsLoggedIn = await VerifySessionAsync().ConfigureAwait(false);

			return IsLoggedIn;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to load HumbleBundle cookies");
			return false;
		}
	}

	/// <summary>
	/// Save current cookies to bot cache
	/// </summary>
	internal async Task SaveCookiesAsync() {
		try {
			Uri baseUri = new(BaseUrl);
			CookieCollection cookies = CookieContainer.GetCookies(baseUri);

			BotCache.Cookies.Clear();

			// Only save the essential session cookie (_simpleauth_sess)
			bool cookieFound = false;
			foreach (Cookie cookie in cookies) {
				if (cookie.Name.Equals("_simpleauth_sess", StringComparison.OrdinalIgnoreCase)) {
					BotCache.Cookies.Add(new HumbleBundleBotCache.SavedCookie {
						Name = cookie.Name,
						Value = cookie.Value,
						Domain = cookie.Domain,
						Path = cookie.Path,
						Expires = cookie.Expires == DateTime.MinValue ? null : cookie.Expires,
						Secure = cookie.Secure,
						HttpOnly = cookie.HttpOnly
					});

					cookieFound = true;
					ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Saved session cookie: {cookie.Name}");
					break; // Only need the session cookie
				}
			}

			if (!cookieFound) {
				ASF.ArchiLogger.LogGenericWarning($"[{BotName}] No session cookie found to save, skipping cache update");
				return;
			}

			BotCache.LastLogin = DateTime.UtcNow;

			await BotCache.SaveAsync().ConfigureAwait(false);

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Saved HumbleBundle session to cache");
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to save HumbleBundle cookies");
		}
	}

	/// <summary>
	/// Login to HumbleBundle with username and password
	/// </summary>
	internal async Task<bool> LoginAsync(string username, string password, string? twoFactorCode = null, ArchiSteamFarm.Steam.Bot? bot = null) {
		await LoginSemaphore.WaitAsync().ConfigureAwait(false);

		try {
			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Attempting to login to HumbleBundle...");

			// Step 1: Get login page to extract CSRF token
			using HttpRequestMessage loginPageRequest = new(HttpMethod.Get, "/login");
			HttpResponseMessage loginPageResponse = await HttpClient.SendAsync(loginPageRequest).ConfigureAwait(false);

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Login page response: {loginPageResponse.StatusCode} from {loginPageResponse.RequestMessage?.RequestUri}");

			if (!loginPageResponse.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to fetch login page: {loginPageResponse.StatusCode}");
				return false;
			}

			string loginPageHtml = await loginPageResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Login page HTML length: {loginPageHtml.Length} chars");
			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Login page preview: {loginPageHtml[..Math.Min(500, loginPageHtml.Length)]}");

			// Extract CSRF token using regex
			string? csrfToken = ExtractCsrfToken(loginPageHtml);

			if (string.IsNullOrEmpty(csrfToken)) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to extract CSRF token from login page");

				// Debug: Check if the page contains the csrf token field at all
				if (loginPageHtml.Contains("_le_csrf_token", StringComparison.OrdinalIgnoreCase)) {
					ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Page contains '_le_csrf_token' string, but regex didn't match");

					// Find and log the surrounding context
					int tokenIndex = loginPageHtml.IndexOf("_le_csrf_token", StringComparison.OrdinalIgnoreCase);
					int start = Math.Max(0, tokenIndex - 100);
					int length = Math.Min(300, loginPageHtml.Length - start);
					string context = loginPageHtml.Substring(start, length);
					ASF.ArchiLogger.LogGenericDebug($"[{BotName}] CSRF token context: {context}");
				} else {
					ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Page does not contain '_le_csrf_token' string at all");
				}

				return false;
			}

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Extracted CSRF token: {csrfToken}");

			// Check for csrf_cookie
			Uri baseUri = new(BaseUrl);
			CookieCollection cookies = CookieContainer.GetCookies(baseUri);
			Cookie? csrfCookie = null;
			foreach (Cookie cookie in cookies) {
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Cookie: {cookie.Name} = {cookie.Value}");
				if (cookie.Name.Equals("csrf_cookie", StringComparison.OrdinalIgnoreCase)) {
					csrfCookie = cookie;
				}
			}

			// Step 2: Submit login credentials
			Dictionary<string, string> loginData = new() {
				{ "username", username },
				{ "password", password },
				{ "_le_csrf_token", csrfToken },
				{ "goto", "/" }
			};

			using HttpRequestMessage loginRequest = new(HttpMethod.Post, "/processlogin") {
				Content = new FormUrlEncodedContent(loginData)
			};

			// Add required headers
			loginRequest.Headers.Add("Referer", $"{BaseUrl}/login");
			loginRequest.Headers.Add("Origin", BaseUrl);

			// Add CSRF prevention token header if csrf_cookie exists
			if (csrfCookie != null) {
				loginRequest.Headers.Add("csrf-prevention-token", csrfCookie.Value);
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Added csrf-prevention-token header: {csrfCookie.Value}");
			}

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Submitting login with username: {username}");

			HttpResponseMessage loginResponse = await HttpClient.SendAsync(loginRequest).ConfigureAwait(false);

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Login response status: {loginResponse.StatusCode}");

			string loginResponseText = await loginResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

			// Check if 2FA is required (can be 401 Unauthorized or 200 OK with 2FA prompt)
			if (loginResponseText.Contains("two_factor_required", StringComparison.OrdinalIgnoreCase) ||
			    loginResponseText.Contains("humbleguard", StringComparison.OrdinalIgnoreCase) ||
			    loginResponseText.Contains("authy-input", StringComparison.OrdinalIgnoreCase)) {

				// Try to extract 2FA type from response using regex
				string twoFactorType = "unknown";

				// Use regex to extract "twofactor_type": "value" from JSON
				Regex twoFactorTypeRegex = TwoFactorTypeRegex();
				Match typeMatch = twoFactorTypeRegex.Match(loginResponseText);

				if (typeMatch.Success && typeMatch.Groups.Count > 1) {
					twoFactorType = typeMatch.Groups[1].Value;
				}

				if (string.IsNullOrEmpty(twoFactorCode)) {
					// Check if we can prompt for 2FA interactively (non-headless mode)
					bool isHeadless = ASF.GlobalConfig?.Headless ?? true;

					if (!isHeadless) {
						ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Two-factor authentication required (method: {twoFactorType})");
						ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Please enter your HumbleBundle 2FA code:");

						// Read 2FA code from console
						string? inputCode = Console.ReadLine();

						if (!string.IsNullOrEmpty(inputCode)) {
							twoFactorCode = inputCode.Trim();
							ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Using provided 2FA code");
						} else {
							ASF.ArchiLogger.LogGenericError($"[{BotName}] No 2FA code provided");
							return false;
						}
					} else {
						ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Two-factor authentication required but no code provided (method: {twoFactorType})");
						ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Please add 'HumbleBundleTwoFactorCode' to your bot configuration with your 2FA code");
						ASF.ArchiLogger.LogGenericDebug($"[{BotName}] 2FA response: {loginResponseText[..Math.Min(200, loginResponseText.Length)]}");
						return false;
					}
				}

				ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Submitting two-factor authentication code (method: {twoFactorType})...");

				// Retry login with 2FA code included
				// The 2FA code is submitted to /processlogin (same endpoint) with all original credentials + code
				Dictionary<string, string> twoFactorLoginData = new() {
					{ "username", username },
					{ "password", password },
					{ "_le_csrf_token", csrfToken },
					{ "goto", "/" },
					{ "code", twoFactorCode }
				};

				using HttpRequestMessage twoFactorRequest = new(HttpMethod.Post, "/processlogin") {
					Content = new FormUrlEncodedContent(twoFactorLoginData)
				};

				twoFactorRequest.Headers.Add("Referer", $"{BaseUrl}/login");
				twoFactorRequest.Headers.Add("Origin", BaseUrl);
				if (csrfCookie != null) {
					twoFactorRequest.Headers.Add("csrf-prevention-token", csrfCookie.Value);
				}

				HttpResponseMessage twoFactorResponse = await HttpClient.SendAsync(twoFactorRequest).ConfigureAwait(false);

				string twoFactorResponseText = await twoFactorResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] 2FA response status: {twoFactorResponse.StatusCode}");
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] 2FA response: {twoFactorResponseText[..Math.Min(500, twoFactorResponseText.Length)]}");

				if (!twoFactorResponse.IsSuccessStatusCode) {
					ASF.ArchiLogger.LogGenericError($"[{BotName}] Two-factor authentication failed: {twoFactorResponse.StatusCode}");
					return false;
				}

				ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Two-factor authentication successful");
			} else if (!loginResponse.IsSuccessStatusCode) {
				// Not a 2FA issue, actual login error
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Login failed with status: {loginResponse.StatusCode}");
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Login error response: {loginResponseText[..Math.Min(500, loginResponseText.Length)]}");
				return false;
			}

			// Verify login was successful
			IsLoggedIn = await VerifySessionAsync().ConfigureAwait(false);

			if (IsLoggedIn) {
				ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Successfully logged in to HumbleBundle");
				await SaveCookiesAsync().ConfigureAwait(false);
			} else {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Login appeared successful but session verification failed");
			}

			return IsLoggedIn;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Exception during HumbleBundle login");
			return false;
		} finally {
			LoginSemaphore.Release();
		}
	}

	/// <summary>
	/// Verify if the current session is still valid by testing the API
	/// </summary>
	private async Task<bool> VerifySessionAsync() {
		try {
			// Use the user/order API to verify the session
			using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/user/order");
			HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

			// If we're redirected to login page, session is invalid
			if (response.RequestMessage?.RequestUri?.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase) == true) {
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Session verification failed: redirected to login");
				return false;
			}

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Session verification failed: {response.StatusCode}");
				return false;
			}

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Session verified: API returned {response.StatusCode}");
			return true;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to verify HumbleBundle session");
			return false;
		}
	}

	/// <summary>
	/// Extract CSRF token from HTML using regex
	/// </summary>
	private static string? ExtractCsrfToken(string html) {
		// Try multiple patterns in case HTML structure varies

		// Pattern 1: JSON-escaped format (current HumbleBundle format)
		// "csrfTokenInput": "\u003cinput ... name\u003d\"_le_csrf_token\" value\u003d\"TOKEN\" ..."
		Regex csrfRegexJson = CsrfTokenRegexJson();
		Match matchJson = csrfRegexJson.Match(html);
		if (matchJson.Success && matchJson.Groups.Count > 1) {
			return matchJson.Groups[1].Value;
		}

		// Pattern 2: Direct HTML - name first, then value
		// <input name="_le_csrf_token" value="TOKEN" ...>
		Regex csrfRegex1 = CsrfTokenRegex();
		Match match1 = csrfRegex1.Match(html);
		if (match1.Success && match1.Groups.Count > 1) {
			return match1.Groups[1].Value;
		}

		// Pattern 3: Direct HTML - value first, then name
		// <input value="TOKEN" name="_le_csrf_token" ...>
		Regex csrfRegex2 = CsrfTokenRegexReverse();
		Match match2 = csrfRegex2.Match(html);
		if (match2.Success && match2.Groups.Count > 1) {
			return match2.Groups[1].Value;
		}

		// Pattern 4: Flexible pattern with any content between
		Regex csrfRegex3 = CsrfTokenRegexFlexible();
		Match match3 = csrfRegex3.Match(html);
		if (match3.Success && match3.Groups.Count > 1) {
			return match3.Groups[1].Value;
		}

		return null;
	}

	/// <summary>
	/// Get all order keys from HumbleBundle using the user/order API
	/// </summary>
	internal async Task<List<string>?> GetOrderKeysAsync() {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return null;
		}

		try {
			using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/user/order");
			HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to fetch user orders: {response.StatusCode}");
				return null;
			}

			string jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

			ASF.ArchiLogger.LogGenericDebug($"[{BotName}] /api/v1/user/order response length: {jsonResponse.Length} chars");

			// Parse the response as an array of objects with "gamekey" property
			// Response format: [{"gamekey": "abc123"}, {"gamekey": "def456"}, ...]
			List<JsonElement>? orders = null;

			try {
				orders = jsonResponse.ToJsonObject<List<JsonElement>>();
			} catch (Exception ex) {
				ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse user orders JSON");
				return new List<string>();
			}

			if (orders == null || orders.Count == 0) {
				ASF.ArchiLogger.LogGenericWarning($"[{BotName}] No orders found");
				return new List<string>();
			}

			List<string> gameKeys = new();

			foreach (JsonElement order in orders) {
				if (order.ValueKind != JsonValueKind.Object) {
					continue;
				}

				foreach (JsonProperty prop in order.EnumerateObject()) {
					if (prop.Name.Equals("gamekey", StringComparison.OrdinalIgnoreCase) &&
					    prop.Value.ValueKind == JsonValueKind.String) {
						string? gamekey = prop.Value.GetString();

						if (!string.IsNullOrEmpty(gamekey)) {
							gameKeys.Add(gamekey);
						}

						break;
					}
				}
			}

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Found {gameKeys.Count} order keys");

			return gameKeys;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to get order keys");
			return null;
		}
	}

	/// <summary>
	/// Get order details for a specific game key
	/// </summary>
	internal async Task<string?> GetOrderDetailsAsync(string gameKey) {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return null;
		}

		try {
			using HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/order/{gameKey}?all_tpkds=true");
			HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to fetch order details for {gameKey}: {response.StatusCode}");
				return null;
			}

			return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to get order details for {gameKey}");
			return null;
		}
	}

	/// <summary>
	/// Get all order details individually (fetches each order separately)
	/// This method is slower but more reliable than the bulk API which returns malformed JSON
	/// </summary>
	internal async Task<Dictionary<string, JsonElement>?> GetAllOrdersIndividuallyAsync(List<string> gameKeys) {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return null;
		}

		if (gameKeys == null || gameKeys.Count == 0) {
			ASF.ArchiLogger.LogGenericWarning($"[{BotName}] No gamekeys provided to fetch orders");
			return new Dictionary<string, JsonElement>();
		}

		try {
			Dictionary<string, JsonElement> allOrders = new();
			int successCount = 0;
			int failCount = 0;
			int blacklistedCount = 0;

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Fetching {gameKeys.Count} orders individually...");

			for (int i = 0; i < gameKeys.Count; i++) {
				string gameKey = gameKeys[i];

				// Skip blacklisted gamekeys (Optimizely injection issue)
				if (BlacklistedGameKeys.Contains(gameKey)) {
					ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Skipping blacklisted gamekey: {gameKey} (Optimizely injection issue)");
					blacklistedCount++;
					continue;
				}

				// Log progress every 10 orders
				if ((i + 1) % 10 == 0 || i == 0 || i == gameKeys.Count - 1) {
					ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Progress: {i + 1}/{gameKeys.Count} orders fetched");
				}

				string? jsonResponse = await GetOrderDetailsAsync(gameKey).ConfigureAwait(false);

				if (string.IsNullOrEmpty(jsonResponse)) {
					failCount++;
					continue;
				}

				try {
					JsonElement orderData = jsonResponse.ToJsonObject<JsonElement>();
					allOrders[gameKey] = orderData;
					successCount++;
				} catch (Exception ex) {
					ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse order {gameKey}");

					// Save the full problematic JSON to a debug file
					try {
						string debugFilePath = Path.Combine(Path.GetTempPath(), $"HumbleRedeemer-{BotName}-order-{gameKey}-error.json");
						await File.WriteAllTextAsync(debugFilePath, jsonResponse).ConfigureAwait(false);
						ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Saved problematic JSON to: {debugFilePath}");
					} catch {
						// Fallback: log preview if file write fails
						int previewLength = Math.Min(1000, jsonResponse.Length);
						ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Problematic JSON preview (first {previewLength} chars): {jsonResponse[..previewLength]}");
					}

					// Try to extract and log the specific error line
					if (ex.Message.Contains("LineNumber:", StringComparison.Ordinal)) {
						try {
							// Extract line number from error message
#pragma warning disable SYSLIB1045 // Simple regex in error handling path, not performance critical
							System.Text.RegularExpressions.Match lineMatch = new System.Text.RegularExpressions.Regex(@"LineNumber:\s*(\d+)").Match(ex.Message);
#pragma warning restore SYSLIB1045
							if (lineMatch.Success && int.TryParse(lineMatch.Groups[1].Value, out int errorLine)) {
								string[] lines = jsonResponse.Split('\n');
								if (errorLine > 0 && errorLine <= lines.Length) {
									int contextStart = Math.Max(0, errorLine - 3);
									int contextEnd = Math.Min(lines.Length, errorLine + 2);

									ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Error at line {errorLine}, showing lines {contextStart + 1}-{contextEnd}:");
									for (int lineIdx = contextStart; lineIdx < contextEnd; lineIdx++) {
										string prefix = lineIdx == errorLine - 1 ? ">>> " : "    ";
										ASF.ArchiLogger.LogGenericDebug($"{prefix}{lineIdx + 1}: {lines[lineIdx]}");
									}
								}
							}
						} catch {
							// Ignore errors in error logging
						}
					}

					failCount++;
				}

				// Small delay to avoid rate limiting (50ms between requests)
				if (i < gameKeys.Count - 1) {
					await Task.Delay(50).ConfigureAwait(false);
				}
			}

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Fetch complete: {successCount} succeeded, {failCount} failed, {blacklistedCount} blacklisted");

			return allOrders;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to get all orders individually");
			return null;
		}
	}

	/// <summary>
	/// Get all order details in bulk (batches of 40 gamekeys)
	/// </summary>
	internal async Task<Dictionary<string, JsonElement>?> GetAllOrdersAsync(List<string> gameKeys) {
		if (!IsLoggedIn) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Not logged in to HumbleBundle");
			return null;
		}

		if (gameKeys == null || gameKeys.Count == 0) {
			ASF.ArchiLogger.LogGenericWarning($"[{BotName}] No gamekeys provided to fetch orders");
			return new Dictionary<string, JsonElement>();
		}

		try {
			Dictionary<string, JsonElement> allOrders = new();
			const int batchSize = 40; // HumbleBundle API limit

			// Process gamekeys in batches of 40
			for (int i = 0; i < gameKeys.Count; i += batchSize) {
				List<string> batch = gameKeys.Skip(i).Take(batchSize).ToList();

				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Fetching orders batch {(i / batchSize) + 1}/{(gameKeys.Count + batchSize - 1) / batchSize} ({batch.Count} orders)");

				// Build query string with multiple gamekeys parameters
				string queryString = "all_tpkds=true&" + string.Join("&", batch.Select(key => $"gamekeys={Uri.EscapeDataString(key)}"));
				string requestUrl = $"/api/v1/orders?{queryString}";

				using HttpRequestMessage request = new(HttpMethod.Get, requestUrl);
				HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);

				if (!response.IsSuccessStatusCode) {
					ASF.ArchiLogger.LogGenericError($"[{BotName}] Failed to fetch orders batch: {response.StatusCode}");
					continue;
				}

				string jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

				ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Batch response length: {jsonResponse.Length} chars");

				// Parse the response as a dictionary
				Dictionary<string, JsonElement>? batchOrders = null;

				try {
					batchOrders = jsonResponse.ToJsonObject<Dictionary<string, JsonElement>>();
				} catch (Exception ex) {
					ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to parse orders batch response");

					// Save the full problematic JSON to a debug file
					try {
						string debugFilePath = Path.Combine(Path.GetTempPath(), $"HumbleRedeemer-{BotName}-batch-{i / batchSize + 1}-error.json");
						await File.WriteAllTextAsync(debugFilePath, jsonResponse).ConfigureAwait(false);
						ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Saved problematic JSON to: {debugFilePath}");
					} catch {
						// Fallback: log preview if file write fails
						int previewLength = Math.Min(2000, jsonResponse.Length);
						ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Problematic JSON preview (first {previewLength} chars): {jsonResponse[..previewLength]}");
					}

					// Try to extract and log the specific error line
					if (ex.Message.Contains("LineNumber:", StringComparison.Ordinal)) {
						try {
							// Extract line number from error message (use instance method, not static)
#pragma warning disable SYSLIB1045 // Simple regex in error handling path, not performance critical
							System.Text.RegularExpressions.Match lineMatch = new System.Text.RegularExpressions.Regex(@"LineNumber:\s*(\d+)").Match(ex.Message);
#pragma warning restore SYSLIB1045
							if (lineMatch.Success && int.TryParse(lineMatch.Groups[1].Value, out int errorLine)) {
								string[] lines = jsonResponse.Split('\n');
								if (errorLine > 0 && errorLine <= lines.Length) {
									int contextStart = Math.Max(0, errorLine - 3);
									int contextEnd = Math.Min(lines.Length, errorLine + 2);

									ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Error at line {errorLine}, showing lines {contextStart + 1}-{contextEnd}:");
									for (int lineIdx = contextStart; lineIdx < contextEnd; lineIdx++) {
										string prefix = lineIdx == errorLine - 1 ? ">>> " : "    ";
										ASF.ArchiLogger.LogGenericDebug($"{prefix}{lineIdx + 1}: {lines[lineIdx]}");
									}
								}
							}
						} catch {
							// Ignore errors in error logging
						}
					}

					continue;
				}

				if (batchOrders != null) {
					foreach ((string gameKey, JsonElement orderData) in batchOrders) {
						allOrders[gameKey] = orderData;
					}

					ASF.ArchiLogger.LogGenericDebug($"[{BotName}] Parsed {batchOrders.Count} orders from batch");
				}
			}

			ASF.ArchiLogger.LogGenericInfo($"[{BotName}] Successfully fetched {allOrders.Count} total orders");

			return allOrders;
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericException(ex, $"[{BotName}] Failed to get all orders");
			return null;
		}
	}

	[GeneratedRegex(@"name\\u003d\\""_le_csrf_token\\""[^""\\]*value\\u003d\\""([^""\\]+)\\""", RegexOptions.IgnoreCase)]
	private static partial Regex CsrfTokenRegexJson();

	[GeneratedRegex(@"name\s*=\s*[""']_le_csrf_token[""'][^>]*value\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
	private static partial Regex CsrfTokenRegex();

	[GeneratedRegex(@"value\s*=\s*[""']([^""']+)[""'][^>]*name\s*=\s*[""']_le_csrf_token[""']", RegexOptions.IgnoreCase)]
	private static partial Regex CsrfTokenRegexReverse();

	[GeneratedRegex(@"_le_csrf_token[""'][^>]*value\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
	private static partial Regex CsrfTokenRegexFlexible();

	[GeneratedRegex(@"""twofactor_type""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase)]
	private static partial Regex TwoFactorTypeRegex();
}
