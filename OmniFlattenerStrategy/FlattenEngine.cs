using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace OmniFlattener
{
    /// <summary>
    /// Watches the leader account and flattens all followers when the leader goes flat.
    ///
    /// Leader and followers are resolved from IFlattenSettings on every check, so
    /// reconnections and new accounts are picked up automatically without restarting.
    ///
    /// If the leader is not currently connected, IsLeaderFlat() returns false —
    /// the engine stands by safely until the leader comes back.
    ///
    /// When the leader first goes flat a sync delay starts. If the leader resumes before
    /// it elapses the delay is cancelled. If the leader is still flat when it elapses,
    /// all follower orders are cancelled and positions closed at market.
    ///
    /// The sync delay should be longer than fill propagation lag so that any remaining
    /// orders and positions on followers are in fact ghosts.
    /// Once a flat window opens it does not re-arm on repeated checks — only a
    /// non-flat → flat transition starts a new sync delay.
    ///
    /// The engine owns the refresh timer. When RefreshIntervalMs > 0 a timer is started
    /// on construction as a backstop for any order/position events that may be missed.
    /// Set RefreshIntervalMs to 0 in tests to suppress the timer entirely.
    /// </summary>
    public class FlattenEngine : IDisposable
    {
        private readonly IFlattenContext  _ctx;
        private readonly Timer?           _refreshTimer;

        private readonly object            _lock = new object();
        private CancellationTokenSource?  _syncDelayCts;

        // True from first detection of leader flat until leader resumes.
        // Prevents re-arming the sync delay on repeated or concurrent checks.
        private bool _inFlatWindow;

        // Suppresses the "nothing to do" log after the first clean sweep in a flat window.
        private bool _alreadyFlattenedThisCycle;

        private bool _disposed;

        public FlattenEngine(IFlattenContext ctx)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

            if (ctx.Settings.RefreshIntervalMs > 0)
            {
                var interval = TimeSpan.FromMilliseconds(ctx.Settings.RefreshIntervalMs);
                _refreshTimer = new Timer(_ => CheckLeaderIsFlat("refresh"), null, interval, interval);
            }
        }

        /// <summary>
        /// Called on every trigger (order/position event or refresh timer).
        /// Thread-safe: drops if engine is already processing.
        /// </summary>
        public void CheckLeaderIsFlat(string source)
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

            bool leaderIsFlat = IsLeaderFlat();

            if (leaderIsFlat)
            {
                if (!_inFlatWindow)
                {
                    _inFlatWindow              = true;
                    _alreadyFlattenedThisCycle = false;
                    _ctx.Logger.Log($"[{source}] Leader is flat — sync delay started ({_ctx.Settings.SyncDelayMs}ms)...");
                    StartSyncDelay();
                }
            }
            else
            {
                if (_inFlatWindow)
                {
                    _ctx.Logger.Log($"[{source}] Leader resumed activity — sync delay cancelled.");
                    StopSyncDelay();
                    _inFlatWindow              = false;
                    _alreadyFlattenedThisCycle = false;
                }
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
                catch (OperationCanceledException) { }
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

        /// <param name="acquireLock">
        /// False when called inline (already under _lock).
        /// True from the threadpool — must re-acquire _lock for the double-check.
        /// </param>
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
                    _inFlatWindow = false;
                    return;
                }

                _ctx.Logger.Log("[sync] Leader still flat — executing OmniFlattener.");
                FlattenAll(_ctx.Settings.Followers);
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
            var orders    = accounts.SelectMany(a => a.GetOpenOrders()).ToList();
            var positions = accounts.SelectMany(a => a.GetOpenPositions()).ToList();

            if (orders.Count == 0 && positions.Count == 0)
            {
                if (!_alreadyFlattenedThisCycle)
                    _ctx.Logger.Log("[flatten] No follower orders or positions to flatten.");
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

        /// <summary>
        /// Returns false if the leader is not currently connected — safe default, no flatten fires.
        /// </summary>
        private bool IsLeaderFlat()
        {
            var leader = _ctx.Settings.Leader;
            if (leader == null)
                return false;

            return leader.GetOpenOrders().Count == 0 &&
                   leader.GetOpenPositions().Count == 0;
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
