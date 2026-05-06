using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;

namespace HumbleRedeemer;

internal sealed partial class HumbleBundleWebHandler {
	/// <summary>
	/// Returns true if the response body contains a Cloudflare bot-detection challenge or block page.
	/// </summary>
	private static bool IsCloudflareBlock(HttpResponseMessage response, string body) {
		// Cloudflare "Attention Required!" hard block
		if (body.Contains("Attention Required! | Cloudflare", StringComparison.OrdinalIgnoreCase)) {
			return true;
		}

		// Cloudflare JavaScript challenge variable
		if (body.Contains("window._cf_chl_opt", StringComparison.OrdinalIgnoreCase)) {
			return true;
		}

		// Cloudflare managed challenge title
		if (body.Contains("<title>Just a moment...</title>", StringComparison.Ordinal)) {
			return true;
		}

		// Cloudflare rate-limit (429) confirmed via the cf-ray response header
		if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.TryGetValues("cf-ray", out _)) {
			return true;
		}

		return false;
	}

	/// <summary>
	/// Sends an HTTP request produced by <paramref name="requestFactory"/>.
	/// If a Cloudflare IP/ASN block is detected the request is not retried and all
	/// subsequent requests are skipped until the plugin is reloaded with a proxy configured.
	/// Non-successful responses have their body pre-buffered so callers can still read it.
	/// </summary>
	internal async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken = default) {
		if (IsCloudflareBlocked) {
			ASF.ArchiLogger.LogGenericError($"[{BotName}] Skipping request — Cloudflare IP/ASN block detected. Configure 'HumbleBundleProxy' with a residential proxy to bypass this.");

			return new HttpResponseMessage(HttpStatusCode.Forbidden) {
				Content = new StringContent("Cloudflare IP/ASN block — all requests skipped")
			};
		}

		using HttpRequestMessage request = requestFactory();
		HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

		if (!response.IsSuccessStatusCode) {
			// Read and re-buffer body so callers can still access it after Cloudflare detection
			string body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
			MediaTypeHeaderValue? contentType = response.Content.Headers.ContentType;
			response.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));

			if (contentType != null) {
				response.Content.Headers.ContentType = contentType;
			}

			if (IsCloudflareBlock(response, body)) {
				IsCloudflareBlocked = true;
				ASF.ArchiLogger.LogGenericError($"[{BotName}] Cloudflare IP/ASN block detected — your server's IP or ASN is blocked by Cloudflare. All further HumbleBundle requests will be skipped. Configure 'HumbleBundleProxy' with a residential proxy to bypass this.");
			}
		}

		return response;
	}
}
