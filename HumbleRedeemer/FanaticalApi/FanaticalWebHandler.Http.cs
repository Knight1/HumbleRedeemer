using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;

namespace HumbleRedeemer;

internal sealed partial class FanaticalWebHandler {
	/// <summary>
	/// Adds Fanatical's auth headers to a request: <c>authorization</c> (opaque token from
	/// <c>localStorage.bsauth.token</c>) and optionally <c>anonid</c> (from
	/// <c>localStorage.bsanonymous.id</c>). Only the authorization header is required; the API
	/// accepts requests without anonid. All headers use <c>TryAddWithoutValidation</c> because
	/// the strongly-typed <c>HttpRequestHeaders</c> properties (Accept, etc.) are stripped from
	/// trimmed ASF builds and throw <see cref="MissingMethodException"/> at runtime.
	/// </summary>
	private void AddAuthHeaders(HttpRequestMessage request) {
		if (!string.IsNullOrEmpty(AuthToken)) {
			request.Headers.TryAddWithoutValidation("authorization", AuthToken);
		}

		if (!string.IsNullOrEmpty(AnonId)) {
			request.Headers.TryAddWithoutValidation("anonid", AnonId);
		}

		request.Headers.TryAddWithoutValidation("Accept", "application/json");
		request.Headers.TryAddWithoutValidation("Origin", BaseUrl);
		request.Headers.TryAddWithoutValidation("Referer", $"{BaseUrl}/en/orders");
	}

	/// <summary>
	/// Sends an authenticated Fanatical request. Pre-buffers the response body on non-success so
	/// callers can inspect it after this method returns.
	/// </summary>
	internal async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken = default) {
		using HttpRequestMessage request = requestFactory();
		AddAuthHeaders(request);

		HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

		if (!response.IsSuccessStatusCode) {
			string body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
			MediaTypeHeaderValue? contentType = response.Content.Headers.ContentType;
			response.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));

			if (contentType != null) {
				response.Content.Headers.ContentType = contentType;
			}
		}

		return response;
	}

	/// <summary>
	/// Minimal <see cref="IWebProxy"/> identical to the Humble handler's — needed because trimmed
	/// ASF builds strip <see cref="WebProxy"/>'s constructors.
	/// </summary>
	private sealed class StaticWebProxy : IWebProxy {
		private readonly Uri ProxyUri;

		public ICredentials? Credentials { get; set; }

		internal StaticWebProxy(Uri proxyUri) {
			ArgumentNullException.ThrowIfNull(proxyUri);

			ProxyUri = proxyUri;

			if (!string.IsNullOrEmpty(proxyUri.UserInfo)) {
				string[] parts = proxyUri.UserInfo.Split(':', 2);
				string user = Uri.UnescapeDataString(parts[0]);
				string pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
				Credentials = new NetworkCredential(user, pass);
			}
		}

		public Uri GetProxy(Uri destination) => ProxyUri;
		public bool IsBypassed(Uri host) => false;
	}
}
