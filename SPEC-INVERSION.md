# Inversion Model

Specification for the generalised inverse fair value gap model. Supersedes
SPEC.md, which covers the Asia-session-only version and stays as it is so the
two can be compared.

Source material: the Strategy Breakdown and iFVG Ratings Guide. Where those
documents and this one disagree, this one is what the code does.

## Scope

**Reversal model only.** The continuation model (retrace to 50% equilibrium of
the impulse leg, then invert a gap on the retrace leg) is deliberately out of
scope for the first version. Prove one before adding the other.

Advanced discretion, "breaking the rules", and trendline liquidity (LRLR) are out
of scope permanently. They are not mechanisable and pretending otherwise would
produce a backtest that flatters a judgment the code cannot make.

## 1. Sessions

Two windows per day, same account:

| Session | Range source | Entry window |
|---|---|---|
| Asia | see level registry | 7:00 PM - 9:00 PM New York |
| New York | see level registry | 9:30 AM - 11:30 AM New York |

Each window is independently toggleable. Prop accounts require flat positions
before the daily close, and an Asia trade must never survive into the New York
window, so each session carries a hard flat time.

## 2. Level registry

Every liquidity level is the same primitive: a price, a timestamp, a source and
a side. Sources are individually toggleable so their contribution can be
measured rather than assumed.

| Source | Definition |
|---|---|
| `prev_day` | Previous day's high and low |
| `prev_week` | Previous week's high and low |
| `h4` | Swing highs and lows on the 4-hour |
| `h1` | Swing highs and lows on the 1-hour |
| `m15` | Swing highs and lows on the 15-minute |
| `asia` | Asia killzone high and low |
| `london` | London killzone high and low |
| `ny_am` | New York AM killzone high and low |
| `ny_lunch` | New York lunch killzone high and low |
| `ny_pm` | New York PM killzone high and low |
| `eqh_eql` | Relative equal highs and lows |

**Ranking.** Higher timeframe outranks lower. `prev_week` > `prev_day` > `h4` > `h1` > `m15` >
killzone > `eqh_eql`. Rank is used for scoring and for choosing between competing
targets, not for eligibility.

**Mitigation.** A level is live only until price trades through it. Once taken,
the pool behind it is gone: the level stops extending, drops out of the candidate
set, and can neither arm a setup nor serve as a target again. This matches the
"Until Mitigated" behaviour of the killzone indicator the same levels are read
from by hand.

**Equal highs and lows.** Two highs are equal when they are within 5 ticks of
each other **and the first high is above the second**. Equal lows mirror this:
within 5 ticks, first low below the second. A second extreme that exceeds the
first is a break, not an equal. Off by default, since in practice these coincide
with other levels anyway.

## 2b. Gap registry

Fair value gaps live in the registry alongside liquidity levels, but they are a
different primitive and need their own lifecycle. A liquidity level is a price
you **sweep**, and it is dead once taken. A gap is a zone price **delivers
from**, and a tap does not end it.

| State | Trigger | Still a draw? |
|---|---|---|
| `unfilled` | Price has not entered the zone | Yes |
| `tapped` | Price traded inside but stayed above / below CE | Yes, degraded |
| `mitigated` | Price reached consequent encroachment, the 50% midpoint | No |
| `inverted` | A close on the gap's own timeframe passed the far edge | No, it is now an entry object |

**Consequent encroachment is the kill line.** Half the imbalance rebalanced is
enough; the gap stops being a draw on liquidity there rather than on a full
fill. Each tap short of CE drains the gap a little, so `taps` is recorded and
available to scoring, but a tapped gap is still a valid target.

**Inversion wins over mitigation.** Reaching CE and closing past the far edge
are not mutually exclusive: every inversion passes through CE on the way. The
fine series flags CE the moment it is touched, which correctly removes the gap
from the target set straight away, and the gap's own timeframe close upgrades it
to `inverted` if the close went all the way through. A bar that does both
inverted the gap, it did not spend it.

**Higher timeframe gaps are targeted unfilled, never inverted.** A 15m and up
gap is a draw on liquidity only while it is `unfilled` or `tapped`. Inverse fair
value gaps are the *entry* mechanism on the low timeframe ladder and are never a
target. These are two different uses of the same shape and the registry keeps
them apart.

**Timeframe is the first filter.** Only 15m, 1h, 4h and daily gaps are
registered, each individually toggleable. Below 15m a gap belongs to the entry
ladder in section 4, not here.

**Size is the second filter, measured in ATR** on the gap's own timeframe. A
threshold in points that is right at 29,000 is wrong at 7,000, and a percentage
of price ignores how much the instrument is actually moving.

| `gap_sensitivity` | ATR multiple |
|---|---|
| `sensitive` | 0.15 |
| `normal` | 0.35 (default) |
| `strict` | 0.75 |

`min_gap_points` remains as an absolute override, off by default. The three
sensitivity names come from the indicator; the multiples behind them are this
model's guess and need checking against a hand markup.

**`gap_detection`** is `single` or `series`. In `series` a fresh gap that
touches a live one of the same direction absorbs into it, so an inversion has to
close through the whole cluster rather than through one slice of it.

**Drawing.** A gap is detected when its third candle closes and drawn from the
close of its first, which is where the box starts in a hand markup. The box runs
`gap_extend_bars` bars of its own timeframe (default 12) and stops early if the
gap is mitigated or inverted before that. Gaps are numerous enough that
extending every one to the right edge makes the chart unreadable, and the box
only has to say where the gap is.

**Visibility is separate from detection.** A gap is drawn only when its own
timeframe is at or above the chart's, so a 15m gap does not render on a 4H or
daily chart. This is `gap_visibility`, with `all` and `chart_only` as
alternatives. A hidden gap is still registered, still tracked through its
lifecycle and still a valid target: the filter decides what is worth putting on
screen, nothing more. Gap labels default off and level labels default on, since
the labels rather than the boxes were what made a higher timeframe chart
unreadable.

A mitigated gap is removed from the live set unless `keep_mitigated` is on. The
per-bar touch check runs on every one minute bar, so a dead gap left in the list
is paid for thousands of times over.

Gaps carry their own `gap_lookback_days` (default 30), separate from the level
lookback, because a daily gap stays relevant far longer than a 15m swing high.

Gaps are used in three places: the `both` arming condition, the break-even
fallback when no internal extreme exists between entry and target (section 5),
and as a draw on liquidity target.

## 3. Arming condition

A setup is armed by a liquidity sweep, by delivery from a higher timeframe FVG,
or by both, selected by `arm_mode`:

| Mode | Requires |
|---|---|
| `sweep_only` | A registered level is swept |
| `delivery_only` | Price is delivering from a higher timeframe FVG |
| `either` | Either of the above (default, the A grade condition) |
| `both` | Both together (the A+ condition) |

Sweep keeps its existing definition: price trades through a level by any amount,
wick or close, detected on the finest enabled series.

The ratings guide calls a level paired with a higher timeframe FVG the highest
quality condition. That pairing is what `both` selects and what the scoring
model rewards.

## 4. Entry

Unchanged from the Asia model, which was verified against twelve hand-marked
sessions:

- Sellside swept, look for a **bearish** FVG, enter when a candle body closes
  **above** its upper boundary
- Buyside swept, look for a **bullish** FVG, enter when a candle body closes
  **below** its lower boundary

Timeframe ladder, gap freshness, the requirement that a gap form at or after the
sweep, the spent-gap rule and the highest-timeframe selection all carry over
unchanged.

## 5. Entry validity

**An entry is invalid if the next internal high (long) or low (short) has already
been taken at the moment of the confirming close.** That level is also the
break-even point, so an inversion that co-occurs with reaching it has no room.

This replaces `max_close_distance_past_gap`, which approximated the same idea
with a distance guess. The internal extreme is the correct definition.

Where no internal extreme exists between entry and target, the break-even point
falls back to the nearest unfilled gap in the trade's direction.

## 6. Stop loss

Selectable by `stop_mode`:

| Mode | Placement |
|---|---|
| `swing` | The sweep extreme, as the Asia model does today (default) |
| `gap_middle_candle` | Beyond the wick of the **second** candle of the original gap, which sits just past the inverted zone |
| `prior_candle` | Beyond the wick of the candle before the one that inverted the gap |
| `gap_far_edge` | Beyond the far boundary of the inverted gap |

`swing` is the widest and gives the trade the most room; the others size larger
for the same dollar risk and reach a fixed R target sooner. Which wins is an
empirical question, which is why all four exist.

`stop_buffer_ticks` applies to whichever mode is selected.

## 7. Target

**Baseline: the nearest qualifying liquidity level** in the trade's direction,
drawn from the same registry, respecting rank when two are close.

A setup with no qualifying target in its direction is not tradeable. This is the
mechanical form of the ratings guide's "targets are clear" criterion.

**R cap.** When `target_r_cap` is set, the trade exits at that R multiple,
full stop. The liquidity level still governs *validity* and a setup without one
is not tradeable, but the cap governs the exit even in the rare case where the
level is nearer. The point of the cap is to bank a known R on a trade whose full
draw might be three times that, so letting a nearby level override it would
defeat it.

## 8. Scoring

Every setup is scored on the five axes from the ratings guide, one point each,
and the grade is logged whether or not the trade is taken.

| Axis | Mechanical test |
|---|---|
| Sweep and delivery | 1.0 for both, 0.5 for either |
| Momentum, no chop | Confirming candle body-to-range ratio at or above threshold, and candles from sweep to inversion at or below threshold |
| Clear target | A qualifying liquidity level exists in the trade direction |
| Singular gap | Exactly one qualifying gap on the confirming timeframe in the zone |
| Premium / discount | Long below the 50% of the reference range, short above it |

| Score | Grade |
|---|---|
| 4.5 - 5.0 | A+ |
| 3.5 - 4.0 | A |
| 2.5 - 3.0 | B |
| below 2.5 | C |

`min_grade` sets the tradeable threshold. Logging the grade on every setup,
including rejected ones, is what turns "A+ only versus A and above" from an
argument into a measurement.

## 9. Carried over unchanged

Position sizing to a fixed dollar risk, the re-arm mechanism for a second
attempt after a deeper sweep, break-even at an R multiple, the diagnostic
drawing mode, per-weekday toggles and the CSV export.

## Session definitions

Used both as entry windows and as sources for session high/low levels. All are
configurable; these are the defaults.

Killzone windows are taken from the ICT Killzones & Pivots indicator, so the
code and the hand markup read the same levels.

| Window | Default |
|---|---|
| Asia killzone | 8:00 PM - 12:00 AM |
| London killzone | 2:00 AM - 5:00 AM |
| New York AM killzone | 9:00 AM - 11:30 AM |
| New York lunch killzone | 12:00 PM - 1:00 PM |
| New York PM killzone | 1:00 PM - 4:00 PM |
| Asia entry window | 7:00 PM - 9:00 PM |
| New York entry window | 9:30 AM - 11:30 AM |

Opening prices (midnight, 8:30, 9:30, 9:00 PM) are deliberately **not** levels in
this registry. An opening price is a reference for bias and for premium/discount,
not a pool of resting orders, so "sweeping" one carries no meaning. If they earn
a place later it will be as a scoring input, not as a sweep target.

## Cross-check against iFVG Ultimate (DodgysDD)

The TradingView indicator this model is derived from was read from its settings
panel and tooltips. Its behaviour is treated as evidence, not authority: where
it is more precise than this spec, this spec changes; where it is doing
something a discretionary trader needs and an algorithm does not, it is ignored.
The Pine source was not available, so everything below comes from the tooltips,
which state their formulas explicitly.

### Confirmed, no change needed

| This spec | The indicator |
|---|---|
| Entry validity: invalid if the break-even point is already taken at the confirming close (section 5) | "Require BE Not Breached on Inversion", on by default |
| Position sizing to a fixed dollar risk | "Contract Sizing, Risk ($)", default 250 |
| Opening prices are not sweep targets | Key Opens are drawn but are absent from the checklist |
| Trendline liquidity out of scope | LRL exists but shows only one line at a time, discretionary by construction |
| Stop modes | Wick / Body / Inversion, a subset of the four here |

### Adopted

**Gap size floor is measured in ATR.** `gap_sensitivity` replaces the point and
percent floors: `sensitive`, `normal`, `strict`, as multiples of ATR. A
threshold in points cannot hold across the 2019 to 2026 price range and a
percentage ignores volatility; ATR handles both.

**Volume influx.** `sma(volume, 5) > 2 x sma(volume, 100)` at the confirming
close. Available both as a hard filter and as a scoring axis.

**SMT divergence.** Against a correlated instrument, `CME_MINI:ES1!` by default.
At the sweep, the correlated instrument fails to make the corresponding extreme.
This is the only axis carrying information from outside the instrument, so it is
worth the extra data series it costs.

**Current timeframe does not count as higher timeframe delivery.** A 1m
inversion delivering from a 1m gap is not delivery from an HTF PDA. Without this
guard the `both` arming mode is satisfied almost always and the A+ grade means
nothing. Toggleable, off, matching the indicator.

**Macro windows.** The first and last fifteen minutes of the hour (00:45-00:15)
or the first and last ten (00:50-00:10). Optional filter, off by default.

**Previous week high and low** join the level registry, ranked with `prev_day`.

**Series detection.** `gap_detection` of `single` or `series`: whether adjacent
gaps combine into one zone for inversion purposes. A cluster of three small
gaps is functionally one zone and requiring a close through the whole cluster is
a different rule from requiring a close through one of them.

**Grades become C, B-, B+, A, A+**, matching the indicator, with `min_grade`
defaulting to A. Five buckets across a five point score rather than four.

### Resolved

**Mitigation is split by purpose**, because the two rules answer two questions:

| Question | Rule |
|---|---|
| Is there still a draw here to target? | Consequent encroachment |
| Is this a virgin PDA we are delivering from? | First touch, plus `mitigation_confirm_bars` (default 15) |

The confirm delay is the whole point of the second rule. An entry forming off a
fresh tap **is** delivery from an untouched gap, and a rule that killed the gap
on contact would reject the setup it exists to find. A CE-dead gap therefore
stays in the registry until its confirm delay also expires: no longer a target,
still a valid delivery source.

**Session windows follow iFVG Ultimate**, so a hand check on the chart and the
backtest always read the same session extremes: NY AM 9:00 - 11:30 and NY PM
1:00 - 4:00. Asia and London already agreed.

**SMT is a scoring axis, not a filter.** It is the only axis carrying
information from outside the instrument, which also makes it the one with no
evidence behind it yet. Logged on every setup, never blocks a trade, until the
backtest says whether it predicts anything.

### Still to calibrate

**`max_bars_to_invert`** has real anchors now: the indicator requires an IFVG to
form within 6 candles, or 15 with "Allow 7-15 Candle IFVGs" on. Both are worth
testing against the off setting this spec currently defaults to.

**`swing_strength`** anchors likewise: the indicator offers 3, 5 and 8, default
5. This spec defaults to 3.

### Deliberately not adopted

Hide Losing Setups, Number of Setups Displayed, Bias, and every label, colour
and alert setting are display concerns. An indicator can show two setups and let
a human choose; an algorithm has to decide, which is what `min_grade` and the
tie-break between simultaneous setups are for. Nothing in the indicator covers
position management, session flat times, daily loss limits or prop drawdown, so
none of that can be cross-checked and all of it stays this model's problem.

## Build order

1. **Level and gap registry**, with diagnostic drawing. Verified against a hand
   markup before anything else is built, because a level the code gets wrong
   invalidates every trade that references it.
2. Sessions and the arming condition.
3. Scoring.
4. Stops and targets.
