using TradingPlatform.BusinessLayer;

namespace OmniFlattener
{
    /// <summary>
    /// Flattens all follower accounts when the leader goes flat.
    /// </summary>
    public partial class OmniFlattenerStrategy : Strategy
    {
        private FlattenEngine? _engine;

        public OmniFlattenerStrategy()
            : base()
        {
            Name        = "OmniFlattener";
            Description = "Cancels orders and closes positions on follower accounts when leader goes flat";
        }

        protected override void OnRun()
        {
            if (string.IsNullOrWhiteSpace(LeaderAccountName))
            {
                this.LogError("Leader account is not set.");
                Stop();
                return;
            }

            this.LogInfo($"Leader:       {LeaderAccountName}");
            this.LogInfo($"Disabled:     {(_disabledFollowers.Count > 0 ? string.Join(", ", _disabledFollowers) : "(none)")}");
            this.LogInfo($"Sync delay:   {SyncDelayMs}ms | Refresh: {RefreshIntervalMs}ms");

            var ctx = new FlattenContext(
                logger: new StrategyLogger(this),
                settings: new FlattenSettings(
                    leaderName:        LeaderAccountName,
                    disabledFollowers: _disabledFollowers,
                    syncDelayMs:       SyncDelayMs,
                    refreshIntervalMs: RefreshIntervalMs
                ),
                flattenService: new LiveFlattenService(this)
            );

            _engine = new FlattenEngine(ctx);

            Core.Instance.OrderAdded      += OnOrderEvent;
            Core.Instance.OrderRemoved    += OnOrderEvent;
            Core.Instance.PositionAdded   += OnPositionEvent;
            Core.Instance.PositionRemoved += OnPositionEvent;

            _engine.CheckLeaderIsFlat("startup");
        }

        protected override void OnStop()
        {
            Core.Instance.OrderAdded      -= OnOrderEvent;
            Core.Instance.OrderRemoved    -= OnOrderEvent;
            Core.Instance.PositionAdded   -= OnPositionEvent;
            Core.Instance.PositionRemoved -= OnPositionEvent;

            _engine?.Dispose();
            _engine = null;

            this.LogInfo("OmniFlattener stopped.");
        }

        private void OnOrderEvent(Order order)          => _engine?.CheckLeaderIsFlat("order-event");
        private void OnPositionEvent(Position position) => _engine?.CheckLeaderIsFlat("position-event");
    }
}
