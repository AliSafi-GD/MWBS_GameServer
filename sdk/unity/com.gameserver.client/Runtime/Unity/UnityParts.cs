#if UNITY_2019_4_OR_NEWER
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GameServer.Client
{
    /// <summary>HTTP through UnityWebRequest (the most compatible choice, also on WebGL). Must be called from the main thread.</summary>
    public sealed class UnityWebRequestHttp : IHttp
    {
        private readonly int _timeoutSeconds;

        public UnityWebRequestHttp(int timeoutSeconds = 30)
        {
            _timeoutSeconds = timeoutSeconds;
        }

        public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<HttpResponse>();
            var web = new UnityWebRequest(request.Url, request.Method);
            web.downloadHandler = new DownloadHandlerBuffer();
            web.timeout = _timeoutSeconds;

            if (request.Body != null)
            {
                web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.Body));
                web.SetRequestHeader("Content-Type", "application/json");
            }

            foreach (var header in request.Headers)
                web.SetRequestHeader(header.Key, header.Value);

            var operation = web.SendWebRequest();
            var registration = ct.Register(() =>
            {
                try { web.Abort(); } catch (Exception) { }
            });

            operation.completed += _ =>
            {
                registration.Dispose();
                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled();
                    }
                    else if (web.result == UnityWebRequest.Result.ConnectionError)
                    {
                        tcs.TrySetException(new ApiException("network_error", web.error, 0));
                    }
                    else
                    {
                        tcs.TrySetResult(new HttpResponse
                        {
                            Status = (int)web.responseCode,
                            Body = web.downloadHandler == null ? "" : web.downloadHandler.text,
                            ETag = web.GetResponseHeader("ETag")
                        });
                    }
                }
                finally
                {
                    web.Dispose();
                }
            };

            return tcs.Task;
        }
    }

    /// <summary>Session and device id in PlayerPrefs.</summary>
    public sealed class PlayerPrefsStorage : IClientStorage
    {
        public string Get(string key) => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;

        public void Set(string key, string value)
        {
            PlayerPrefs.SetString(key, value);
            PlayerPrefs.Save();
        }

        public void Delete(string key)
        {
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }
    }
}
#endif
