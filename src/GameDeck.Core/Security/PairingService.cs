using System.Security.Cryptography;
using System.Text;

namespace GameDeck.Core.Security;

public enum PairOutcome { Success, Invalid, LockedOut }

public sealed record PairAttempt(PairOutcome Outcome, string? Token = null, TimeSpan RetryAfter = default);

/// <summary>
/// Short-lived, single-use 6-digit pairing codes shown only on the PC. A correct code is exchanged
/// for a 256-bit session token; only the token's SHA-256 is persisted. Repeated failures lock pairing
/// and rotate the code, which keeps brute force impractical.
/// </summary>
public sealed class PairingService
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private string _code = "";
    private DateTime _expiresUtc;
    private int _failures;
    private DateTime _lockedUntilUtc;

    /// <summary>Raised when the code rotates (expiry, use, lockout or manual refresh).</summary>
    public event Action? CodeChanged;

    public PairingService(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        lock (_gate) RotateLocked();
    }

    public (string Code, DateTime ExpiresUtc) GetCurrentCode()
    {
        bool rotated;
        (string, DateTime) result;
        lock (_gate)
        {
            rotated = _clock() >= _expiresUtc;
            if (rotated) RotateLocked();
            result = (_code, _expiresUtc);
        }
        if (rotated) CodeChanged?.Invoke();
        return result;
    }

    public string Regenerate()
    {
        string code;
        lock (_gate)
        {
            RotateLocked();
            code = _code;
        }
        CodeChanged?.Invoke();
        return code;
    }

    public PairAttempt TryPair(string? code)
    {
        PairAttempt result;
        var rotated = false;
        lock (_gate)
        {
            var now = _clock();
            if (now < _lockedUntilUtc)
                return new PairAttempt(PairOutcome.LockedOut, RetryAfter: _lockedUntilUtc - now);

            var normalized = new string((code ?? "").Where(char.IsDigit).ToArray());
            var valid = now < _expiresUtc
                && normalized.Length == _code.Length
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(normalized), Encoding.ASCII.GetBytes(_code));

            if (valid)
            {
                _failures = 0;
                RotateLocked(); // single use
                rotated = true;
                result = new PairAttempt(PairOutcome.Success, NewToken());
            }
            else if (++_failures >= MaxFailures)
            {
                _failures = 0;
                _lockedUntilUtc = now + LockoutDuration;
                RotateLocked();
                rotated = true;
                result = new PairAttempt(PairOutcome.LockedOut, RetryAfter: LockoutDuration);
            }
            else
            {
                result = new PairAttempt(PairOutcome.Invalid);
            }
        }
        if (rotated) CodeChanged?.Invoke();
        return result;
    }

    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool HashMatches(string token, string storedHash)
    {
        var computed = Encoding.ASCII.GetBytes(HashToken(token));
        var stored = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    private void RotateLocked()
    {
        _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _expiresUtc = _clock() + CodeLifetime;
    }
}
