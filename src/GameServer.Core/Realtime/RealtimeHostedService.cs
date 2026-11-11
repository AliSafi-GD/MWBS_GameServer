using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Core.Realtime;

/// <summary>Runs the enabled transports and closes connections that never sign in or went silent.</summary>
public sealed class RealtimeHostedService : BackgroundService
{
    private readonly IReadOnlyList<ITransport> _transports;
    private readonly RealtimeHub _hub;
    private readonly ILogger<RealtimeHostedService> _log;

    public RealtimeHostedService(IEnumerable<ITransport> transports, RealtimeHub hub, ILogger<RealtimeHostedService> log)
    {
        _transports = transports.ToArray();
        _hub = hub;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_transports.Count == 0)
        {
            _log.LogInformation("Realtime is switched off (no transports enabled).");
            return;
        }

        _log.LogInformation("Realtime transports: {Transports}", string.Join(", ", _transports.Select(t => t.Name)));

        var tasks = _transports.Select(t => RunTransportAsync(t, stoppingToken)).ToList();
        tasks.Add(SweepLoopAsync(stoppingToken));

        // If any of them fails (for example a TCP port that is already taken), stop the server instead of running half-working.
        var finished = await Task.WhenAny(tasks);
        await finished;
    }

    private async Task RunTransportAsync(ITransport transport, CancellationToken ct)
    {
        try
        {
            await transport.RunAsync(ct);
            await Task.Delay(Timeout.Infinite, ct); // transports that only add endpoints have nothing to run
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Realtime transport '{Name}' failed.", transport.Name);
            throw;
        }
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await _hub.SweepAsync();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Sweeping idle connections failed.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
