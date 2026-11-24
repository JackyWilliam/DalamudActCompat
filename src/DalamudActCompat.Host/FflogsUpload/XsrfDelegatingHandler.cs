using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;


namespace DalamudActCompat.Host.FflogsUpload
{
    // Sets X-XSRF-TOKEN on every request from the XSRF-TOKEN cookie.
    public sealed class XsrfDelegatingHandler : DelegatingHandler
    {
        private readonly CookieContainer _cookies;
        private readonly Uri _baseUri;

        public XsrfDelegatingHandler(CookieContainer cookies, Uri baseUri, HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
            _cookies = cookies;
            _baseUri = baseUri;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var xsrfCookie = _cookies.GetCookies(_baseUri)["XSRF-TOKEN"]?.Value;
            if (!string.IsNullOrEmpty(xsrfCookie))
            {
                request.Headers.Remove("X-XSRF-TOKEN");
                request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrfCookie));
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}

