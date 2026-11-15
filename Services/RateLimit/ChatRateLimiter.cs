using System.Collections.Concurrent;
using StackExchange.Redis;

namespace aida.Services.RateLimit;

public interface IChatRateLimiter
{
    Task<bool> TryAcquireAsync(string userId, CancellationToken ct = default);
}

internal class ChatRateLimiter : IChatRateLimiter
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly int _limit;
    private readonly ILogger<ChatRateLimiter> _logger;

    private readonly ConcurrentDictionary<string, (long Count, DateTimeOffset StartsAt)> _memory = new();

    public ChatRateLimiter(
        IConnectionMultiplexer? redis,
        IConfiguration configuration,
        ILogger<ChatRateLimiter> logger)
    {
        _redis = redis;
        _limit = configuration.GetValue("RateLimit:ChatPerMinute", 20);
        _logger = logger;
    }

    internal virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public async Task<bool> TryAcquireAsync(string userId, CancellationToken ct = default)
    {
        if (_limit <= 0) return true;

        return _redis is null
            ? TryAcquireInMemory(userId)
            : await TryAcquireInRedisAsync(userId, ct);
    }

    private bool TryAcquireInMemory(string userId)
    {
        var now = UtcNow;
        var entry = _memory.AddOrUpdate(
            userId,
            _ => (1L, now),
            (_, existing) => now - existing.StartsAt >= TimeSpan.FromMinutes(1)
                ? (1L, now)
                : (existing.Count + 1, existing.StartsAt));

        return entry.Count <= _limit;
    }

    private async Task<bool> TryAcquireInRedisAsync(string userId, CancellationToken ct)
    {
        try
        {
            var db = _redis!.GetDatabase();
            var key = $"aida:rl:chat:{userId}:{UtcNow:yyyyMMddHHmm}";
            var count = await db.StringIncrementAsync(key);
            if (count == 1)
                await db.KeyExpireAsync(key, TimeSpan.FromSeconds(70));
            return count <= _limit;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis rate-limit check failed; allowing the send.");
            return true;
        }
    }
}
