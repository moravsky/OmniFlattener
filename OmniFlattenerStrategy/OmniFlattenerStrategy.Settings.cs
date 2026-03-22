using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace OmniFlattener
{
    public partial class OmniFlattenerStrategy
    {
        public string?   LeaderAccountName { get; set; }
        public int       SyncDelayMs       { get; set; } = 3000;
        public int       RefreshIntervalMs { get; set; } = 5000;
        public bool      EodEnabled        { get; set; } = true;
        public TimeSpan  EodFlattenAt      { get; set; } = new TimeSpan(16, 59, 0);

        private readonly HashSet<string>   _disabledFollowers  = new();
        private readonly List<SettingItem> _additionalSettings = [];

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

        private void BuildSettings()
        {
            _additionalSettings.Clear();
            _additionalSettings.Add(BuildAccountsGroup());
            BuildTimingSettings();
            BuildEodSettings();
        }

        private SettingItemGroup BuildAccountsGroup()
        {
            var leaderSetting = new SettingItemAccount(
                "Leader account",
                Core.Instance.Accounts.FirstOrDefault(a => a.Name == LeaderAccountName),
                sortIndex: 10);

            leaderSetting.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingItem.Value))
                    LeaderAccountName = (leaderSetting.Value as Account)?.Name;
            };

            var items     = new List<SettingItem> { leaderSetting };
            int sortIndex = 20;

            foreach (var account in Core.Instance.Accounts
                .Where(a => a.State == BusinessObjectState.Normal)
                .OrderBy(a => a.Name))
            {
                if (account.Name == LeaderAccountName) continue;

                var  name   = account.Name;
                bool enable = !_disabledFollowers.Contains(name);

                var checkbox = new SettingItemBoolean(name, enable, sortIndex++);
                checkbox.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(SettingItem.Value) || checkbox.Value is not bool enabled)
                        return;

                    if (enabled) _disabledFollowers.Remove(name);
                    else         _disabledFollowers.Add(name);
                };

                items.Add(checkbox);
            }

            return new SettingItemGroup("Accounts", items);
        }

        private void BuildTimingSettings()
        {
            var timingGroup = new SettingItemSeparatorGroup("Timing");

            var syncDelaySetting = new SettingItemInteger("Sync delay (ms)", SyncDelayMs, sortIndex: 10)
            {
                Minimum        = 0,
                Maximum        = 30000,
                SeparatorGroup = timingGroup,
                Description    = """
                    Milliseconds to wait after detecting leader is flat before acting.
                    Absorbs TradeSyncer fill propagation lag (~1-2s).
                    If the leader resumes within this window the flatten is cancelled.
                    """,
            };
            syncDelaySetting.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingItem.Value) && syncDelaySetting.Value is int d)
                    SyncDelayMs = d;
            };

            var refreshIntervalSetting = new SettingItemInteger("Refresh interval (ms)", RefreshIntervalMs, sortIndex: 20)
            {
                Minimum        = 100,
                Maximum        = 60000,
                SeparatorGroup = timingGroup,
                Description    = "Backstop refresh frequency. Catches anything missed by order/position events.",
            };
            refreshIntervalSetting.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingItem.Value) && refreshIntervalSetting.Value is int p)
                    RefreshIntervalMs = p;
            };

            _additionalSettings.Add(syncDelaySetting);
            _additionalSettings.Add(refreshIntervalSetting);
        }

        private void BuildEodSettings()
        {
            var eodGroup = new SettingItemSeparatorGroup("EOD Protection");

            var enabledSetting = new SettingItemBoolean("Enabled", EodEnabled, sortIndex: 30)
            {
                SeparatorGroup = eodGroup,
            };
            enabledSetting.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingItem.Value) && enabledSetting.Value is bool enabled)
                    EodEnabled = enabled;
            };

            const string preferredTemplate = "CME Indexes Full day";
            var templateNames = Core.Instance.CustomSessions
                .Select(c => c.Name)
                .OrderBy(n => n == preferredTemplate ? 0 : 1)
                .ThenBy(n => n)
                .ToList();

            var templateSetting = new SettingItemSelector(
                "Session template",
                templateNames.Contains(preferredTemplate) ? preferredTemplate : templateNames.FirstOrDefault() ?? "",
                templateNames,
                sortIndex: 40)
            {
                SeparatorGroup = eodGroup,
                Description    = "Selecting a template sets Flatten At to 2 minutes before the primary session close time.",
            };

            var flattenAtSetting = new SettingItemDateTime(
                "Flatten At",
                DateTime.Today + EodFlattenAt,
                sortIndex: 50)
            {
                SeparatorGroup = eodGroup,
                Description    = "Time of day (in machine local time) to flatten all accounts.",
            };
            flattenAtSetting.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingItem.Value) && flattenAtSetting.Value is DateTime dt)
                    EodFlattenAt = dt.TimeOfDay;
            };

            templateSetting.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingItem.Value)) ApplyTemplate();
            };

            // Apply immediately so Flatten At is in machine local time on first open
            ApplyTemplate();

            void ApplyTemplate()
            {
                var selectedName = templateSetting.Value as string;
                if (string.IsNullOrEmpty(selectedName)) return;

                var container = Core.Instance.CustomSessions
                    .FirstOrDefault(c => c.Name == selectedName);
                if (container == null) return;

                var primarySession =
                    container.ActiveSessions.FirstOrDefault(sess => sess.IsPrimary && sess.Type == SessionType.Main)
                    ?? container.ActiveSessions.FirstOrDefault(sess => sess.Type == SessionType.Main)
                    ?? container.ActiveSessions.FirstOrDefault();

                if (primarySession == null) return;

                // CloseTime is stored in UTC — convert directly to machine local.
                var closeLocal = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.UtcNow.Date + primarySession.CloseTime,
                    TimeZoneInfo.Local);
                EodFlattenAt           = closeLocal.TimeOfDay - TimeSpan.FromMinutes(2);
                flattenAtSetting.Value = DateTime.Today + EodFlattenAt;
            }

            _additionalSettings.Add(enabledSetting);
            _additionalSettings.Add(templateSetting);
            _additionalSettings.Add(flattenAtSetting);
        }
    }
}
