# OmniFlattener E2E Testing Guide

Replay connection, three accounts: `Leader`, `Follower1`, `Follower2`. Sync delay: 3000ms throughout.

---

## Setup

1. `.\deploy.ps1`, connect Quantower to Replay, create 3 accounts
2. Strategy Manager → OmniFlattener → Add instance
3. Leader: `Leader`, both followers checked, sync delay 3000ms, EOD enabled

---

## Core Scenarios (~30 min)

| Done | Scenario | Steps | Expected |
|------|----------|-------|----------|
| <input type="checkbox"> | **Basic flatten** | Open position on Leader + Follower1 + Follower2. Start strategy. Close leader position. | Both followers flattened. Log: `flattening followers` |
| <input type="checkbox"> | **Re-flatten while leader stays flat** | Do **Basic flatten**. While leader is still flat, open a new position on Follower1. | New position flattened. Log: `flattening followers` a second time. |
| <input type="checkbox"> | **Mixed state flatten** | Open a position and place a limit order on the Leader. Do the same for the Follower1. Close the Leader's position (leave order open) -> Verify no flatten. Then cancel the Leader's order. | Once the Leader's order is cancelled, Follower1's position is closed and its order is cancelled. |
| <input type="checkbox"> | **Sync delay cancels** | Follower1 has position. Leader goes flat, then place any order on Leader before sync delay elapses. | No flatten. Log: `sync delay cancelled` |
| <input type="checkbox"> | **Excluded follower ignored** | Uncheck Follower2 in settings. Open positions on Leader, Follower1, and Follower2. Close leader position. | Follower1 is flattened. Follower2 remains untouched. |
| <input type="checkbox"> | **Leader disconnect safety** | Start strategy with positions on followers. Disconnect the Leader account connection in Quantower. | No flatten fires. (Treats disconnected leader as not flat). |
| <input type="checkbox"> | **EOD flattens everyone** | Set Flatten At 5 min from now. Leader + Follower1 have positions. Wait. | Both flattened including leader. Log: `EOD flatten at HH:MM reached` |
| <input type="checkbox"> | EOD fires once only | After EOD fires, open a new position on the Leader (to prevent leader-flat logic), then open positions on followers. | No second flatten fires. |
| <input type="checkbox"> | **No leader — EOD only** | Clear leader account. Open positions. Wait for EOD time. | Strategy starts cleanly. Log: `(none — EOD protection only)`. EOD fires normally. |
| <input type="checkbox"> | **Settings timezone** | Open settings, select "CME Indexes Full day" template. | Flatten At shows correct local time for market close (ex. 1:58 PM MST, not EST or UTC). |
---

## Smoke Check Before Prod Deploy

<input type="checkbox"> Leader correct, EOD time correct  
<input type="checkbox"> Deploy Release build: `.\deploy.ps1 -Config Release`  
<input type="checkbox"> Scenario 1 passes on live eval account  
