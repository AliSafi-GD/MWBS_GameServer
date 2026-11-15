namespace GameServer.Core;

public static class TimeProviderExtensions
{
    /// <summary>
    /// "Now" as a UTC <see cref="DateTime"/> (Kind = Utc), the type the server uses everywhere.
    /// Read time only through the injected <see cref="TimeProvider"/>, never <c>DateTime.UtcNow</c>, so tests can control it.
    /// </summary>
    public static DateTime UtcNow(this TimeProvider time) => time.GetUtcNow().UtcDateTime;
}
