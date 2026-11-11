using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GameServer.Core.Identity;

public sealed class PhoneOptions
{
    /// <summary>Off by default. Turning it on without a real <see cref="ISmsSender"/> makes begin return sms_not_configured.</summary>
    public bool Enabled { get; set; }
    public int CodeTtlSeconds { get; set; } = 300;
    public int CooldownSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>Delivers a one-time code. Plug your SMS gateway in by registering your own implementation.</summary>
public interface ISmsSender
{
    Task SendAsync(string phoneE164, string message, CancellationToken ct);
}

/// <summary>Development only: writes the code to the server log instead of sending an SMS.</summary>
public sealed class LoggingSmsSender : ISmsSender
{
    private readonly ILogger<LoggingSmsSender> _log;
    public LoggingSmsSender(ILogger<LoggingSmsSender> log) => _log = log;

    public Task SendAsync(string phoneE164, string message, CancellationToken ct)
    {
        _log.LogWarning("DEV SMS to {Phone}: {Message}", phoneE164, message);
        return Task.CompletedTask;
    }
}

/// <summary>Default outside Development: no SMS gateway has been wired up yet.</summary>
public sealed class NotConfiguredSmsSender : ISmsSender
{
    public Task SendAsync(string phoneE164, string message, CancellationToken ct) =>
        throw new GameException("sms_not_configured", "SMS delivery is not configured on this server.");
}

/// <summary>
/// Phone-number login with a one-time code. Flow: POST /v1/auth/phone/begin {credential:{phone}} sends a code,
/// then POST /v1/auth/phone {credential:{phone, code}} verifies it. The infrastructure is complete; only the
/// SMS gateway (ISmsSender) is left to implement.
/// </summary>
public sealed partial class PhoneIdentityProvider : IIdentityProvider
{
    public const string ProviderName = "phone";

    private readonly Npgsql.NpgsqlDataSource _db;
    private readonly PhoneOptions _options;
    private readonly ISmsSender _sms;
    private readonly byte[] _hmacKey;

    public PhoneIdentityProvider(NpgsqlDataSource db, IOptions<PhoneOptions> options, ISmsSender sms, IOptions<JwtOptions> jwt)
    {
        _db = db;
        _options = options.Value;
        _sms = sms;
        // Codes are hashed with a key derived from the JWT secret, so a database leak alone doesn't reveal live codes.
        _hmacKey = SHA256.HashData(Encoding.UTF8.GetBytes("phone-otp:" + jwt.Value.Secret));
    }

    public string Name => ProviderName;
    public bool Enabled => _options.Enabled;

    public async Task BeginAsync(JsonElement request, CancellationToken ct)
    {
        var phone = NormalizePhone(JsonHelpers.GetString(request, "phone"));
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        // Upsert, but refuse (0 rows) if a code was sent too recently for this number.
        await using var cmd = _db.CreateCommand("""
            INSERT INTO phone_otps (phone, code_hash, expires_at, attempts, last_sent_at)
            VALUES (@p, @h, now() + make_interval(secs => @ttl), 0, now())
            ON CONFLICT (phone) DO UPDATE
               SET code_hash = EXCLUDED.code_hash,
                   expires_at = EXCLUDED.expires_at,
                   attempts = 0,
                   last_sent_at = now()
             WHERE phone_otps.last_sent_at < now() - make_interval(secs => @cool)
            """);
        cmd.Parameters.AddWithValue("p", phone);
        cmd.Parameters.AddWithValue("h", Hash(phone, code));
        cmd.Parameters.AddWithValue("ttl", (double)_options.CodeTtlSeconds);
        cmd.Parameters.AddWithValue("cool", (double)_options.CooldownSeconds);

        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw new GameException("too_soon", "Please wait before requesting another code.");

        await _sms.SendAsync(phone, $"Your login code is {code}", ct);
    }

    public async Task<VerifiedIdentity> VerifyAsync(JsonElement credential, CancellationToken ct)
    {
        var phone = NormalizePhone(JsonHelpers.GetString(credential, "phone"));
        var code = (JsonHelpers.GetString(credential, "code") ?? "").Trim();
        if (!CodeRegex().IsMatch(code))
            throw new GameException("invalid_code", "The code is not valid.");

        // Count the attempt first (atomically), so brute-forcing is limited even with parallel requests.
        string? hash;
        await using (var attempt = _db.CreateCommand("""
            UPDATE phone_otps SET attempts = attempts + 1
             WHERE phone = @p AND expires_at > now() AND attempts < @max
            RETURNING code_hash
            """))
        {
            attempt.Parameters.AddWithValue("p", phone);
            attempt.Parameters.AddWithValue("max", _options.MaxAttempts);
            hash = await attempt.ExecuteScalarAsync(ct) as string;
        }

        if (hash is null)
            throw new GameException("invalid_code", "The code is not valid.");

        var expected = Encoding.UTF8.GetBytes(hash);
        var actual = Encoding.UTF8.GetBytes(Hash(phone, code));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new GameException("invalid_code", "The code is not valid.");

        // One use only.
        await using (var delete = _db.CreateCommand("DELETE FROM phone_otps WHERE phone = @p"))
        {
            delete.Parameters.AddWithValue("p", phone);
            await delete.ExecuteNonQueryAsync(ct);
        }

        return new VerifiedIdentity(ProviderName, phone, Mask(phone));
    }

    private string Hash(string phone, string code) =>
        Convert.ToHexString(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(phone + ":" + code)));

    /// <summary>Accepts Persian/Arabic digits, spaces and dashes; local Iranian 09xxxxxxxxx becomes +989xxxxxxxxx.</summary>
    internal static string NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new GameException("invalid_phone", "A phone number is required.");

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.Trim())
        {
            if (c >= '0' && c <= '9') sb.Append(c);
            else if (c >= '۰' && c <= '۹') sb.Append((char)('0' + (c - '۰'))); // Persian digits
            else if (c >= '٠' && c <= '٩') sb.Append((char)('0' + (c - '٠'))); // Arabic-Indic digits
            else if (c == '+' && sb.Length == 0) sb.Append('+');
            else if (c is ' ' or '-' or '(' or ')') continue;
            else throw new GameException("invalid_phone", "The phone number is not valid.");
        }

        var s = sb.ToString();
        if (s.StartsWith("00")) s = "+" + s[2..];
        else if (s.StartsWith("09") && s.Length == 11) s = "+98" + s[1..];
        else if (s.StartsWith("98") && s.Length == 12) s = "+" + s;

        if (!E164Regex().IsMatch(s))
            throw new GameException("invalid_phone", "The phone number is not valid. Use international format, e.g. +989121234567.");
        return s;
    }

    private static string Mask(string phone) =>
        phone.Length <= 7 ? phone : phone[..4] + new string('*', phone.Length - 7) + phone[^3..];

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}\z")]
    private static partial Regex E164Regex();

    [GeneratedRegex(@"^[0-9]{6}\z")]
    private static partial Regex CodeRegex();
}
