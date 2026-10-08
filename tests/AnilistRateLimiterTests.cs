using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Abstractions.Connectivity.Suspensions;
using Shoko.Plugin.Anilist.Api;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

public class AnilistRateLimiterTests
{
    [Fact]
    public async Task CallsWithinWindow_ProceedImmediately()
    {
        using var limiter = CreateRateLimiter(maxRequests: 3, windowMs: 1000);
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < 3; i++)
            await limiter.EnsureRateAsync(() => Task.FromResult(0), TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(200), $"Expected < 200ms, got {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task ExtraCall_WaitsForWindowSlot()
    {
        using var limiter = CreateRateLimiter(maxRequests: 2, windowMs: 1000);
        await limiter.EnsureRateAsync(() => Task.FromResult(0), TestContext.Current.CancellationToken);
        await limiter.EnsureRateAsync(() => Task.FromResult(0), TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await limiter.EnsureRateAsync(() => Task.FromResult(0), TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(300), $"Expected a wait, got {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task RateLimitExceeded_PausesUntilRetryAfter()
    {
        using var limiter = CreateRateLimiter(maxRequests: 10, windowMs: 1000);
        limiter.NotifyRateLimitExceeded(TimeSpan.FromMilliseconds(300));

        var stopwatch = Stopwatch.StartNew();
        await limiter.EnsureRateAsync(() => Task.FromResult(0), TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(250), $"Expected a wait for the backoff, got {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task ExhaustedQuota_WithReset_PausesUntilReset()
    {
        using var limiter = CreateRateLimiter(maxRequests: 10, windowMs: 1000);
        limiter.NotifyQuota(null, 0, DateTimeOffset.UtcNow.AddMilliseconds(300));

        Assert.Equal(0, limiter.RemainingRequests);
        var stopwatch = Stopwatch.StartNew();
        await limiter.EnsureRateAsync(() => Task.FromResult(0), TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(250), $"Expected a wait for the reset, got {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task RemainingQuota_DoesNotPause()
    {
        using var limiter = CreateRateLimiter(maxRequests: 10, windowMs: 1000);
        limiter.NotifyQuota(null, 5, DateTimeOffset.UtcNow.AddMinutes(1));

        var stopwatch = Stopwatch.StartNew();
        await limiter.EnsureRateAsync(() => Task.FromResult(0), TestContext.Current.CancellationToken);

        Assert.Equal(5, limiter.RemainingRequests);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(200), $"Expected no wait, got {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public void FirstServerError_TripsBreaker_AndIsReported()
    {
        var reporter = new FakeSuspensionReporter<AnilistSuspensionProvider>();
        using var limiter = CreateRateLimiter(reporter: reporter);

        limiter.Notify5xxError();

        Assert.True(limiter.IsPaused);
        Assert.True(limiter.GetPauseSnapshot().IsPaused);
        Assert.True(limiter.BackoffUntilTicks > DateTimeOffset.UtcNow.UtcTicks);
        Assert.Equal(new DateTime(limiter.BackoffUntilTicks, DateTimeKind.Utc), reporter.Active[SuspensionKind.ServerErrors].ResumesAt);
    }

    [Fact]
    public void ServerErrorsWhilePaused_DoNotEscalate_ButALongerRetryAfterLengthensThePause()
    {
        var reporter = new FakeSuspensionReporter<AnilistSuspensionProvider>();
        using var limiter = CreateRateLimiter(reporter: reporter);

        limiter.Notify5xxError();
        var firstDeadline = limiter.BackoffUntilTicks;
        limiter.Notify5xxError();
        limiter.Notify5xxError(TimeSpan.FromSeconds(1));

        Assert.Equal(firstDeadline, limiter.BackoffUntilTicks);

        limiter.Notify5xxError(TimeSpan.FromMinutes(10));

        Assert.True(limiter.BackoffUntilTicks > firstDeadline);
        Assert.Equal(new DateTime(limiter.BackoffUntilTicks, DateTimeKind.Utc), reporter.Active[SuspensionKind.ServerErrors].ResumesAt);
    }

    [Fact]
    public void ARateLimit_IsReported_AndAShorterOneNeverCutsItShort()
    {
        var reporter = new FakeSuspensionReporter<AnilistSuspensionProvider>();
        using var limiter = CreateRateLimiter(reporter: reporter);

        limiter.NotifyRateLimitExceeded(TimeSpan.FromMinutes(5));
        var deadline = reporter.Active[SuspensionKind.RateLimited].ResumesAt;
        limiter.NotifyRateLimitExceeded(TimeSpan.FromSeconds(5));
        limiter.NotifyQuota(null, 0, DateTimeOffset.UtcNow.AddSeconds(5));

        Assert.False(limiter.IsPaused);
        Assert.Equal(deadline, reporter.Active[SuspensionKind.RateLimited].ResumesAt);
        Assert.Equal(new DateTime(limiter.BackoffUntilTicks, DateTimeKind.Utc), deadline);
        Assert.DoesNotContain(SuspensionKind.ServerErrors, reporter.Active.Keys);
    }

    [Fact]
    public async Task WaitWhilePaused_ReturnsAtOnce_WhenNothingIsPaused()
    {
        using var limiter = CreateRateLimiter();
        var stopwatch = Stopwatch.StartNew();

        await limiter.WaitWhilePausedAsync(TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task WaitWhilePaused_HoldsUntilTheBackoffRunsOut()
    {
        using var limiter = CreateRateLimiter();
        limiter.NotifyRateLimitExceeded(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await limiter.WaitWhilePausedAsync(TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void Defaults_AreOneRequestPerFourSeconds()
    {
        var settings = new AnilistConfiguration().RateLimit;

        Assert.Equal(1, settings.MaxRequestsPerWindow);
        Assert.Equal(4000, settings.WindowDurationMs);
    }

    [Fact]
    public void AdvertisedLimit_IsScaledToTheLocalWindow_AndOnlyLowers()
    {
        // 10 per 4s locally is 150 a minute; AniList advertising 30 a minute allows 2 per 4s.
        using var limiter = CreateRateLimiter(maxRequests: 10, windowMs: 4000);
        limiter.NotifyQuota(30, 29, null);
        Assert.Equal(2, limiter.MaxRequestsPerWindow);

        limiter.NotifyQuota(900, 899, null);
        Assert.Equal(10, limiter.MaxRequestsPerWindow);

        limiter.NotifyQuota(1, 0, null);
        Assert.Equal(1, limiter.MaxRequestsPerWindow);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    [InlineData(5, 60)]
    [InlineData(9, 60)]
    public void GetPauseDuration_Escalates_AndCaps(int level, int expectedMinutes)
        => Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), AnilistRateLimiter.GetPauseDuration(level));

    internal static AnilistRateLimiter CreateRateLimiter(
        int maxRequests = 3,
        int windowMs = 1000,
        int errorWindowMs = 10_000,
        FakeSuspensionReporter<AnilistSuspensionProvider>? reporter = null
    )
    {
        var configuration = new AnilistConfiguration();
        configuration.RateLimit.MaxRequestsPerWindow = maxRequests;
        configuration.RateLimit.WindowDurationMs = windowMs;
        return new AnilistRateLimiter(NullLogger<AnilistRateLimiter>.Instance, TestHarness.CreateConfigurationProvider(configuration), TimeSpan.FromMilliseconds(errorWindowMs), reporter);
    }
}
