using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class NetworkRpcRateLimiter
{
    private static readonly TimeSpan IdleRetention = TimeSpan.FromMinutes(5);
    private readonly ModNetworkRpcRateLimit _limit;
    private readonly long _frequency;
    private readonly long _idleRetentionTicks;
    private readonly Dictionary<long, Bucket> _buckets = [];

    internal NetworkRpcRateLimiter(ModNetworkRpcRateLimit limit, long frequency)
    {
        ArgumentNullException.ThrowIfNull(limit);
        if (frequency < 1) throw new ArgumentOutOfRangeException(nameof(frequency));
        _limit = limit;
        _frequency = frequency;
        _idleRetentionTicks = checked((long)Math.Ceiling(IdleRetention.TotalSeconds * frequency));
    }

    internal int BucketCount => _buckets.Count;

    internal bool TryConsume(long peerToken, long timestamp)
    {
        if (!_limit.Enabled) return true;
        if (!_buckets.TryGetValue(peerToken, out var bucket))
        {
            bucket = new Bucket(_limit.Burst, timestamp);
            _buckets.Add(peerToken, bucket);
        }
        else if (timestamp > bucket.LastTimestamp)
        {
            var elapsedSeconds = (timestamp - bucket.LastTimestamp) / (double)_frequency;
            bucket.Tokens = Math.Min(
                _limit.Burst,
                bucket.Tokens + elapsedSeconds * _limit.RefillPerSecond);
        }
        bucket.LastTimestamp = Math.Max(bucket.LastTimestamp, timestamp);
        bucket.LastSeenTimestamp = Math.Max(bucket.LastSeenTimestamp, timestamp);
        if (bucket.Tokens < 1) return false;
        bucket.Tokens -= 1;
        return true;
    }

    internal void RemoveIdle(long timestamp)
    {
        if (!_limit.Enabled || _buckets.Count == 0) return;
        foreach (var pair in _buckets.Where(value =>
                     timestamp - value.Value.LastSeenTimestamp >= _idleRetentionTicks).ToArray())
            _buckets.Remove(pair.Key);
    }

    internal void Clear() => _buckets.Clear();

    private sealed class Bucket(double tokens, long timestamp)
    {
        internal double Tokens { get; set; } = tokens;
        internal long LastTimestamp { get; set; } = timestamp;
        internal long LastSeenTimestamp { get; set; } = timestamp;
    }
}
