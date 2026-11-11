using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameServer.Client
{
    public sealed class HttpRequest
    {
        public string Method;
        public string Url;
        public string Body;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
    }

    public sealed class HttpResponse
    {
        public int Status;
        public string Body;
        public string ETag;
    }

    /// <summary>How the SDK sends HTTP. In Unity the default is UnityWebRequest; elsewhere HttpClient.</summary>
    public interface IHttp
    {
        /// <summary>Returns the response for any HTTP status. Throws <see cref="ApiException"/> (status 0) when the server cannot be reached.</summary>
        Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct);
    }

    /// <summary>HttpClient based implementation (works in the editor, on desktop and on most IL2CPP platforms).</summary>
    public sealed class HttpClientHttp : IHttp
    {
        private readonly HttpClient _client;

        public HttpClientHttp(int timeoutSeconds = 30)
        {
            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        }

        public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct)
        {
            using (var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url))
            {
                if (request.Body != null)
                    message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");

                foreach (var header in request.Headers)
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);

                try
                {
                    using (var response = await _client.SendAsync(message, ct))
                    {
                        var body = response.Content == null ? "" : await response.Content.ReadAsStringAsync();
                        return new HttpResponse
                        {
                            Status = (int)response.StatusCode,
                            Body = body,
                            ETag = response.Headers.ETag == null ? null : response.Headers.ETag.ToString()
                        };
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
                {
                    throw new ApiException("network_error", ex.Message, 0);
                }
            }
        }
    }
}
