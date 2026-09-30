using System;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Events;

namespace Shoko.Plugin.Anilist.Api;

/// <summary>
/// Paces every request to AniList. A local sliding window smooths the plugin's
/// own rate, a server-driven backoff honours 429s and the quota headers, and a
/// circuit breaker pauses everything on the first server error.
/// </summary>
/// <remarks>
/// The breaker trips on the very first 5XX because AniList has been fragile,
/// and a client that keeps knocking during an outage makes it worse. Queued
/// work waits for the pause to lift and then resumes, so nothing is lost.
/// </remarks>
public sealed class AnilistRateLimiter : IDisposable
{
    /// <summary>
    /// The length of the window AniList's advertised limit applies to.
    /// </summary>
    private static readonly TimeSpan _serverWindow = TimeSpan.FromMinutes(1);

    private readonly ILogger<AnilistRateLimiter> _logger;

    private readonly ConfigurationProvider<AnilistConfiguration> _configurationProvider;

    // Errors within this window of the previous pause escalate the level instead of restarting at 1.
    private readonly long _errorWindowTicks;

    // Guards the pause level and every write to the backoff deadline, so the two are always
    // updated together. Reads elsewhere go through Interlocked.Read.
    private readonly Lock _breakerLock = new();

    private readonly CancellationTokenSource _disposeCts = new();

    private volatile SlidingWindowRateLimiter _limiter;

    private volatile int _maxRequestsPerWindow;

    private volatile int _configuredMaxRequestsPerWindow;

    private volatile int _windowDurationMs;

    private volatile int _serverLimit = -1;

    private volatile int _pauseLevel;

    private volatile bool _isPaused;

    private volatile string? _pauseReason;

    private volatile int _remainingRequests = -1;

    private long _backoffUntilTicks;

    private long _lastErrorTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnilistRateLimiter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="configurationProvider">The plugin's configuration, read for the local window.</param>
    public AnilistRateLimiter(ILogger<AnilistRateLimiter> logger, ConfigurationProvider<AnilistConfiguration> configurationProvider)
        : this(logger, configurationProvider, TimeSpan.FromMinutes(5)) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="AnilistRateLimiter"/> class
    /// with a custom error window, for tests.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="configurationProvider">The plugin's configuration, read for the local window.</param>
    /// <param name="errorWindow">How soon after a pause another error escalates the next pause.</param>
    internal AnilistRateLimiter(ILogger<AnilistRateLimiter> logger, ConfigurationProvider<AnilistConfiguration> configurationProvider, TimeSpan errorWindow)
    {
        _logger = logger;
        _configurationProvider = configurationProvider;
        _errorWindowTicks = errorWindow.Ticks;
        var settings = configurationProvider.Load().RateLimit;
        _configuredMaxRequestsPerWindow = settings.MaxRequestsPerWindow;
        _windowDurationMs = settings.WindowDurationMs;
        _maxRequestsPerWindow = settings.MaxRequestsPerWindow;
        _limiter = CreateLimiter(settings.MaxRequestsPerWindow, settings.WindowDurationMs);
        _configurationProvider.Saved += OnConfigurationSaved;
    }

    /// <summary>
    /// Raised when <see cref="IsPaused"/> flips either way.
    /// </summary>
    public event EventHandler? PauseStateChanged;

    #region State

    /// <summary>
    /// The request limit per window enforced locally, after applying the limit
    /// AniList advertises in its <c>X-RateLimit-Limit</c> header.
    /// </summary>
    public int MaxRequestsPerWindow => _maxRequestsPerWindow;

    /// <summary>
    /// Requests left in AniList's own quota window, as last reported by its
    /// <c>X-RateLimit-Remaining</c> header, or <see langword="null"/> before the
    /// first response.
    /// </summary>
    public int? RemainingRequests => _remainingRequests < 0 ? null : _remainingRequests;

    /// <summary>
    /// Whether the circuit breaker is tripped, which the provider reports as
    /// its pause, so the core holds back every job of it.
    /// </summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Why the breaker is tripped, in words a person can read, or
    /// <see langword="null"/> while it is not.
    /// </summary>
    public string? PauseReason => _isPaused ? _pauseReason : null;

    /// <summary>
    /// Time left on the current backoff, or <see langword="null"/> when there is none.
    /// </summary>
    public TimeSpan? RemainingPauseTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _backoffUntilTicks);
            if (ticks == 0)
                return null;

            var remaining = new DateTimeOffset(ticks, TimeSpan.Zero) - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    /// <summary>
    /// The raw backoff deadline, for tests.
    /// </summary>
    internal long BackoffUntilTicks => Interlocked.Read(ref _backoffUntilTicks);

    /// <summary>
    /// The pause state as one consistent snapshot.
    /// </summary>
    /// <returns>Whether anything is paused, for how long, and the server quota left.</returns>
    public (bool IsPaused, TimeSpan? Remaining, int? RemainingRequests) GetPauseSnapshot()
    {
        lock (_breakerLock)
        {
            var ticks = _backoffUntilTicks;
            var remaining = ticks == 0 ? (TimeSpan?)null : new DateTimeOffset(ticks, TimeSpan.Zero) - DateTimeOffset.UtcNow;
            remaining = remaining > TimeSpan.Zero ? remaining : null;
            return (_isPaused || remaining is not null, remaining, RemainingRequests);
        }
    }

    #endregion

    #region Pacing

    /// <summary>
    /// Takes a slot in the window, waiting out any backoff, and then runs the
    /// action.
    /// </summary>
    /// <typeparam name="T">What the action returns.</typeparam>
    /// <param name="action">The request to make.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the action returned.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled, or the limiter disposed.</exception>
    public async Task<T> EnsureRateAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        while (true)
        {
            await WaitForBackoffAsync(linked.Token).ConfigureAwait(false);
            using var lease = await _limiter.AcquireAsync(1, linked.Token).ConfigureAwait(false);

            // A 429 may have landed between the wait and the lease. Give the
            // slot back and wait again rather than sitting on it.
            var backoffTicks = Interlocked.Read(ref _backoffUntilTicks);
            if (backoffTicks > 0 && DateTimeOffset.UtcNow.UtcTicks < backoffTicks)
                continue;

            return await action().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for as long as the breaker is tripped or a backoff is running,
    /// without taking a slot.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes once AniList may be asked again.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task WaitWhilePausedAsync(CancellationToken cancellationToken = default)
    {
        while (_isPaused || RemainingPauseTime is not null)
        {
            var wait = RemainingPauseTime ?? TimeSpan.FromSeconds(1);
            await Task.Delay(wait + Jitter(), cancellationToken).ConfigureAwait(false);

            // A breaker whose deadline passed without a success to reset it
            // would otherwise hold everything back for good.
            if (_isPaused && RemainingPauseTime is null)
                ExpirePause();
        }
    }

    /// <summary>
    /// A little randomness to spread out requests that were all held back by
    /// the same pause.
    /// </summary>
    /// <returns>Up to a quarter of a second.</returns>
    internal static TimeSpan Jitter()
        => TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));

    private async Task WaitForBackoffAsync(CancellationToken cancellationToken)
    {
        // A concurrent 429 can push the deadline forward mid-wait, hence the loop.
        while (true)
        {
            var backoffTicks = Interlocked.Read(ref _backoffUntilTicks);
            if (backoffTicks == 0 || DateTimeOffset.UtcNow.UtcTicks >= backoffTicks)
                return;

            var wait = new DateTimeOffset(backoffTicks, TimeSpan.Zero) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                var jitter = Jitter();
                _logger.LogTrace("AniList server backoff active. Waiting {Wait}ms", (wait + jitter).TotalMilliseconds);
                await Task.Delay(wait + jitter, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    #endregion

    #region Signals

    /// <summary>
    /// Records a 429. Every request waits until the backoff runs out.
    /// </summary>
    /// <param name="retryAfter">How long to back off; a minute, AniList's quota window, when left out.</param>
    public void NotifyRateLimitExceeded(TimeSpan? retryAfter)
        => SetBackoff(DateTimeOffset.UtcNow + (retryAfter ?? TimeSpan.FromSeconds(60)), "AniList rate limit exceeded");

    /// <summary>
    /// Records the quota headers of a response, backing off until the reset
    /// once the quota is spent, and lowering the local window when AniList
    /// advertises less than is configured.
    /// </summary>
    /// <param name="limit">The <c>X-RateLimit-Limit</c> value, if present.</param>
    /// <param name="remaining">The <c>X-RateLimit-Remaining</c> value.</param>
    /// <param name="resetAt">The <c>X-RateLimit-Reset</c> value, if present.</param>
    public void NotifyQuota(int? limit, int remaining, DateTimeOffset? resetAt)
    {
        if (limit is > 0 && limit.Value != _serverLimit)
        {
            _serverLimit = limit.Value;
            var effective = EffectiveLimit();
            if (effective != _maxRequestsPerWindow)
                _logger.LogInformation("AniList advertises {ServerLimit} requests per minute. Using {Effective} per {Window}s window locally.", limit.Value, effective, _windowDurationMs / 1000d);
            ReplaceLimiter(effective);
        }

        _remainingRequests = Math.Max(remaining, 0);
        if (remaining > 0 || resetAt is null)
            return;

        SetBackoff(resetAt.Value, "AniList request quota exhausted");
    }

    /// <summary>
    /// Records a server error. The first one trips the breaker; more while it
    /// is tripped are absorbed, and one soon after a pause lifted makes the
    /// next pause longer.
    /// </summary>
    public void Notify5xxError()
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        TimeSpan duration;
        lock (_breakerLock)
        {
            // Requests already in flight when the breaker tripped fail too; they do not escalate.
            if (_isPaused)
                return;

            // A fresh error long after the last one starts the ramp over; a quick repeat escalates it.
            if (_lastErrorTicks != 0 && now - _lastErrorTicks >= _errorWindowTicks)
                _pauseLevel = 0;
            _lastErrorTicks = now;

            var nextLevel = Math.Min(_pauseLevel + 1, 5);
            duration = GetPauseDuration(nextLevel);
            var newTicks = (DateTimeOffset.UtcNow + duration).UtcTicks;

            _pauseLevel = nextLevel;
            if (newTicks > Interlocked.Read(ref _backoffUntilTicks))
                Interlocked.Exchange(ref _backoffUntilTicks, newTicks);

            _pauseReason = nextLevel > 1
                ? $"AniList answered with server errors again soon after the last pause, so its work is paused for {(int)duration.TotalMinutes} minutes."
                : $"AniList answered with a server error, so its work is paused for {(int)duration.TotalMinutes} minute(s).";
            _isPaused = true;
        }

        _logger.LogInformation("AniList is temporarily unavailable. All AniList work paused for {Duration} minutes. It will resume automatically.", (int)duration.TotalMinutes);
        PauseStateChanged?.Invoke(this, EventArgs.Empty);
        SchedulePauseExpiry(duration);
    }

    /// <summary>
    /// Records a successful request, which resets the ramp once a pause has
    /// run out, so the next outage starts at the shortest pause again.
    /// </summary>
    public void NotifySuccess()
    {
        if (_pauseLevel == 0)
            return;

        var resumed = false;
        lock (_breakerLock)
        {
            if (_pauseLevel == 0)
                return;

            var backoffTicks = Interlocked.Read(ref _backoffUntilTicks);
            if (backoffTicks > 0 && DateTimeOffset.UtcNow.UtcTicks < backoffTicks)
                return;

            Interlocked.Exchange(ref _backoffUntilTicks, 0);
            _pauseLevel = 0;
            _lastErrorTicks = 0;
            if (_isPaused)
            {
                _isPaused = false;
                resumed = true;
            }
        }

        if (!resumed)
            return;

        _logger.LogInformation("AniList is available again. Queued AniList work will now resume.");
        PauseStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// How long a pause lasts at each level of the ramp.
    /// </summary>
    /// <param name="level">The level, from 1.</param>
    /// <returns>The pause.</returns>
    internal static TimeSpan GetPauseDuration(int level) => level switch
    {
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(3),
        3 => TimeSpan.FromMinutes(5),
        4 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(60),
    };

    private void SetBackoff(DateTimeOffset until, string reason)
    {
        var newTicks = until.UtcTicks;
        lock (_breakerLock)
        {
            if (newTicks <= Interlocked.Read(ref _backoffUntilTicks))
                return;

            Interlocked.Exchange(ref _backoffUntilTicks, newTicks);
        }

        _logger.LogTrace("{Reason}. Backing off until {Until}", reason, until);
    }

    private void SchedulePauseExpiry(TimeSpan duration)
        => _ = Task.Delay(duration, _disposeCts.Token)
            .ContinueWith(_ => ExpirePause(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    private void ExpirePause()
    {
        lock (_breakerLock)
        {
            if (!_isPaused)
                return;

            // A 429 during the pause pushed the deadline further out. Nothing
            // polls the breaker, so the expiry has to be rearmed for it.
            var backoffTicks = Interlocked.Read(ref _backoffUntilTicks);
            if (backoffTicks > DateTimeOffset.UtcNow.UtcTicks)
            {
                SchedulePauseExpiry(new DateTimeOffset(backoffTicks, TimeSpan.Zero) - DateTimeOffset.UtcNow);
                return;
            }

            Interlocked.Exchange(ref _backoffUntilTicks, 0);
            _isPaused = false;
        }

        _logger.LogInformation("AniList pause expired. Queued AniList work will now resume.");
        PauseStateChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Window

    // The configured limit is a pace the user chose; the server's limit, scaled from its one-minute
    // window down to ours, is the ceiling never gone over.
    private int EffectiveLimit()
    {
        if (_serverLimit <= 0)
            return _configuredMaxRequestsPerWindow;

        var serverAllowance = Math.Max(1, (int)Math.Floor(_serverLimit * (_windowDurationMs / _serverWindow.TotalMilliseconds)));
        return Math.Min(_configuredMaxRequestsPerWindow, serverAllowance);
    }

    private void ReplaceLimiter(int maxRequests)
    {
        if (maxRequests == _maxRequestsPerWindow)
            return;

        _maxRequestsPerWindow = maxRequests;
        var oldLimiter = _limiter;
        _limiter = CreateLimiter(maxRequests, _windowDurationMs);

        // Leave the old limiter to any lease still in flight for a while before disposing it.
        _ = Task.Delay(TimeSpan.FromSeconds(15))
            .ContinueWith(_ => oldLimiter.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static SlidingWindowRateLimiter CreateLimiter(int maxRequests, int windowMs)
        => new(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = maxRequests,
            Window = TimeSpan.FromMilliseconds(windowMs),
            SegmentsPerWindow = 12,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = int.MaxValue,
            AutoReplenishment = true,
        });

    private void OnConfigurationSaved(object? sender, ConfigurationSavedEventArgs<AnilistConfiguration> eventArgs)
    {
        var settings = eventArgs.Configuration.RateLimit;
        _configuredMaxRequestsPerWindow = settings.MaxRequestsPerWindow;
        var windowChanged = _windowDurationMs != settings.WindowDurationMs;
        _windowDurationMs = settings.WindowDurationMs;
        if (windowChanged)
            _maxRequestsPerWindow = -1;

        ReplaceLimiter(EffectiveLimit());
    }

    #endregion

    /// <summary>
    /// Stops listening for configuration changes and cancels every wait.
    /// </summary>
    public void Dispose()
    {
        _configurationProvider.Saved -= OnConfigurationSaved;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _limiter.Dispose();
    }
}
