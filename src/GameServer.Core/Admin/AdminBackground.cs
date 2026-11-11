using GameServer.Core.Matches;
using GameServer.Core.Realtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Core.Admin;

/// <summary>Sends scheduled broadcasts when they are due and records a metrics sample every minute for the dashboard.</summary>
public sealed class AdminBackground : BackgroundService
{
    private readonly AdminStore _store;
    private readonly INotificationStore _notifications;
    private readonly ConnectionManager _connections;
    private readonly MatchService _matches;
    private readonly Matchmaker _matchmaker;
    private readonly ILogger<AdminBackground> _log;

    public AdminBackground(
        AdminStore store, INotificationStore notifications, ConnectionManager connections,
        MatchService matches, Matchmaker matchmaker, ILogger<AdminBackground> log)
    {
        _store = store;
        _notifications = notifications;
        _connections = connections;
        _matches = matches;
        _matchmaker = matchmaker;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastSample = DateTime.MinValue;
        var lastPrune = DateTime.MinValue;

        // Give the migrator time to finish before the first query.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendDueAsync(stoppingToken);

                var now = DateTime.UtcNow;
                if (now - lastSample >= TimeSpan.FromSeconds(55))
                {
                    lastSample = now;
                    var queued = _matchmaker.CountsByMode().Values.Sum();
                    await _store.SaveSampleAsync(_connections.Counts().Users, _matches.Count, queued, stoppingToken);
                }

                if (now - lastPrune >= TimeSpan.FromHours(6))
                {
                    lastPrune = now;
                    await _store.PruneAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Admin background work failed; will retry.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task SendDueAsync(CancellationToken ct)
    {
        foreach (var item in await _store.ClaimDueAsync(ct))
        {
            var sent = 0;
            string? error = null;
            try
            {
                DateTime? expires = item.ExpiresInDays is > 0 ? DateTime.UtcNow.AddDays(item.ExpiresInDays.Value) : null;
                if (item.Audience == "online")
                {
                    foreach (var (userId, _) in _connections.OnlineUsers(100_000))
                    {
                        try
                        {
                            await _notifications.SendAsync(userId, item.Subject, item.Body, item.Reward, expires, ct);
                            sent++;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _log.LogDebug(ex, "Could not send scheduled message {Id} to {User}.", item.Id, userId);
                        }
                    }
                }
                else
                {
                    sent = await _notifications.BroadcastAsync(item.Subject, item.Body, item.Reward, expires, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
                _log.LogWarning(ex, "Scheduled broadcast {Id} failed.", item.Id);
            }

            await _store.FinishBroadcastAsync(item.Id, sent, error, CancellationToken.None);
            _log.LogInformation("Scheduled broadcast {Id} ({Audience}) sent to {Count} players.", item.Id, item.Audience, sent);
        }
    }
}
