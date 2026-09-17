namespace MentorRecorder.Collector.Diagnostics;

/// <summary>
/// Exponentially weighted estimate of "messages per second", for the diagnostics page.
///
/// A raw counter cannot answer "is traffic still flowing"; a plain average over the whole
/// session cannot either, because it keeps reporting the healthy rate for minutes after the
/// stream has stopped. The exponential estimate reacts within a few samples while staying
/// steady under the natural burstiness of network traffic.
///
/// Time comes from a monotonic reading, never from two wall-clock readings subtracted: a
/// clock adjustment must not be able to produce a negative interval
/// (docs/architecture.md section 4, rule 6).
/// </summary>
public sealed class ExponentialRateEstimator
{
    /// <summary>Weight given to the newest sample.</summary>
    public const double DefaultSmoothing = 0.3;

    /// <summary>Shortest interval between two samples that is allowed to update the estimate.</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(250);

    private readonly double _smoothing;
    private readonly TimeSpan _minInterval;
    private readonly object _gate = new();
    private long _lastCount;
    private TimeSpan _lastAt;
    private double _rate;
    private bool _seeded;

    /// <summary>Creates an estimator.</summary>
    /// <param name="smoothing">Weight of the newest sample, in (0, 1].</param>
    /// <param name="minInterval">Samples closer together than this are ignored.</param>
    public ExponentialRateEstimator(double? smoothing = null, TimeSpan? minInterval = null)
    {
        var alpha = smoothing ?? DefaultSmoothing;
        if (alpha is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(smoothing), alpha, "smoothing must be in (0, 1]");
        }

        _smoothing = alpha;
        _minInterval = minInterval ?? DefaultMinInterval;
    }

    /// <summary>Current estimate, in messages per second.</summary>
    public double PerSecond
    {
        get
        {
            lock (_gate)
            {
                return _rate;
            }
        }
    }

    /// <summary>Forgets everything, so a new capture session starts from zero.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _seeded = false;
            _lastCount = 0;
            _lastAt = TimeSpan.Zero;
            _rate = 0;
        }
    }

    /// <summary>
    /// Feeds a cumulative counter and the monotonic reading it was taken at, and returns the
    /// updated estimate.
    /// </summary>
    /// <param name="totalCount">Cumulative message count.</param>
    /// <param name="mono">Monotonic reading, from a stopwatch.</param>
    public double Observe(long totalCount, TimeSpan mono)
    {
        lock (_gate)
        {
            if (!_seeded)
            {
                _seeded = true;
                _lastCount = totalCount;
                _lastAt = mono;
                return _rate;
            }

            var elapsed = mono - _lastAt;
            if (elapsed < _minInterval)
            {
                return _rate;
            }

            // A counter that went backwards means the session was reset under us. Re-seed
            // rather than report a negative rate.
            var delta = totalCount - _lastCount;
            if (delta < 0)
            {
                _lastCount = totalCount;
                _lastAt = mono;
                _rate = 0;
                return _rate;
            }

            var instant = delta / elapsed.TotalSeconds;
            _rate = (_smoothing * instant) + ((1 - _smoothing) * _rate);
            _lastCount = totalCount;
            _lastAt = mono;
            return _rate;
        }
    }
}
