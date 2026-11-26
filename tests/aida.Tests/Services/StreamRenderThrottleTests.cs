using aida.Components.Pages.Chat;

namespace aida.Tests.Services;

public class StreamRenderThrottleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void First_render_is_allowed()
        => Assert.True(new StreamRenderThrottle(Interval).TryAcquire(0));

    [Fact]
    public void Render_inside_the_interval_is_blocked()
    {
        var throttle = new StreamRenderThrottle(Interval);
        throttle.TryAcquire(0);

        Assert.False(throttle.TryAcquire(99));
    }

    [Fact]
    public void Render_after_the_interval_is_allowed()
    {
        var throttle = new StreamRenderThrottle(Interval);
        throttle.TryAcquire(0);

        Assert.True(throttle.TryAcquire(100));
    }

    [Fact]
    public void Thousand_tokens_at_five_ms_batch_into_fifty_renders()
    {
        var throttle = new StreamRenderThrottle(Interval);
        var renders = 0;

        for (var token = 0; token < 1000; token++)
        {
            if (throttle.TryAcquire(token * 5))
                renders++;
        }

        Assert.Equal(50, renders);
    }

    [Fact]
    public void Reset_allows_an_immediate_render_for_the_next_response()
    {
        var throttle = new StreamRenderThrottle(Interval);
        throttle.TryAcquire(0);

        throttle.Reset();

        Assert.True(throttle.TryAcquire(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void Non_positive_interval_is_rejected(int milliseconds)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamRenderThrottle(TimeSpan.FromMilliseconds(milliseconds)));
}
