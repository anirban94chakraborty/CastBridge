namespace CastBridge.Core.Volume;

/// <summary>
/// Rate-limits volume changes so that dragging a slider does not flood the device with
/// SET_VOLUME messages. The UI is updated immediately (optimistic), while the device only sees
/// the latest value once per <c>interval</c> - and never more than one in-flight request.
/// </summary>
public sealed class VolumeCoalescer : IAsyncDisposable
{
    private readonly Func<double, Task> _apply;
    private readonly TimeSpan _interval;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private double _target;
    private double _lastSent = double.NaN;
    private bool _dirty;
    private bool _disposed;

    public VolumeCoalescer(Func<double, Task> apply, TimeSpan? interval = null)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _interval = interval ?? TimeSpan.FromMilliseconds(150);
        _target = double.NaN;
    }

    /// <summary>Raised for every user-intent change, before the device is contacted.</summary>
    public event Action<double>? ValueChanged;

    /// <summary>Raised when the device call fails, so the UI can report or revert.</summary>
    public event Action<Exception>? Failed;

    public double Target => _target;

    /// <summary>Records a new target level (0..1) and schedules the device update.</summary>
    public void SetLevel(double level)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        level = Math.Clamp(level, 0, 1);

        lock (_sync)
        {
            _target = level;
            _dirty = true;
            EnsureLoop();
        }

        ValueChanged?.Invoke(level);
    }

    /// <summary>Applies a level without scheduling a device write (used when the device reports its own state).</summary>
    public void ResetFromDevice(double level)
    {
        lock (_sync)
        {
            _target = double.IsNaN(_target) ? level : _target;
            _lastSent = level;
        }
    }

    /// <summary>Forces the pending value to be written immediately.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        double level;
        lock (_sync)
        {
            if (!_dirty || double.IsNaN(_target))
                return;
            level = _target;
            _dirty = false;
        }

        await SendAsync(level, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureLoop()
    {
        if (_loop is not null)
            return;

        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_loopCts.Token));
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            double level;
            lock (_sync)
            {
                if (!_dirty || double.IsNaN(_target))
                    continue;
                level = _target;
                _dirty = false;
            }

            await SendAsync(level, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(double level, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (!double.IsNaN(_lastSent) && Math.Abs(_lastSent - level) < 0.005)
                    return;
            }

            await _apply(level).ConfigureAwait(false);

            lock (_sync)
            {
                _lastSent = level;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Allow a retry on the next change rather than wedging the value as "sent".
            lock (_sync)
            {
                _dirty = true;
            }

            Failed?.Invoke(ex);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_loopCts is not null)
                await _loopCts.CancelAsync().ConfigureAwait(false);

            if (_loop is not null)
                await _loop.ConfigureAwait(false);
        }
        catch
        {
            // Shutting down.
        }

        _loopCts?.Dispose();
        _sendGate.Dispose();
    }
}
