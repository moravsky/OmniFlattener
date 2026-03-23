
namespace OmniFlattener.Tests
{
    public class FlattenEngineTests
    {
        #region Test doubles

        private class StubAccount : IAccountView
        {
            public string AccountName { get; init; } = "stub";
            public List<OrderSnapshot> Orders { get; } = new();
            public List<PositionSnapshot> Positions { get; } = new();
            public List<OrderSnapshot> GetOpenOrders() => Orders;
            public List<PositionSnapshot> GetOpenPositions() => Positions;
        }

        private class SpyFlattenService(params StubAccount[] accounts) : IFlattenService
        {
            private readonly List<StubAccount> _accounts = accounts.ToList();
            public List<OrderSnapshot> CancelledOrders { get; } = new();
            public List<PositionSnapshot> ClosedPositions { get; } = new();

            public void CancelOrder(OrderSnapshot o)
            {
                CancelledOrders.Add(o);
                _accounts.FirstOrDefault(a => a.AccountName == o.AccountName)?.Orders.RemoveAll(x => x.Id == o.Id);
            }

            public void ClosePositionAtMarket(PositionSnapshot p)
            {
                ClosedPositions.Add(p);
                _accounts.FirstOrDefault(a => a.AccountName == p.AccountName)?.Positions.Clear();
            }
        }

        private class StubLogger(Action<string>? log = null) : IFlattenLogger
        {
            public void LogInfo(string message) => log?.Invoke(message);
            public void LogError(string message) => log?.Invoke("[ERROR] " + message);
        }

        private class StubSettings : IFlattenSettings
        {
            public IAccountView? Leader { get; set; }
            public List<IAccountView> AllAccountList { get; init; } = new();
            public IReadOnlyList<IAccountView> AllAccounts => AllAccountList;
            public int SyncDelayMs { get; init; }
            public int RefreshIntervalMs { get; init; }
            public bool EodEnabled { get; set; }
            public TimeSpan EodFlattenAt { get; set; } = new TimeSpan(16, 59, 0);

            // Mutable clock — tests advance this to simulate time passing.
            // Defaults to far in the past so EOD never fires unless explicitly set.
            public DateTime Now { get; set; } = new DateTime(2000, 1, 1, 0, 0, 0);
        }

        private class StubContext(IFlattenLogger logger, IFlattenSettings settings, IFlattenService flattenService)
            : IFlattenContext
        {
            public IFlattenLogger Logger { get; } = logger;
            public IFlattenSettings Settings { get; } = settings;
            public IFlattenService FlattenService { get; } = flattenService;
        }

        #endregion

        #region Builders

        private static OrderSnapshot MakeOrder(string id, string account = "follower") =>
            new() { Id = id, AccountName = account, Symbol = "MNQ", Side = "Buy", Quantity = 1, Price = 20000 };

        private static PositionSnapshot MakePosition(string account = "follower", double qty = 1) =>
            new() { AccountName = account, Symbol = "MNQ", Quantity = qty, Side = qty > 0 ? "Sell" : "Buy" };

        private static StubAccount MakeLeader() => new() { AccountName = "leader" };
        private static StubAccount MakeFollower(string name = "follower") => new() { AccountName = name };

        private static (FlattenEngine engine, StubSettings settings, SpyFlattenService spy, StubAccount leader,
            StubAccount follower)
            Make(int syncDelayMs = 0, Action<string>? log = null)
        {
            var leader = MakeLeader();
            var follower = MakeFollower();
            var spy = new SpyFlattenService(leader, follower);
            var settings = new StubSettings
            {
                Leader = leader,
                AllAccountList = [leader, follower],
                SyncDelayMs = syncDelayMs,
            };
            var ctx = new StubContext(new StubLogger(log), settings, spy);
            var engine = new FlattenEngine(ctx);
            return (engine, settings, spy, leader, follower);
        }

        private static DateTime At(int hour, int minute, int day = 1) =>
            new DateTime(2026, 3, day, hour, minute, 0);

        #endregion

        #region Basic flatten

        [Fact]
        public void Leader_flat_cancels_orders_and_closes_positions()
        {
            var (engine, _, spy, _, follower) = Make();
            follower.Orders.Add(MakeOrder("O1"));
            follower.Positions.Add(MakePosition());

            engine.Check("test");

            Assert.Single(spy.CancelledOrders);
            Assert.Single(spy.ClosedPositions);
        }

        [Fact]
        public void Leader_has_position_no_action()
        {
            var (engine, _, spy, leader, follower) = Make();
            leader.Positions.Add(MakePosition("leader"));
            follower.Orders.Add(MakeOrder("O1"));

            engine.Check("test");

            Assert.Empty(spy.CancelledOrders);
        }

        [Fact]
        public void Leader_has_open_order_no_action()
        {
            var (engine, _, spy, leader, follower) = Make();
            leader.Orders.Add(MakeOrder("L1", "leader"));
            follower.Positions.Add(MakePosition());

            engine.Check("test");

            Assert.Empty(spy.ClosedPositions);
        }

        [Fact]
        public void Short_position_on_follower_is_closed()
        {
            var (engine, _, spy, _, follower) = Make();
            follower.Positions.Add(MakePosition("follower", -2));

            engine.Check("test");

            Assert.Single(spy.ClosedPositions);
            Assert.Equal(-2, spy.ClosedPositions[0].Quantity);
        }

        [Fact]
        public void Multiple_followers_all_flattened()
        {
            var f1 = MakeFollower("f1");
            var f2 = MakeFollower("f2");
            var spy = new SpyFlattenService();
            var settings = new StubSettings
            {
                Leader = MakeLeader(),
                AllAccountList = [f1, f2],
            };
            var engine = new FlattenEngine(new StubContext(new StubLogger(), settings, spy));

            f1.Orders.Add(MakeOrder("A", "f1"));
            f2.Positions.Add(MakePosition("f2"));

            engine.Check("test");

            Assert.Single(spy.CancelledOrders);
            Assert.Single(spy.ClosedPositions);
        }

        [Fact]
        public void New_follower_position_while_leader_stays_flat_gets_flattened()
        {
            var (engine, _, spy, _, follower) = Make();
            follower.Orders.Add(MakeOrder("O1"));

            engine.Check("refresh-1");
            Assert.Single(spy.CancelledOrders);
            follower.Orders.Clear();

            // Leader still flat — follower opens a new position, PositionAdded event fires
            follower.Positions.Add(MakePosition());
            engine.Check("position-event");

            Assert.Single(spy.ClosedPositions);
        }

        #endregion

        #region Broker Lag

        [Fact]
        public void Broker_lag_simulation_duplicate_order_events_are_ignored()
        {
            var (engine, _, spy, _, follower) = Make();
            var order = MakeOrder("GHOST-ORDER-123");
            follower.Orders.Add(order);

            // 1. First event triggers the flatten
            engine.Check("event-1");

            // Spy instantly removed it. Let's pretend broker just mutated it,
            // leaving it open, which fires a second event into the engine.
            follower.Orders.Add(order);
            engine.Check("event-2");

            // 2. TrackingSet should remember the ID and prevent a second cancel
            Assert.Single(spy.CancelledOrders);
        }

        [Fact]
        public void Broker_lag_simulation_duplicate_position_events_are_ignored()
        {
            var (engine, _, spy, _, follower) = Make();
            var position = MakePosition("follower", 2);
            follower.Positions.Add(position);

            engine.Check("event-1");

            // Pretend broker lagged and position is still there
            follower.Positions.Add(position);
            engine.Check("event-2");

            // TrackingSet should remember the AccountName_Symbol key
            Assert.Single(spy.ClosedPositions);
        }

        #endregion

        #region Flat window

        [Fact]
        public void Repeated_checks_while_flat_with_nothing_to_do_do_not_spam_log()
        {
            var logs = new List<string>();
            var (engine, _, _, _, _) = Make(log: logs.Add);

            for (int i = 0; i < 5; i++)
                engine.Check("refresh");

            Assert.Equal(1, logs.Count(l => l.Contains("Nothing to flatten")));
        }

        [Fact]
        public void Leader_resumes_then_goes_flat_again_triggers_new_flatten()
        {
            var (engine, _, spy, leader, follower) = Make();
            follower.Orders.Add(MakeOrder("O1"));

            engine.Check("refresh-1");
            Assert.Single(spy.CancelledOrders);

            follower.Orders.Clear();
            leader.Positions.Add(MakePosition("leader"));
            engine.Check("position-event");

            leader.Positions.Clear();
            follower.Orders.Add(MakeOrder("O2"));
            engine.Check("refresh-2");

            Assert.Equal(2, spy.CancelledOrders.Count);
        }

        #endregion

        #region Sync delay

        [Fact]
        public void Ghost_order_present_at_sync_delay_end_is_caught()
        {
            var (engine, _, spy, _, follower) = Make();
            follower.Orders.Add(MakeOrder("O1"));
            follower.Orders.Add(MakeOrder("O2-ghost"));

            engine.Check("refresh");

            Assert.Equal(2, spy.CancelledOrders.Count);
        }

        [Fact]
        public void Leader_resumes_before_sync_delay_elapses_no_flatten()
        {
            var (engine, _, spy, leader, follower) = Make(syncDelayMs: 1000);
            follower.Orders.Add(MakeOrder("O1"));

            engine.Check("event");

            leader.Positions.Add(MakePosition("leader"));
            engine.Check("position-event");

            Thread.Sleep(1500);

            Assert.Empty(spy.CancelledOrders);
        }

        [Fact]
        public void Leader_stays_flat_through_sync_delay_triggers_flatten()
        {
            var (engine, _, spy, _, follower) = Make(syncDelayMs: 1000);
            follower.Orders.Add(MakeOrder("O1"));

            engine.Check("event");

            Thread.Sleep(1500);

            Assert.Single(spy.CancelledOrders);
        }

        #endregion

        #region Reconnection and dynamic accounts

        [Fact]
        public void Leader_disconnected_does_not_trigger_flatten()
        {
            var (engine, settings, spy, _, follower) = Make();
            follower.Orders.Add(MakeOrder("O1"));

            settings.Leader = null;
            engine.Check("refresh");

            Assert.Empty(spy.CancelledOrders);
        }

        [Fact]
        public void Leader_reconnects_with_new_object_and_resumes_working()
        {
            var (engine, settings, spy, _, follower) = Make();
            follower.Orders.Add(MakeOrder("O1"));

            settings.Leader = null;
            engine.Check("refresh");
            Assert.Empty(spy.CancelledOrders);

            settings.Leader = MakeLeader();
            engine.Check("refresh");

            Assert.Single(spy.CancelledOrders);
        }

        [Fact]
        public void New_follower_account_added_mid_session_gets_flattened()
        {
            var (engine, settings, spy, _, _) = Make();

            var newEval = MakeFollower("new-eval");
            newEval.Orders.Add(MakeOrder("O1", "new-eval"));
            settings.AllAccountList.Add(newEval);

            engine.Check("refresh");

            Assert.Single(spy.CancelledOrders);
            Assert.Equal("new-eval", spy.CancelledOrders[0].AccountName);
        }

        [Fact]
        public void Busted_follower_reconnects_and_is_still_flattened()
        {
            var (engine, settings, spy, leader, follower) = Make();
            follower.Orders.Add(MakeOrder("O1"));

            engine.Check("refresh-1");
            Assert.Single(spy.CancelledOrders);
            follower.Orders.Clear();

            settings.AllAccountList.Clear();

            var reconnected = MakeFollower();
            reconnected.Orders.Add(MakeOrder("O2"));
            settings.AllAccountList.Add(reconnected);
            leader.Positions.Add(MakePosition("leader"));
            engine.Check("position-event");
            leader.Positions.Clear();

            engine.Check("refresh-2");

            Assert.Equal(2, spy.CancelledOrders.Count);
        }

        [Fact]
        public void Disabled_follower_never_gets_flattened_even_after_reconnect()
        {
            var f1 = MakeFollower("f1");
            var f2 = MakeFollower("f2");
            var spy = new SpyFlattenService();
            var settings = new StubSettings
            {
                Leader = MakeLeader(),
                AllAccountList = [f1, f2],
            };
            var engine = new FlattenEngine(new StubContext(new StubLogger(), settings, spy));

            f1.Orders.Add(MakeOrder("A", "f1"));
            f2.Orders.Add(MakeOrder("B", "f2"));

            settings.AllAccountList.Remove(f2);

            engine.Check("test");

            Assert.Single(spy.CancelledOrders);
            Assert.Equal("f1", spy.CancelledOrders[0].AccountName);
        }

        [Fact]
        public void Leader_disconnected_during_sync_delay_aborts_flatten()
        {
            var (engine, settings, spy, _, follower) = Make(syncDelayMs: 1000);
            follower.Orders.Add(MakeOrder("O1"));

            engine.Check("event");

            settings.Leader = null;
            engine.Check("refresh");

            Thread.Sleep(1500);

            Assert.Empty(spy.CancelledOrders);
        }

        #endregion

        #region EOD

        [Fact]
        public void Eod_fires_when_now_reaches_flatten_at()
        {
            var (_, settings, spy, leader, follower) = Make();
            settings.EodEnabled = true;
            settings.EodFlattenAt = new TimeSpan(16, 59, 0);
            settings.Now = At(16, 59);

            leader.Positions.Add(MakePosition("leader")); // leader not flat — no leader-flat flatten
            follower.Orders.Add(MakeOrder("O1"));

            // Rebuild engine so _nextEodFlattenAt is computed from the new Now
            var ctx = new StubContext(new StubLogger(), settings, spy);
            var engine2 = new FlattenEngine(ctx);

            engine2.Check("test");

            // EOD flattens AllAccounts — but AllAccountList is leader+follower
            // follower has an order → it gets cancelled
            Assert.Single(spy.CancelledOrders);
        }

        [Fact]
        public void Eod_does_not_fire_before_flatten_at()
        {
            var (_, settings, spy, leader, follower) = Make();
            settings.EodEnabled = true;
            settings.EodFlattenAt = new TimeSpan(16, 59, 0);
            settings.Now = At(16, 58);
            follower.Orders.Add(MakeOrder("O1"));

            // Leader not flat either — nothing should fire
            leader.Positions.Add(MakePosition("leader"));

            var engine = new FlattenEngine(new StubContext(new StubLogger(), settings, spy));
            engine.Check("test");

            Assert.Empty(spy.CancelledOrders);
        }

        [Fact]
        public void Eod_disabled_does_not_fire()
        {
            var (_, settings, spy, leader, follower) = Make();
            settings.EodEnabled = false;
            settings.EodFlattenAt = new TimeSpan(16, 59, 0);
            settings.Now = At(17, 30);

            leader.Positions.Add(MakePosition("leader"));
            follower.Orders.Add(MakeOrder("O1"));

            var engine = new FlattenEngine(new StubContext(new StubLogger(), settings, spy));
            engine.Check("test");

            Assert.Empty(spy.CancelledOrders);
        }

        [Fact]
        public void Eod_fires_all_accounts_including_leader()
        {
            var leader = MakeLeader();
            var follower = MakeFollower();
            var spy = new SpyFlattenService();
            var settings = new StubSettings
            {
                Leader = leader,
                AllAccountList = [leader, follower],
                EodEnabled = true,
                EodFlattenAt = new TimeSpan(16, 59, 0),
                Now = At(16, 59),
            };

            // Leader has a position, follower has an order.
            // EOD must flatten both via AllAccounts — if it only used Followers,
            // the leader position would survive.
            leader.Positions.Add(MakePosition("leader"));
            follower.Orders.Add(MakeOrder("O1"));

            var engine = new FlattenEngine(new StubContext(new StubLogger(), settings, spy));
            engine.Check("test");

            Assert.Single(spy.CancelledOrders);
            Assert.Single(spy.ClosedPositions);
            Assert.Equal("leader", spy.ClosedPositions[0].AccountName);
        }

        [Fact]
        public void Eod_fires_only_once_then_schedules_next_day()
        {
            var leader = MakeLeader();
            var follower = MakeFollower();
            var spy = new SpyFlattenService();
            var settings = new StubSettings
            {
                Leader = leader,
                AllAccountList = [leader, follower],
                EodEnabled = true,
                EodFlattenAt = new TimeSpan(16, 59, 0),
                Now = At(16, 59, day: 1),
            };

            // Leader not flat — prevents leader-flat flatten from interfering
            leader.Positions.Add(MakePosition("leader"));
            follower.Orders.Add(MakeOrder("O1"));

            var engine = new FlattenEngine(new StubContext(new StubLogger(), settings, spy));

            engine.Check("test"); // fires — day 1 at 16:59
            Assert.Single(spy.CancelledOrders);
            follower.Orders.Clear(); // simulate broker processed the cancellation

            follower.Orders.Add(MakeOrder("O2"));
            engine.Check("test"); // same day — should not fire again
            Assert.Single(spy.CancelledOrders);

            // Advance to next day
            settings.Now = At(16, 59, day: 2);
            follower.Orders.Add(MakeOrder("O3"));
            engine.Check("test"); // next day — fires again
            Assert.Equal(3, spy.CancelledOrders.Count);
        }

        [Fact]
        public void Eod_strategy_recreated_after_deadline_schedules_tomorrow()
        {
            var leader = MakeLeader();
            var follower = MakeFollower();
            var spy = new SpyFlattenService();
            var settings = new StubSettings
            {
                Leader = leader,
                AllAccountList = [leader, follower],
                EodEnabled = true,
                EodFlattenAt = new TimeSpan(16, 59, 0),
                Now = At(17, 30, day: 1), // started after today's deadline
            };

            leader.Positions.Add(MakePosition("leader"));
            follower.Orders.Add(MakeOrder("O1"));

            var engine = new FlattenEngine(new StubContext(new StubLogger(), settings, spy));
            engine.Check("test"); // should NOT fire — past today, scheduled for tomorrow

            Assert.Empty(spy.CancelledOrders);
        }

        #endregion

        #region Refresh timer

        [Fact]
        public void Refresh_timer_fires_and_triggers_flatten()
        {
            var leader = MakeLeader();
            var follower = MakeFollower();
            var spy = new SpyFlattenService(leader, follower);
            var settings = new StubSettings
            {
                Leader = leader,
                AllAccountList = [leader, follower],
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
            var leader = MakeLeader();
            var follower = MakeFollower();
            var spy = new SpyFlattenService();
            var settings = new StubSettings
            {
                Leader = leader,
                AllAccountList = [leader, follower],
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
            var (engine, _, spy, _, follower) = Make();
            follower.Orders.Add(MakeOrder("O1"));

            var threads = Enumerable.Range(0, 10)
                .Select(_ => new Thread(() => engine.Check("concurrent")))
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
            var (engine, _, spy, _, follower) = Make(syncDelayMs: 2000);
            follower.Orders.Add(MakeOrder("O1"));

            engine.Check("event");
            engine.Dispose();

            Thread.Sleep(2500);

            Assert.Empty(spy.CancelledOrders);
        }

        [Fact]
        public void Check_after_dispose_is_noop()
        {
            var (engine, _, spy, _, follower) = Make();
            follower.Orders.Add(MakeOrder("O1"));

            engine.Dispose();
            engine.Check("test");

            Assert.Empty(spy.CancelledOrders);
        }

        #endregion
    }
}
