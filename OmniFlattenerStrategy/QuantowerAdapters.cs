using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace OmniFlattener
{
    public class QuantowerAccountView(Account account) : IAccountView
    {
        private readonly Account _account = account ?? throw new ArgumentNullException(nameof(account));

        public string AccountName => _account.Name;

        public List<OrderSnapshot> GetOpenOrders() =>
            Core.Instance.Orders
                .Where(o => o.Account.Id == _account.Id && IsOpenOrder(o))
                .Select(o => new OrderSnapshot
                {
                    Id = o.Id,
                    AccountName = _account.Name,
                    Symbol = o.Symbol?.Name ?? "?",
                    Side = o.Side.ToString(),
                    Quantity = o.TotalQuantity,
                    Price = o.Price,
                    RawOrder = o
                })
                .ToList();

        public List<PositionSnapshot> GetOpenPositions() =>
            Core.Instance.Positions
                .Where(p => p.Account.Id == _account.Id && Math.Abs(p.Quantity) > 0)
                .Select(p => new PositionSnapshot
                {
                    AccountName = _account.Name,
                    Symbol = p.Symbol?.Name ?? "?",
                    Quantity = p.Quantity,
                    Side = p.Quantity > 0 ? "Sell" : "Buy",
                    RawPosition = p
                })
                .ToList();

        private static bool IsOpenOrder(Order o) =>
            o.Status == OrderStatus.Opened ||
            o.Status == OrderStatus.PartiallyFilled;
    }

    public class LiveFlattenService : IFlattenService
    {
        private readonly Action<string> _logError;

        public LiveFlattenService(Strategy strategy)
        {
            if (strategy == null) throw new ArgumentNullException(nameof(strategy));
            _logError = strategy.LogError;
        }

        public void CancelOrder(OrderSnapshot snapshot)
        {
            if (snapshot.RawOrder is not Order order)
            {
                _logError($"CancelOrder: RawOrder is not an Order ({snapshot.Id})");
                return;
            }

            var result = Core.Instance.CancelOrder((IOrder)order);
            if (result?.Status == TradingOperationResultStatus.Failure)
                _logError($"Cancel failed for {snapshot.Id}: {result.Message}");
        }

        public void ClosePositionAtMarket(PositionSnapshot snapshot)
        {
            if (snapshot.RawPosition is not Position position)
            {
                _logError($"ClosePosition: RawPosition is not a Position ({snapshot.AccountName}/{snapshot.Symbol})");
                return;
            }

            var result = Core.Instance.ClosePosition(position);
            if (result?.Status == TradingOperationResultStatus.Failure)
                _logError($"ClosePosition failed for {snapshot.AccountName}/{snapshot.Symbol}: {result.Message}");
        }
    }

    public class StrategyLogger : IFlattenLogger
    {
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;

        public StrategyLogger(Strategy strategy)
        {
            if (strategy == null) throw new ArgumentNullException(nameof(strategy));
            _logInfo = strategy.LogInfo;
            _logError = strategy.LogError;
        }

        public void LogInfo(string message) => _logInfo(message);
        public void LogError(string message) => _logError(message);
    }

    public class FlattenSettings(
        string? leaderName,
        HashSet<string> disabledFollowers,
        int syncDelayMs,
        int refreshIntervalMs,
        bool copyProtectionEnabled,
        bool eodEnabled,
        TimeSpan eodFlattenAt)
        : IFlattenSettings
    {
        private readonly string _leaderName = leaderName ?? ""; // empty = no leader, Leader property returns null

        private readonly HashSet<string> _disabledFollowers =
            disabledFollowers ?? throw new ArgumentNullException(nameof(disabledFollowers));

        public IAccountView? Leader
        {
            get
            {
                var account = Core.Instance.Accounts.FirstOrDefault(a =>
                    a.Name == _leaderName && a.State == BusinessObjectState.Normal);
                return account != null ? new QuantowerAccountView(account) : null;
            }
        }

        public IReadOnlyList<IAccountView> AllAccounts =>
            Core.Instance.Accounts
                .Where(a => a.State == BusinessObjectState.Normal
                            && !_disabledFollowers.Contains(a.Name))
                .Select(a => (IAccountView)new QuantowerAccountView(a))
                .ToList();

        public int SyncDelayMs => syncDelayMs;
        public int RefreshIntervalMs => refreshIntervalMs;
        public bool CopyProtectionEnabled => copyProtectionEnabled;
        public bool EodEnabled => eodEnabled;
        public TimeSpan EodFlattenAt => eodFlattenAt;

        public DateTime Now => DateTime.Now;
    }

    public class FlattenContext(IFlattenLogger logger, IFlattenSettings settings, IFlattenService flattenService)
        : IFlattenContext
    {
        public IFlattenLogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));
        public IFlattenSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

        public IFlattenService FlattenService { get; } =
            flattenService ?? throw new ArgumentNullException(nameof(flattenService));
    }
}
