namespace aida.Components.Pages.Chat;

/// <summary>
/// Streaming arrives one token at a time, so rendering every update floods the circuit.
/// Callers append text unconditionally and only re-render when this gate opens.
/// </summary>
internal sealed class StreamRenderThrottle
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    private readonly long intervalMs;
    private long? lastRenderedMs;

    public StreamRenderThrottle(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Render interval must be positive.");
        intervalMs = (long)interval.TotalMilliseconds;
    }

    /// <summary>
    /// True when a render is due at <paramref name="nowMs"/>; a true answer reserves that slot.
    /// </summary>
    public bool TryAcquire(long nowMs)
    {
        if (lastRenderedMs is { } last && nowMs - last < intervalMs)
            return false;
        lastRenderedMs = nowMs;
        return true;
    }

    public void Reset() => lastRenderedMs = null;
}
