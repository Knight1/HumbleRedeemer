using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;

namespace HumbleRedeemer;

internal sealed partial class HumbleBundleWebHandler {
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

			// Retry 2FA submission for up to 30 seconds (Authy codes are only valid for ~30s and Cloudflare may transiently block)
				DateTime twoFactorDeadline = DateTime.UtcNow.AddSeconds(30);
				bool twoFactorSuccess = false;

				while (DateTime.UtcNow < twoFactorDeadline) {
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

					if (twoFactorResponse.IsSuccessStatusCode) {
						twoFactorSuccess = true;
						break;
					}

					TimeSpan remaining = twoFactorDeadline - DateTime.UtcNow;
					if (remaining <= TimeSpan.Zero) {
						ASF.ArchiLogger.LogGenericError($"[{BotName}] Two-factor authentication failed: {twoFactorResponse.StatusCode}");
						return false;
					}

					ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Two-factor authentication attempt failed ({twoFactorResponse.StatusCode}), retrying... ({remaining.TotalSeconds:F0}s remaining)");
					await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
				}

				if (!twoFactorSuccess) {
					ASF.ArchiLogger.LogGenericError($"[{BotName}] Two-factor authentication failed after retrying for 30 seconds");
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
