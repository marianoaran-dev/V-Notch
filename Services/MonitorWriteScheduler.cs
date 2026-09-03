namespace VNotch.Services;

/// <summary>
/// Coalesces slider drag values per monitor/control and commits immediately when
/// the caller flushes on pointer release.  The service transport still fences
/// writes by generation when requests overlap.
/// </summary>
public sealed class MonitorWriteScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(string MonitorId, MonitorControlKind Control), MonitorWriteRequest> _pending = new();
    private readonly HashSet<Task> _inFlightFlushes = new();
    private readonly Func<MonitorWriteRequest, Task> _writer;
    private readonly TimeSpan _delay;
    private readonly Timer _timer;
    private bool _disposed;

    public MonitorWriteScheduler(Func<MonitorWriteRequest, Task> writer, TimeSpan? delay = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _delay = delay ?? TimeSpan.FromMilliseconds(90);
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public int PendingCount
    {
        get
        {
            lock (_gate) return _pending.Count;
        }
    }

    public void Queue(MonitorWriteRequest request)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _pending[(request.MonitorId, request.Control)] = request with
            {
                Percentage = MonitorLinkEngine.ClampPercentage(request.Percentage)
            };
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    public Task FlushAsync()
    {
        MonitorWriteRequest[]? batch = null;
        TaskCompletionSource<bool>? batchCompletion = null;
        Task[] waits;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            var active = _inFlightFlushes.ToArray();
            if (_pending.Count > 0)
            {
                batch = _pending.Values.ToArray();
                _pending.Clear();
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                batchCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlightFlushes.Add(batchCompletion.Task);
                waits = active.Append(batchCompletion.Task).ToArray();
            }
            else
            {
                waits = active;
            }
        }

        if (batchCompletion != null)
            _ = RunBatchAsync(batch!, batchCompletion);

        return waits.Length switch
        {
            0 => Task.CompletedTask,
            1 => waits[0],
            _ => Task.WhenAll(waits)
        };
    }

    private async Task RunBatchAsync(
        MonitorWriteRequest[] batch,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await Task.WhenAll(batch.Select(_writer)).ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                _inFlightFlushes.Remove(completion.Task);
            }
        }
    }

    private void OnTimer(object? state)
    {
        _ = FlushFromTimerAsync();
    }

    private async Task FlushFromTimerAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // The view model reports transport failures.  A timer callback must
            // never surface a background exception on the UI dispatcher.
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MonitorWriteScheduler));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _timer.Dispose();
    }
}
