using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameServer.Client
{
    // Messages from the server, keep-alive pings and reconnecting.
    public sealed partial class RealtimeClient
    {
        // ---- incoming messages --------------------------------------------------------------------------------

        private void OnMessage(string text)
        {
            RealtimeMessage message;
            try
            {
                var obj = JsonConvert.DeserializeObject<JObject>(text, Json.Settings);
                message = new RealtimeMessage
                {
                    Type = (string)obj["type"],
                    Id = obj["id"] == null || obj["id"].Type == JTokenType.Null ? null : (string)obj["id"],
                    Data = obj["data"]
                };
            }
            catch (Exception)
            {
                return;
            }

            if (message.Id != null && _pending.TryGetValue(message.Id, out var pending))
            {
                if (message.Type == "error")
                {
                    var code = message.Data == null ? "error" : (string)message.Data["code"] ?? "error";
                    var detail = message.Data == null ? "" : (string)message.Data["message"] ?? "";
                    pending.Completion.TrySetException(new ApiException(code, detail, 400));
                }
                else
                {
                    pending.Completion.TrySetResult(message);
                }

                return;
            }

            _dispatch.Post(() => Raise(message));
        }

        private void Raise(RealtimeMessage message)
        {
            var data = message.Data;
            switch (message.Type)
            {
                case "channel.message":
                    ChatMessageReceived?.Invoke(data.To<ChatMessage>());
                    break;
                case "channel.message_deleted":
                    ChatMessageDeleted?.Invoke((string)data["channel"], (long)data["id"]);
                    break;
                case "matchmaker.matched":
                    MatchFound?.Invoke(data.To<MatchFound>());
                    break;
                case "matchmaker.expired":
                    MatchmakerExpired?.Invoke(data.To<TicketExpired>());
                    break;
                case "notification.new":
                    NotificationReceived?.Invoke(data.To<NotificationEvent>());
                    break;
                case "party.invite":
                    PartyInvited?.Invoke(data.To<PartyInvite>());
                    break;
                case "party.update":
                    PartyUpdated?.Invoke(data.To<Party>());
                    break;
                case "party.left":
                    PartyLeft?.Invoke(data.To<PartyLeft>());
                    break;
            }

            EventReceived?.Invoke(message);

            Action<JToken>[] listeners;
            lock (_handlers)
                listeners = _handlers.TryGetValue(message.Type ?? "", out var list) ? list.ToArray() : new Action<JToken>[0];

            foreach (var listener in listeners)
            {
                try
                {
                    listener(data);
                }
                catch (Exception ex)
                {
                    _options.Log?.Invoke("A realtime handler threw: " + ex);
                }
            }
        }

        // ---- keeping the connection ---------------------------------------------------------------------------

        private void StartPings(IRealtimeConnection connection)
        {
            StopPings();
            var cts = new CancellationTokenSource();
            _pingCts = cts;
            var seconds = Math.Max(_options.PingIntervalSeconds, 5);

            Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
                        await RequestOnAsync(connection, "ping", null, cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // no pong: treat the connection as dead so the reconnect logic takes over
                        try { await connection.CloseAsync(); } catch (Exception) { }
                        return;
                    }
                }
            });
        }

        private void StopPings()
        {
            var cts = _pingCts;
            _pingCts = null;
            if (cts != null)
                cts.Cancel();
        }

        private void OnClosed(IRealtimeConnection connection, string reason)
        {
            foreach (var entry in _pending.ToArray())
            {
                if (ReferenceEquals(entry.Value.Connection, connection))
                    entry.Value.Completion.TrySetException(new ApiException("connection_closed", "The connection closed: " + reason, 0));
            }

            if (!ReferenceEquals(connection, _connection))
                return;

            _connection = null;
            StopPings();
            _dispatch.Post(() => Disconnected?.Invoke(reason));

            if (_wanted && _options.AutoReconnect)
                Task.Run(ReconnectLoop);
        }

        private async Task ReconnectLoop()
        {
            var delaySeconds = 1;
            while (_wanted)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                if (!_wanted)
                    return;

                try
                {
                    await _connectLock.WaitAsync();
                    try
                    {
                        if (_connection != null)
                            return;
                        await OpenAsync(_transport ?? _options.RealtimeTransport, CancellationToken.None);
                    }
                    finally
                    {
                        _connectLock.Release();
                    }

                    foreach (var channel in _channels.Keys.ToArray())
                    {
                        try
                        {
                            await RequestAsync("channel.join", new { channel }, CancellationToken.None);
                        }
                        catch (ApiException)
                        {
                            bool ignored;
                            _channels.TryRemove(channel, out ignored);
                        }
                    }

                    _dispatch.Post(() => Reconnected?.Invoke());
                    return;
                }
                catch (ApiException ex) when (ex.Code == "unauthorized" || ex.Code == "not_logged_in" || ex.Code == "invalid_refresh_token" || ex.Code == "transport_not_enabled")
                {
                    _wanted = false;
                    _dispatch.Post(() => Disconnected?.Invoke(ex.Code));
                    return;
                }
                catch (Exception)
                {
                    delaySeconds = Math.Min(delaySeconds * 2, 30);
                }
            }
        }
    }
}
