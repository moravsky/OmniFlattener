using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace OmniFlattener
{
    public partial class OmniFlattenerStrategy
    {
        public string? LeaderAccountName { get; set; }
        public int     SyncDelayMs       { get; set; } = 3000;
        public int     RefreshIntervalMs { get; set; } = 5000;

        // Accounts the user explicitly opted out of. Everything else is flattened,
        // including new accounts that appear after the strategy started.
        private readonly HashSet<string> _disabledFollowers = new();

        private readonly List<SettingItem> _additionalSettings = [];

        // Rebuilt on every Settings.get so the account list reflects the current
        // connected set without requiring a strategy restart.
        public override IList<SettingItem> Settings
        {
            get
            {
                BuildSettings();
                var result = base.Settings;
                result.AddRange(_additionalSettings);
                return result;
            }
        }

        protected override void OnCreated()
        {
            base.OnCreated();
        }

        private void BuildSettings()
        {
            _additionalSettings.Clear();

            var leaderSetting = new SettingItemAccount(
                "Leader account",
                Core.Instance.Accounts.FirstOrDefault(a => a.Name == LeaderAccountName),
                sortIndex: 10);

            leaderSetting.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingItem.Value))
                    LeaderAccountName = (leaderSetting.Value as Account)?.Name;
            };

            var accountSettings = new List<SettingItem> { leaderSetting };
            int sortIndex = 20;

            foreach (var account in Core.Instance.Accounts
                .Where(a => a.State == BusinessObjectState.Normal)
                .OrderBy(a => a.Name))
            {
                if (account.Name == LeaderAccountName) continue;

                var name    = account.Name;
                bool enable = !_disabledFollowers.Contains(name);

                var checkbox = new SettingItemBoolean(name, enable, sortIndex++);
                checkbox.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName != nameof(SettingItem.Value) || checkbox.Value is not bool enabled)
                        return;

                    if (enabled) _disabledFollowers.Remove(name);
                    else         _disabledFollowers.Add(name);
                };

                accountSettings.Add(checkbox);
            }

            var syncDelaySetting = new SettingItemInteger("Sync delay (ms)", SyncDelayMs, sortIndex: 10)
            {
                Minimum = 0,
                Maximum = 30000,
                Description = """
                    Milliseconds to wait after detecting leader is flat before acting.
                    Absorbs TradeSyncer fill propagation lag (~1-2s).
                    If the leader resumes within this window the flatten is cancelled.
                    """,
            };
            syncDelaySetting.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingItem.Value) && syncDelaySetting.Value is int d)
                    SyncDelayMs = d;
            };

            var refreshIntervalSetting = new SettingItemInteger("Refresh interval (ms)", RefreshIntervalMs, sortIndex: 20)
            {
                Minimum = 100,
                Maximum = 60000,
                Description = "Backstop refresh frequency. Catches anything missed by order/position events.",
            };
            refreshIntervalSetting.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingItem.Value) && refreshIntervalSetting.Value is int p)
                    RefreshIntervalMs = p;
            };

            _additionalSettings.Add(new SettingItemGroup("Accounts", accountSettings));

            var timingGroup = new SettingItemSeparatorGroup("Timing");
            syncDelaySetting.SeparatorGroup    = timingGroup;
            refreshIntervalSetting.SeparatorGroup = timingGroup;

            _additionalSettings.Add(syncDelaySetting);
            _additionalSettings.Add(refreshIntervalSetting);
        }
    }
}
