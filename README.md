# OmniFlattener

A Quantower strategy with two protection layers:

1. **Copy-trade safety** — cancels all open orders and closes all open positions on follower accounts when the leader account goes flat
2. **EOD protection** — flattens all accounts (leader included) at a configured time of day, preventing from holding positions past session close

---

## How it works

### Leader-flat detection
- Every order/position event and a configurable refresh interval trigger a check
- If the leader has no open orders **and** no open positions, a sync delay starts
- If the leader remains flat through the delay, all follower orders are cancelled and all follower positions are closed at market
- If the leader resumes activity before the delay elapses, the flatten is aborted

The sync delay (default 3s) is intentionally longer than fill propagation lag (~1-2s), so ghost orders from copy lag are already present and caught in the same sweep.

### EOD protection
- On every refresh tick, current machine time is compared against the configured flatten time
- When the time is reached, all selected accounts are flattened immediately — no sync delay
- After firing, the next trigger is scheduled for the same time tomorrow
- Starting or restarting the strategy after the day's flatten time schedules for the next day — no spurious immediate fire

---

## Settings

### Accounts tab
| Setting | Default | Description |
|---|---|---|
| **Leader account** | — | The account to watch. |
| **Follower checkboxes** | all checked | One checkbox per non-leader connected account. Uncheck to exclude. Close and reopen the settings panel after changing the leader to refresh this list. New accounts are included automatically. |

### View tab — EOD Protection
| Setting | Default | Description |
|---|---|---|
| **Enabled** | `true` | Whether EOD flattening is active. |
| **Session template** | CME Indexes Full day | Pre-fills Flatten At from the session's primary close time (2 min before). |
| **Flatten At** | `16:58` | Date and time to flatten all accounts, in machine local time. |

### View tab — Timing
| Setting | Default | Description |
|---|---|---|
| **Sync delay (ms)** | `3000` | Milliseconds to wait after detecting leader is flat before acting. Absorbs propagation lag. |
| **Refresh interval (ms)** | `1000` | Backstop refresh frequency. Catches anything missed by order/position events. Also drives the EOD time check. |

---

## Building

```
dotnet build
```

---

## Deployment

```powershell
# Debug build → C:\QuantowerDev (default)
.\deploy.ps1

# Release build → C:\Quantower
.\deploy.ps1 -Config Release

# Explicit overrides
.\deploy.ps1 -Config Debug -Target Prod
.\deploy.ps1 -Config Release -Target Dev
```

After deploying, restart Quantower or reload strategies from the Strategy Manager.

---

## Running

1. Open **Strategy Manager** in Quantower
2. Find **OmniFlattener** and click **Add instance**
3. Under **Accounts**, set the **Leader account** and verify follower checkboxes
4. Under **View**, confirm **Flatten At** shows the correct local time (select a session template to auto-fill)
5. Click **Run**

Check the strategy log on startup to confirm configuration:

```
Leader:       ACC-123456
Disabled:     ACC-111111, ACC-222222
Sync delay:   3000ms | Refresh: 5000ms
EOD:          enabled, flatten at 13:58
```
