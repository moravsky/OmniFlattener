using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

namespace OmniFlattener.Tests
{
    public class FlattenEngineTests
    {
        #region Test doubles

        private class StubAccount : IAccountView
        {
            public string AccountName { get; set; } = "stub";
            public List<OrderSnapshot> Orders    { get; set; } = new();
            public List<PositionSnapshot> Positions { get; set; } = new();
            public List<OrderSnapshot> GetOpenOrders()       => Orders;
            public List<PositionSnapshot> GetOpenPositions() => Positions;
        }

        private class SpyFlattenService : IFlattenService
        {
            public List<OrderSnapshot> CancelledOrders    { get; } = new();
            public List<PositionSnapshot> ClosedPositions { get; } = new();
            public void CancelOrder(OrderSnapshot o)              => CancelledOrders.Add(o);
            public void ClosePositionAtMarket(PositionSnapshot p) => ClosedPositions.Add(p);
        }

        private class StubLogger : IFlattenLogger
        {
            private readonly Action<string>? _log;
            public StubLogger(Action<string>? log = null) => _log = log;
            public void Log(string message) => _log?.Invoke(message);
        }

        private class StubSettings : IFlattenSettings
        {
            public IAccountView?               Leader          { get; set; }
            public List<IAccountView>          FollowerList    { get; set; } = new();
            public IReadOnlyList<IAccountView> Followers       => FollowerList;
            public int                         SyncDelayMs     { get; set; }

            // 0 suppresses the refresh timer — tests drive the engine directly
            // via CheckLeaderIsFlat() and don't need background polling.
            public int                         RefreshIntervalMs { get; set; } = 0;
        }

        private class StubContext : IFlattenContext
        {
            public IFlattenLogger   Logger         { get; }
            public IFlattenSettings Settings       { get; }
            public IFlattenService  FlattenService { get; }

            public StubContext(IFlattenLogger logger, IFlattenSettings settings, IFlattenService flattenService)
            {
                Logger         = logger;
                Settings       = settings;
                FlattenService = flattenService;
            }
        }

        #endregion

        #region Builders

        private static OrderSnapshot MakeOrder(string id, string account = "follower") =>
            new() { Id = id, AccountName = account, Symbol = "MNQ", Side = "Buy", Quantity = 1, Price = 20000 };

        private static PositionSnapshot MakePosition(string account = "follower", double qty = 1) =>
            new() { AccountName = account, Symbol = "MNQ", Quantity = qty, Side = qty > 0 ? "Sell" : "Buy" };

        private static StubAccount MakeLeader()                           => new() { AccountName = "leader" };
        private static StubAccount MakeFollower(string name = "follower") => new() { AccountName = name };

        private static (FlattenEngine engine, StubSettings settings, SpyFlattenService spy)
            Make(int syncDelayMs = 0, Action<string>? log = null)
        {
            var leader   = MakeLeader();
            var follower = MakeFollower();
            var spy      = new SpyFlattenService();
            var settings = new StubSettings
            {
                Leader       = leader,
                FollowerList = new List<IAccountView> { follower },
                SyncDelayMs  = syncDelayMs,
            };
            var ctx    = new StubContext(new StubLogger(log), settings, spy);
            var engine = new FlattenEngine(ctx);
            return (engine, settings, spy);
        }

        #endregion

        #region Basic flatten

        [Fact]
        public void Leader_flat_cancels_orders_and_closes_positions()
        {
            var (engine, settings, spy) = Make();
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));
            follower.Positions.Add(MakePosition());

            engine.CheckLeaderIsFlat("test");

            Assert.Single(spy.CancelledOrders);
            Assert.Single(spy.ClosedPositions);
        }

        [Fact]
        public void Leader_has_position_no_action()
        {
            var (engine, settings, spy) = Make();
            var leader   = (StubAccount)settings.Leader!;
            var follower = (StubAccount)settings.FollowerList[0];
            leader.Positions.Add(MakePosition("leader"));
            follower.Orders.Add(MakeOrder("O1"));

            engine.CheckLeaderIsFlat("test");

            Assert.Empty(spy.CancelledOrders);
        }

        [Fact]
        public void Leader_has_open_order_no_action()
        {
            var (engine, settings, spy) = Make();
            var leader   = (StubAccount)settings.Leader!;
            var follower = (StubAccount)settings.FollowerList[0];
            leader.Orders.Add(MakeOrder("L1", "leader"));
            follower.Positions.Add(MakePosition());

            engine.CheckLeaderIsFlat("test");

            Assert.Empty(spy.ClosedPositions);
        }

        [Fact]
        public void Short_position_on_follower_is_closed()
        {
            var (engine, settings, spy) = Make();
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Positions.Add(MakePosition("follower", -2));

            engine.CheckLeaderIsFlat("test");

            Assert.Single(spy.ClosedPositions);
            Assert.Equal(-2, spy.ClosedPositions[0].Quantity);
        }

        [Fact]
        public void Multiple_followers_all_flattened()
        {
            var f1  = MakeFollower("f1");
            var f2  = MakeFollower("f2");
            var spy = new SpyFlattenService();
            var settings = new StubSettings
            {
                Leader       = MakeLeader(),
                FollowerList = new List<IAccountView> { f1, f2 },
            };
            var engine = new FlattenEngine(new StubContext(new StubLogger(), settings, spy));

            f1.Orders.Add(MakeOrder("A", "f1"));
            f2.Positions.Add(MakePosition("f2"));

            engine.CheckLeaderIsFlat("test");

            Assert.Single(spy.CancelledOrders);
            Assert.Single(spy.ClosedPositions);
        }

        #endregion

        #region Flat window

        [Fact]
        public void Repeated_checks_while_flat_with_nothing_to_do_do_not_spam_log()
        {
            var logs = new List<string>();
            var (engine, _, _) = Make(log: msg => logs.Add(msg));

            for (int i = 0; i < 5; i++)
                engine.CheckLeaderIsFlat("refresh");

            Assert.Equal(1, logs.Count(l => l.Contains("No follower")));
        }

        [Fact]
        public void Leader_resumes_then_goes_flat_again_triggers_new_flatten()
        {
            var (engine, settings, spy) = Make();
            var leader   = (StubAccount)settings.Leader!;
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));

            engine.CheckLeaderIsFlat("refresh-1");
            Assert.Single(spy.CancelledOrders);

            follower.Orders.Clear();

            leader.Positions.Add(MakePosition("leader"));
            engine.CheckLeaderIsFlat("position-event");

            leader.Positions.Clear();
            follower.Orders.Add(MakeOrder("O2"));
            engine.CheckLeaderIsFlat("refresh-2");

            Assert.Equal(2, spy.CancelledOrders.Count);
        }

        #endregion

        #region Sync delay

        [Fact]
        public void Ghost_order_present_at_sync_delay_end_is_caught()
        {
            var (engine, settings, spy) = Make();
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));
            follower.Orders.Add(MakeOrder("O2-ghost"));

            engine.CheckLeaderIsFlat("refresh");

            Assert.Equal(2, spy.CancelledOrders.Count);
        }

        [Fact]
        public void Leader_resumes_before_sync_delay_elapses_no_flatten()
        {
            var (engine, settings, spy) = Make(syncDelayMs: 1000);
            var leader   = (StubAccount)settings.Leader!;
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));

            engine.CheckLeaderIsFlat("event");

            leader.Positions.Add(MakePosition("leader"));
            engine.CheckLeaderIsFlat("position-event");

            Thread.Sleep(1500);

            Assert.Empty(spy.CancelledOrders);
        }

        [Fact]
        public void Leader_stays_flat_through_sync_delay_triggers_flatten()
        {
            var (engine, settings, spy) = Make(syncDelayMs: 1000);
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));

            engine.CheckLeaderIsFlat("event");

            Thread.Sleep(1500);

            Assert.Single(spy.CancelledOrders);
        }

        #endregion

        #region Reconnection and dynamic accounts

        [Fact]
        public void Leader_disconnected_does_not_trigger_flatten()
        {
            var (engine, settings, spy) = Make();
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));

            settings.Leader = null;

            engine.CheckLeaderIsFlat("refresh");

            Assert.Empty(spy.CancelledOrders);
        }

        [Fact]
        public void Leader_reconnects_with_new_object_and_resumes_working()
        {
            var (engine, settings, spy) = Make();
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));

            settings.Leader = null;
            engine.CheckLeaderIsFlat("refresh");
            Assert.Empty(spy.CancelledOrders);

            settings.Leader = MakeLeader();
            engine.CheckLeaderIsFlat("refresh");

            Assert.Single(spy.CancelledOrders);
        }

        [Fact]
        public void New_follower_account_added_mid_session_gets_flattened()
        {
            var (engine, settings, spy) = Make();

            var newEval = MakeFollower("new-eval");
            newEval.Orders.Add(MakeOrder("O1", "new-eval"));
            settings.FollowerList.Add(newEval);

            engine.CheckLeaderIsFlat("refresh");

            Assert.Single(spy.CancelledOrders);
            Assert.Equal("new-eval", spy.CancelledOrders[0].AccountName);
        }

        [Fact]
        public void Busted_follower_reconnects_and_is_still_flattened()
        {
            var (engine, settings, spy) = Make();
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));

            engine.CheckLeaderIsFlat("refresh-1");
            Assert.Single(spy.CancelledOrders);
            follower.Orders.Clear();

            settings.FollowerList.Clear();

            var reconnected = MakeFollower("follower");
            reconnected.Orders.Add(MakeOrder("O2"));
            settings.FollowerList.Add(reconnected);

            var leader = (StubAccount)settings.Leader!;
            leader.Positions.Add(MakePosition("leader"));
            engine.CheckLeaderIsFlat("position-event");
            leader.Positions.Clear();

            engine.CheckLeaderIsFlat("refresh-2");

            Assert.Equal(2, spy.CancelledOrders.Count);
        }

        [Fact]
        public void Disabled_follower_never_gets_flattened_even_after_reconnect()
        {
            var f1  = MakeFollower("f1");
            var f2  = MakeFollower("f2");
            var spy = new SpyFlattenService();
            var settings = new StubSettings
            {
                Leader       = MakeLeader(),
                FollowerList = new List<IAccountView> { f1, f2 },
            };
            var engine = new FlattenEngine(new StubContext(new StubLogger(), settings, spy));

            f1.Orders.Add(MakeOrder("A", "f1"));
            f2.Orders.Add(MakeOrder("B", "f2"));

            settings.FollowerList.Remove(f2);

            engine.CheckLeaderIsFlat("test");

            Assert.Single(spy.CancelledOrders);
            Assert.Equal("f1", spy.CancelledOrders[0].AccountName);
        }

        [Fact]
        public void Leader_disconnected_during_sync_delay_aborts_flatten()
        {
            var (engine, settings, spy) = Make(syncDelayMs: 1000);
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));

            engine.CheckLeaderIsFlat("event");

            settings.Leader = null;
            engine.CheckLeaderIsFlat("refresh");

            Thread.Sleep(1500);

            Assert.Empty(spy.CancelledOrders);
        }

        #endregion

        #region Refresh timer

        [Fact]
        public void Refresh_timer_fires_and_triggers_flatten()
        {
            var leader   = MakeLeader();
            var follower = MakeFollower();
            var spy      = new SpyFlattenService();
            var settings = new StubSettings
            {
                Leader            = leader,
                FollowerList      = new List<IAccountView> { follower },
                RefreshIntervalMs = 200,
            };
            var engine = new FlattenEngine(new StubContext(new StubLogger(), settings, spy));

            follower.Orders.Add(MakeOrder("O1"));

            Thread.Sleep(500);

            Assert.Single(spy.CancelledOrders);

            engine.Dispose();
        }

        [Fact]
        public void Refresh_timer_stops_after_dispose()
        {
            var leader   = MakeLeader();
            var follower = MakeFollower();
            var spy      = new SpyFlattenService();
            var settings = new StubSettings
            {
                Leader            = leader,
                FollowerList      = new List<IAccountView> { follower },
                RefreshIntervalMs = 200,
            };
            var engine = new FlattenEngine(new StubContext(new StubLogger(), settings, spy));

            engine.Dispose();

            follower.Orders.Add(MakeOrder("O1"));
            Thread.Sleep(500);

            Assert.Empty(spy.CancelledOrders);
        }

        #endregion

        #region Concurrency

        [Fact]
        public void Concurrent_checks_do_not_cause_double_flatten()
        {
            var (engine, settings, spy) = Make();
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));

            var threads = Enumerable.Range(0, 10)
                .Select(_ => new Thread(() => engine.CheckLeaderIsFlat("concurrent")))
                .ToList();

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Single(spy.CancelledOrders);
        }

        #endregion

        #region Dispose

        [Fact]
        public void Dispose_cancels_pending_sync_delay()
        {
            var (engine, settings, spy) = Make(syncDelayMs: 2000);
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));

            engine.CheckLeaderIsFlat("event");
            engine.Dispose();

            Thread.Sleep(2500);

            Assert.Empty(spy.CancelledOrders);
        }

        [Fact]
        public void CheckLeaderIsFlat_after_dispose_is_noop()
        {
            var (engine, settings, spy) = Make();
            var follower = (StubAccount)settings.FollowerList[0];
            follower.Orders.Add(MakeOrder("O1"));

            engine.Dispose();
            engine.CheckLeaderIsFlat("test");

            Assert.Empty(spy.CancelledOrders);
        }

        #endregion
    }
}
