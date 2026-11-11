using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Core.Matches;

/// <summary>Runs the match transports that own a socket, the matchmaker loop and the timeout sweeper.</summary>
public sealed class MatchHostedService : BackgroundService
{
    private readonly IReadOnlyList<IMatchTransport> _transports;
    private readonly MatchService _matches;
    private readonly Matchmaker _matchmaker;
    private readonly ILogger<MatchHostedService> _log;

    public MatchHostedService(IEnumerable<IMatchTransport> transports, MatchService matches, Matchmaker matchmaker, ILogger<MatchHostedService> log)
    {
        _transports = transports.ToArray();
        _matches = matches;
        _matchmaker = matchmaker;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Match transports: {Transports}",
            _transports.Count == 0 ? "(none)" : string.Join(", ", _transports.Select(t => t.Name)));

        var tasks = _transports.Select(t => RunTransportAsync(t, stoppingToken)).ToList();
        tasks.Add(SweepLoopAsync(stoppingToken));
        tasks.Add(_matchmaker.RunAsync(stoppingToken));

        try
        {
            var finished = await Task.WhenAny(tasks);
            await finished;
        }
        finally
        {
            _matches.EndAll("server_stopping");
        }
    }

    private async Task RunTransportAsync(IMatchTransport transport, CancellationToken ct)
    {
        try
        {
            await transport.RunAsync(ct);
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Match transport '{Name}' failed.", transport.Name);
            throw;
        }
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    _matches.Sweep();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Sweeping matches failed.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
