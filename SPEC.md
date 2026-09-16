# MNQ Asia Session Sweep + IFVG Strategy

Locked specification. This document is the source of truth. Code follows this
document, not the other way around. Changes to behavior start with a change here.

## Instrument

Micro E-mini Nasdaq-100 futures (MNQ), most recent front-month contract, rolled
the same way a continuous MNQ chart rolls.

- Point value: $2.00 per index point
- Tick size: 0.25 points ($0.50 per tick)

## Timezone

All session times are America/New_York and follow the New York clock, so they
shift with daylight saving. "6 PM Eastern" means 6 PM in New York on that date,
EST or EDT.

## 1. Range definition

Each session day, record the highest high and lowest low printed from
6:00:00 PM through 6:59:59 PM New York time, measured from 1-minute candles and
including wicks.

- `range_high` is the buyside liquidity level
- `range_low` is the sellside liquidity level

Levels are locked at 7:00 PM and reset every day. Nothing carries over.
If the 6 PM hour has no data (holiday, outage), the day is skipped.

## 2. Trading window

The sweep and the confirming inversion close must both occur between 7:00 PM and
8:30 PM New York time. A confirming candle whose close stamps exactly 8:30 PM is
valid. After 8:30 PM the day is over, whether or not a setup was developing.

## 3. Sweep

A level is swept when price trades through it by any amount. A wick through
counts and a full candle close through counts. Detection uses 1-minute bars.

- Sellside sweep: a 1-minute low prints strictly below `range_low`
- Buyside sweep: a 1-minute high prints strictly above `range_high`

## 4. Direction

The first level swept inside the window sets the direction for the day.

| First sweep | Direction | FVG sought | Inversion event |
|---|---|---|---|
| `range_low` | Long | Bearish FVG | Candle closes above the FVG's upper boundary |
| `range_high` | Short | Bullish FVG | Candle closes below the FVG's lower boundary |

If both levels are swept before an entry is taken, the day is dead. There is no
take profit target left, so no trade.

## 5. Fair value gap qualification

Timeframes monitored: 30s, 1m, 2m, 3m, 5m, aligned to the hour (the 6 PM session
start is on an exact hour, so hour alignment matches TradingView's session
alignment for all of these intervals).

The 30-second rung is the last resort, used only when no higher timeframe
produced a qualifying gap. Building it requires tick data.

A three candle pattern `c1, c2, c3` on a given timeframe forms an FVG when:

- Bearish: `c3.high < c1.low`. Gap range is `[c3.high, c1.low]`, upper boundary `c1.low`.
- Bullish: `c3.low > c1.high`. Gap range is `[c1.high, c3.low]`, lower boundary `c1.high`.

An FVG qualifies when:

1. **It completes at or after the first sweep of the level.** The gap has to
   belong to the sweep, not merely occur on the same evening. A gap left higher
   up during the earlier decline is not the setup, and treating it as one makes
   the strategy wait for price to close above a level far from the sweep, which
   enters late at a fraction of the intended size and reward.
2. Its direction matches the sweep direction per section 4.
3. It has not already been closed through.
4. It is still fresh, if `max_bars_to_invert` is set. The confirming close must
   land within that many candles of the gap's own timeframe after it formed.
   An immediate reaction is the signal: the sweep happens, the gap forms in that
   sweep, and the next candle or two close beyond it, showing the real move is
   the other way. This also lets a wide higher timeframe gap age out on its own
   instead of holding the entry hostage while price runs away.

The FVG does not have to be created by the sweeping candle itself. This sequence
is explicitly valid: the level is swept, an FVG forms, price trades back up into
it, price re-sweeps the level, then price closes through the FVG. What the gap
cannot do is predate the first sweep entirely.

Only the **most recently formed** qualifying FVG per timeframe and direction is
tracked. A newer FVG replaces the older one.

An FVG is **spent** the first time price closes through its boundary. If that
close happens before any sweep, the FVG is consumed and can never trigger an
entry. Price must build a fresh FVG.

## 6. Timeframe selection

Two selectable entry models, both implemented, switched by `entry_mode`:

**`wait_highest_tf`** (default) - At each candle close, `H` is the highest
timeframe currently holding a live qualifying FVG. Only a close on timeframe `H`
can trigger entry. If a 30s FVG inverts while a live 5m FVG is waiting, the 30s
signal is ignored and the engine waits for the 5m candle to close. If `H` never
inverts before 8:30 PM, no trade is taken that day.

**`first_confirmation`** - The first qualifying inversion close on any timeframe
triggers entry. When several timeframes close on the same minute, the highest
timeframe wins.

In both modes, an inversion close marks its FVG spent whether or not the engine
acts on it.

## 7. Entry

Market order at the close of the confirming candle, in the direction from
section 4. Only one position may be open at a time.

`max_trades_per_session` caps attempts per session, defaulting to 1. Above 1, a
session re-arms for another attempt only when both hold:

1. The previous trade is closed.
2. Price has pushed **beyond the extreme that defined the previous trade's
   stop**. A shallower move is the same sweep continuing, not a new one.

On re-arm the gap registry is cleared, so only gaps built after the new sweep can
qualify. This is the mechanical form of a common sequence: the first inversion
fails, price runs deeper liquidity, and the second inversion is the real move.
Each attempt risks the full `risk_dollars`, so a cap of 2 doubles session risk.

## 8. Stop loss

The most extreme price printed during the sweep episode, measured on 1-minute
bars from the first sweep of the traded level through the entry bar inclusive.

- Long: lowest low over that span
- Short: highest high over that span

`stop_buffer_ticks` adds padding beyond that extreme, defaulting to 0. ICT's
guidance is that a stop parked exactly on the swept level often gets hunted on
the second test, so a few ticks of room is worth testing. The buffer is applied
before sizing, so contracts adjust for the wider risk.

## 8b. Entry proximity

A liquidity sweep pokes through a level and rejects. When price instead sweeps
and keeps running, the reversal premise is gone, and a gap left far below the
level that eventually inverts is not this setup.

If `max_entry_distance` is set, the entry must sit within that fraction of the
range height beyond the swept level. An entry a few points past the level is
normal and stays valid; one most of a range width past it is rejected.

## 8c. Sweep depth and the chase guard

Two separate ways the premise can be gone by the time an entry triggers.

`max_sweep_depth_points` voids the session when price runs further than that many
points past the level. ICT's distinction: past a certain depth the level was not
raided, it was broken, and what follows is a run rather than a reversal. Observed
range on real sessions: 45 and 94 points past the level both reversed and paid,
153 points did not.

`max_close_distance_past_gap` skips an inversion whose confirming close lands
further past the gap's boundary than that fraction of the range height. A close
37 points beyond a 3 point gap is one candle that already made the move, not a
controlled reversal. Unlike the other filters this skips only that inversion and
leaves the session open, since a later gap may still set up properly.

## 8d. Break even

`break_even_at_r` moves the stop to the entry price once the trade has run that
multiple of its own risk. Off by default. It converts full losers into scratches
on choppy sessions that oscillate inside the range, at the cost of being stopped
out of trades that dip and then recover.

## 9. Take profit

`target_mode` selects one of three:

**`opposite_level`** (default) - The other side of the range, exactly. Long
targets `range_high`, short targets `range_low`. If that level was already swept
before entry there is no target left and the trade is skipped (section 4).

**`fixed_r`** - `target_r_multiple` times the trade's own risk, measured from the
entry. Trades the largest winners away for a much higher hit rate.

**`fixed_points`** - `target_points` from the entry.

Both fixed modes are indifferent to the opposite level being swept, so that
condition only ends a session in `opposite_level` mode.

The original model produces the biggest winners and the worst hit rate: across
seven years, average favorable excursion is 1.30R while two thirds of trades
give nearly all of it back reaching for a level they never touch. A fixed target
exists to test whether capturing less of the move more often is worth more.

## 10. Position sizing

```
risk_points       = abs(entry - stop)
risk_per_contract = risk_points * point_value
contracts         = floor(risk_dollars / risk_per_contract)
```

`risk_dollars` defaults to $250 and is user-configurable. If one contract's risk
exceeds `risk_dollars`, the trade is skipped.

## 11. Exits

No time-based exit inside the session. The position runs until the stop or the
target is hit.

If `flatten_before_next_session` is set, any position still open when the next
session's range window begins is closed at market. Without it a trade can sit
for days waiting on its target, carrying risk through sessions that were never
analysed and blocking every setup in between.

## 12. Optional filters and toggles

| Setting | Default | Notes |
|---|---|---|
| `risk_dollars` | 250.0 | Max dollar risk per trade |
| `entry_mode` | `wait_highest_tf` | Or `first_confirmation` |
| `timeframes` | 1, 2, 3, 5 | Monitored FVG timeframes |
| `min_rr` | off | Optional minimum reward:risk, skip trade if below |
| `max_bars_to_invert` | off | Candles allowed between a gap forming and inverting |
| `max_entry_distance` | off | How far past the level an entry may sit, x range height |
| `flatten_before_next_session` | off | Close a position still open at the next session |
| `max_trades_per_session` | 1 | Attempts per session, each re-armed by a deeper sweep |
| `stop_buffer_ticks` | 0 | Ticks of padding beyond the sweep extreme |
| `max_sweep_depth_points` | off | Points past the level before the setup is void |
| `max_close_distance_past_gap` | off | How far past the gap a close may land, x range |
| `break_even_at_r` | off | R multiple at which the stop moves to entry |
| `target_mode` | opposite level | Or a fixed R multiple, or fixed points |
| `target_r_multiple` | 1.0 | Used in fixed R mode |
| `target_points` | 20 | Used in fixed points mode |
| `enabled_weekdays` | Sun-Thu | Per-weekday on/off, keyed to the 6 PM session date |
| `apply_slippage` | off | Ticks of adverse fill on entries and stops |
| `slippage_ticks` | 1.0 | Used when slippage is on |
| `apply_commissions` | off | Round-turn commission per contract |
| `commission_per_contract_rt` | 1.24 | Used when commissions are on |
| `max_contracts` | none | Optional hard cap on position size |

Take profit is a resting limit at the level, so no slippage is applied to it.
Slippage and commissions affect reported P&L only, not position sizing.

## 13. Backtest fill conventions

- Entry fills at the confirming candle's close price, adjusted for slippage.
- Exits are evaluated from the bar after entry onward, on 1-minute bars.
- If a single 1-minute bar contains both the stop and the target, the stop is
  assumed hit first.

## 14. Session skip reasons

Every session that produces no trade is recorded with a reason, so the backtest
reports how often each stage of the setup fails:

`weekday_disabled`, `no_range`, `no_window_data`, `no_sweep`, `sweep_no_fvg`,
`no_inversion`, `both_levels_swept`, `size_zero`, `min_rr`, `entry_too_far`,
`sweep_too_deep`, `position_open`

## Reference trades

Two hand-verified sessions used as behavioral fixtures.

**2026-09-09, 3-minute entry, long**

| Field | Value |
|---|---|
| Range high | 29,474.50 |
| Range low | 29,422.75 |
| First sweep | 7:09 PM |
| Sweep extreme | 29,414.50 (7:15 PM candle) |
| Confirming timeframe | 3m (no qualifying 5m FVG existed) |
| Entry | 29,427.50 |
| Stop | 29,414.50 |
| Target | 29,474.50 |
| Risk | 13.00 points, 9 contracts, $234.00 |
| Result | Target hit near 8:00 PM |

**2026-09-08, 1-minute entry, long**

| Field | Value |
|---|---|
| Range high | 29,550.25 |
| Range low | 29,503.25 |
| Sweep extreme | 29,489.75 |
| Confirming timeframe | 1m (only timeframe with a qualifying FVG) |
| Entry | 29,500.00 |
| Stop | 29,489.75 |
| Target | 29,550.25 |
| Risk | 10.25 points, 12 contracts, $246.00 |
| Result | Target hit |

Note that the second entry filled below the swept level. That is expected and
valid. The inversion close is what matters, not where it sits relative to the
level.
