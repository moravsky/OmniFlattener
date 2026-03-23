using System;
using System.Collections.Generic;

namespace OmniFlattener
{
    public interface IFlattenContext
    {
        IFlattenLogger Logger { get; }
        IFlattenSettings Settings { get; }
        IFlattenService FlattenService { get; }
    }

    public interface IFlattenLogger
    {
        void LogInfo(string message);
        void LogError(string message);
    }

    public interface IFlattenSettings
    {
        /// <summary>
        /// Returns null when the leader account is not currently connected.
        /// The engine treats a missing leader as not-flat — no flatten will fire.
        /// </summary>
        IAccountView? Leader { get; }

        /// <summary>
        /// Resolved fresh on every call: all active accounts minus disabled ones (leader included).
        /// The engine derives followers by excluding the leader.
        /// </summary>
        IReadOnlyList<IAccountView> AllAccounts { get; }

        int SyncDelayMs { get; }

        /// <summary>
        /// How often the engine checks account state. 0 suppresses the timer (tests).
        /// </summary>
        int RefreshIntervalMs { get; }
        bool CopyProtectionEnabled { get; }
        bool EodEnabled { get; }
        TimeSpan EodFlattenAt { get; }

        /// <summary>
        /// Current time in the user's selected timezone. Injected so tests control the clock.
        /// </summary>
        DateTime Now { get; }
    }

    public interface IFlattenService
    {
        void CancelOrder(OrderSnapshot order);
        void ClosePositionAtMarket(PositionSnapshot position);
    }

    public interface IAccountView
    {
        string AccountName { get; }
        List<OrderSnapshot> GetOpenOrders();
        List<PositionSnapshot> GetOpenPositions();
    }

    public class OrderSnapshot
    {
        public required string Id { get; set; }
        public required string AccountName { get; set; }
        public required string Symbol { get; set; }
        public required string Side { get; set; }
        public double Quantity { get; set; }
        public double Price { get; set; }
        public object? RawOrder { get; set; }
    }

    public class PositionSnapshot
    {
        public required string AccountName { get; set; }
        public required string Symbol { get; set; }
        public double Quantity { get; set; }
        public required string Side { get; set; }
        public object? RawPosition { get; set; }
    }
}
