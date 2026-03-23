using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AutoSizeStrategy
{
    public record Expiration(DateTime Time, TaskCompletionSource<bool> Completion) { }

    /// Thread-safe set for tracking items with automatic expiration
    /// Used for idempotency checks and preventing duplicate operations.
    public class TrackingSet<T> : IDisposable
        where T : notnull
    {
        private static readonly TimeSpan DefaultCleanupInterval = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan DefaultExpirationTime = TimeSpan.FromSeconds(5);
        private const int DefaultWaitTimeoutMs = 5000;

        private readonly ConcurrentDictionary<T, Expiration> _tracked = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly TimeSpan _defaultExpirationTime;
        private bool _disposed;

        public TrackingSet(TimeSpan? cleanupInterval = null, TimeSpan? defaultExpirationTime = null)
        {
            _defaultExpirationTime = defaultExpirationTime ?? DefaultExpirationTime;
            _ = RunCleanupLoop(cleanupInterval ?? DefaultCleanupInterval);
        }

        public bool TryTrack(T key, DateTime expirationDate)
        {
            var tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            return _tracked.TryAdd(key, new Expiration(expirationDate, tcs));
        }

        public bool TryTrack(T key)
        {
            return TryTrack(key, DateTime.UtcNow + _defaultExpirationTime);
        }

        // Allows a caller to await the removal/cancellation of a specific tracked item.
        public async Task<bool> WaitAsync(T key, int timeoutMs = DefaultWaitTimeoutMs)
        {
            // If the item isn't tracked, it's effectively 'done'
            if (!_tracked.TryGetValue(key, out var expiration))
                return true;

            // Create a local timer for THIS specific caller's SLA
            using var cts = new CancellationTokenSource();
            var timeoutTask = Task.Delay(timeoutMs, cts.Token);

            // Race the shared completion task against our local timeout
            var completedTask = await Task.WhenAny(expiration.Completion.Task, timeoutTask);

            if (completedTask == expiration.Completion.Task)
            {
                cts.Cancel(); // Clean up the timer immediately
                return await expiration.Completion.Task; // Return the actual result (true/false)
            }

            // Our local SLA hit before the platform responded
            return false;
        }

        public bool Contains(T key)
        {
            return _tracked.ContainsKey(key);
        }

        public bool TryRemove(T key, out Expiration entry)
        {
            if (_tracked.TryRemove(key, out entry))
            {
                entry.Completion.TrySetResult(true); // Signal success to anyone waiting
                return true;
            }
            return false;
        }

        public bool TryRemove(T key)
        {
            Expiration _;
            return TryRemove(key, out _);
        }

        public Task<bool> GetTask(T key)
        {
            // If the item is tracked, return its completion task.
            // If not, it's effectively "done" (either already removed or never started).
            return _tracked.TryGetValue(key, out var expiration)
                ? expiration.Completion.Task
                : Task.FromResult(true);
        }

        /// <summary>
        /// Current number of tracked items.
        /// </summary>
        public int Count => _tracked.Count;

        public void Dispose()
        {
            if (!_disposed)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        private async Task RunCleanupLoop(TimeSpan interval)
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, _cancellationTokenSource.Token);

                    foreach (var kvp in _tracked)
                    {
                        if (kvp.Value.Time < DateTime.UtcNow)
                        {
                            TryRemove(kvp.Key);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
