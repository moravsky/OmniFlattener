using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace OmniFlattener
{
    /// <summary>
    /// Watches the leader account and flattens followers when the leader goes flat,
    /// and flattens all accounts (leader included) at a configured time of day (EOD).
    ///
    /// Both checks share the same refresh timer — no second timer needed.
    ///
    /// Leader-flat path: sync delay → double-check → flatten followers only.
    /// EOD path: no sync delay (time-sensitive) → flatten all accounts.
    ///
    /// EOD scheduling: on construction, _nextEodFlattenAt is set to today's FlattenAt
    /// time. If that moment is already past, it advances to tomorrow. This means
    /// recreating the strategy after the deadline correctly schedules for the next day.
    /// After each EOD fire, advance by one day. Adding one day to a local DateTime
    /// handles DST correctly — the wall-clock time stays the same.
    /// </summary>
    public class FlattenEngine : IDisposable
    {
        private readonly IFlattenContext _ctx;
        private readonly Timer? _refreshTimer;

        private readonly object _lock = new object();
        private CancellationTokenSource? _syncDelayCts;

        private bool _alreadyFlattenedThisCycle;

        private DateTime _nextEodFlattenAt;

        private bool _disposed;

        public FlattenEngine(IFlattenContext ctx)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

            _nextEodFlattenAt = ComputeNextEodFlattenAt(ctx.Settings.Now, ctx.Settings.EodFlattenAt);

            if (ctx.Settings.RefreshIntervalMs > 0)
            {
                var interval = TimeSpan.FromMilliseconds(ctx.Settings.RefreshIntervalMs);
                _refreshTimer = new Timer(_ => Check("refresh"), null, interval, interval);
            }
        }

        /// <summary>
        /// Called on every trigger (order/position event or refresh timer).
        /// Handles both leader-flat detection and EOD flattening.
        /// Thread-safe: drops if engine is already processing.
        /// </summary>
        public void Check(string source)
        {
            if (_disposed) return;

            if (!Monitor.TryEnter(_lock))
                return;
            try
            {
                CheckCore(source);
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }

        private void CheckCore(string source)
        {
            if (_disposed) return;

            CheckEod(source);
            CheckLeaderIsFlat(source);
        }

        private void CheckEod(string source)
        {
            if (!_ctx.Settings.EodEnabled) return;

            var now = _ctx.Settings.Now;
            if (now < _nextEodFlattenAt) return;

            _ctx.Logger.Log(
                $"[{source}] EOD flatten at {_ctx.Settings.EodFlattenAt:hh\\:mm} reached — flattening all accounts.");
            FlattenAll(_ctx.Settings.AllAccounts);

            _nextEodFlattenAt = _nextEodFlattenAt.AddDays(1);
        }

        private void CheckLeaderIsFlat(string source)
        {
            if (IsLeaderFlat())
            {
                if (_syncDelayCts != null)
                {
                    // We are actively waiting for the sync delay to elapse. Do nothing.
                    return;
                }

                if (!_alreadyFlattenedThisCycle)
                {
                    // 1. Just became flat. Start the clock.
                    _ctx.Logger.Log(
                        $"[{source}] Leader is flat — sync delay started ({_ctx.Settings.SyncDelayMs}ms)...");
                    StartSyncDelay();
                }
                else
                {
                    // 2. Delay elapsed previously, and leader is STILL flat. 
                    // Sweep any new stragglers (FlattenAll avoids log spam internally).
                    var leaderName = _ctx.Settings.Leader?.AccountName;
                    var followers = _ctx.Settings.AllAccounts
                        .Where(a => a.AccountName != leaderName)
                        .ToList();
                    FlattenAll(followers);
                }
            }
            else
            {
                // Leader is NOT flat.
                if (_syncDelayCts != null)
                {
                    // They resumed while we were waiting. Cancel the delay.
                    _ctx.Logger.Log($"[{source}] Leader resumed activity — sync delay cancelled.");
                    StopSyncDelay();
                }

                // Reset the cycle so it can trigger again next time they go flat.
                _alreadyFlattenedThisCycle = false;
            }
        }

        private void StartSyncDelay()
        {
            StopSyncDelay();

            var cts = new CancellationTokenSource();
            _syncDelayCts = cts;

            if (_ctx.Settings.SyncDelayMs == 0)
            {
                OnSyncDelayElapsed(cts.Token, acquireLock: false);
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Thread.Sleep(_ctx.Settings.SyncDelayMs);
                    OnSyncDelayElapsed(cts.Token, acquireLock: true);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _ctx.Logger.Log($"[sync] Delay error: {ex.Message}");
                }
            });
        }

        private void StopSyncDelay()
        {
            _syncDelayCts?.Cancel();
            _syncDelayCts?.Dispose();
            _syncDelayCts = null;
        }

        private void OnSyncDelayElapsed(CancellationToken token, bool acquireLock)
        {
            if (acquireLock)
                Monitor.Enter(_lock);
            try
            {
                if (token.IsCancellationRequested || _disposed)
                    return;

                if (!IsLeaderFlat())
                {
                    _ctx.Logger.Log("[sync] Leader resumed before delay elapsed — flatten aborted.");
                    _syncDelayCts = null;
                    return;
                }

                _ctx.Logger.Log("[sync] Leader still flat — flattening followers.");
                var leaderName = _ctx.Settings.Leader?.AccountName;
                var followers = _ctx.Settings.AllAccounts
                    .Where(a => a.AccountName != leaderName)
                    .ToList();

                FlattenAll(followers);

                // Clear the CTS so the engine knows the wait is over
                _syncDelayCts = null;
            }
            finally
            {
                if (acquireLock)
                    Monitor.Exit(_lock);
            }
        }

        private void FlattenAll(IReadOnlyList<IAccountView> accounts)
        {
            var orders = accounts.SelectMany(a => a.GetOpenOrders()).ToList();
            var positions = accounts.SelectMany(a => a.GetOpenPositions()).ToList();

            if (orders.Count == 0 && positions.Count == 0)
            {
                if (!_alreadyFlattenedThisCycle)
                    _ctx.Logger.Log("[flatten] Nothing to flatten.");
                _alreadyFlattenedThisCycle = true;
                return;
            }

            _alreadyFlattenedThisCycle = true;

            foreach (var order in orders)
            {
                _ctx.Logger.Log($"[flatten] Cancelling order {order.Id} on '{order.AccountName}' " +
                                $"({order.Side} x{order.Quantity} {order.Symbol} @ {order.Price})");
                _ctx.FlattenService.CancelOrder(order);
            }

            foreach (var position in positions)
            {
                _ctx.Logger.Log($"[flatten] Closing position on '{position.AccountName}' " +
                                $"({position.Side} x{Math.Abs(position.Quantity)} {position.Symbol}) at market");
                _ctx.FlattenService.ClosePositionAtMarket(position);
            }
        }

        private bool IsLeaderFlat()
        {
            var leader = _ctx.Settings.Leader;
            if (leader == null) return false;

            return leader.GetOpenOrders().Count == 0 &&
                   leader.GetOpenPositions().Count == 0;
        }

        /// <summary>
        /// Computes the next wall-clock DateTime at which EOD should fire.
        /// If today's FlattenAt is still in the future, returns it.
        /// If it has already passed (including strategy recreation after market close),
        /// returns tomorrow's FlattenAt — no spurious immediate fire.
        /// </summary>
        private static DateTime ComputeNextEodFlattenAt(DateTime now, TimeSpan flattenAt)
        {
            var flattenAtToday = now.Date + flattenAt;
            return now > flattenAtToday ? flattenAtToday.AddDays(1) : flattenAtToday;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                _refreshTimer?.Dispose();

                Monitor.Enter(_lock);
                try
                {
                    StopSyncDelay();
                }
                finally
                {
                    Monitor.Exit(_lock);
                }
            }
        }
    }
}
