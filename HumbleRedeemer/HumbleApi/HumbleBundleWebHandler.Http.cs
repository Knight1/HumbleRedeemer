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
	private const int CloudflareMaxRetries = 5;
	private static readonly TimeSpan CloudflareRetryDelay = TimeSpan.FromSeconds(3);

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
	/// Sends an HTTP request produced by <paramref name="requestFactory"/> with automatic retry
	/// when a Cloudflare bot-detection response is detected. A fresh <see cref="HttpRequestMessage"/>
	/// is obtained from the factory on every attempt so content streams are never reused.
	/// Non-successful responses have their body pre-buffered so callers can still read it.
	/// </summary>
	internal async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken = default) {
		for (int attempt = 1; attempt <= CloudflareMaxRetries; attempt++) {
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
					if (attempt < CloudflareMaxRetries) {
						ASF.ArchiLogger.LogGenericWarning($"[{BotName}] Cloudflare bot-detection on attempt {attempt}/{CloudflareMaxRetries}, retrying in {CloudflareRetryDelay.TotalSeconds:F0}s...");
						response.Dispose();
						await Task.Delay(CloudflareRetryDelay, cancellationToken).ConfigureAwait(false);
						continue;
					}

					ASF.ArchiLogger.LogGenericError($"[{BotName}] Cloudflare bot-detection persists after {CloudflareMaxRetries} attempts");
				}
			}

			return response;
		}

		// Unreachable: every loop iteration either continues or returns
		throw new InvalidOperationException("Unexpected exit from Cloudflare retry loop");
	}
}
