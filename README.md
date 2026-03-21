# OmniFlattener

A Quantower strategy that cancels all open orders and closes all open positions on follower accounts when the leader account goes flat.

---

## How it works

1. Every order/position event and a configurable poll interval trigger a check
2. If the leader has no open orders **and** no open positions, an observation delay starts
3. If the leader remains flat through the delay, all follower orders are cancelled and all follower positions are closed at market
4. If the leader resumes activity before the delay elapses, the flatten is aborted

The observation delay (default 3s) is intentionally longer than fill propagation lag (~1-2s), so ghost orders from copy lag are already present and caught in the same sweep.

---

## Settings

| Setting | Default | Description |
|---|---|---|
| **Leader account** | — | The account to watch. |
| **Followers** | all checked | One checkbox per non-leader connected account. Uncheck to exclude (e.g. a cash account). Close and reopen the settings panel after changing the leader to refresh this list. |
| **Observation delay (s)** | `3` | Seconds to wait after detecting leader is flat before acting. Absorbs propagation lag. |
| **Poll interval (s)** | `5` | Poll frequency. Catches anything missed by order/position events. |

---

## Building

dotnet build

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
3. Set **Leader account** to your  primary account
4. Select follower accounts
5. Click **Run**

Check the strategy log on startup to confirm leader and followers resolved correctly:

```
Leader:            ACC-123456
Followers:         ACC-111111, ACC-222222
Observation delay: 3s | Poll: 5s
```

---
