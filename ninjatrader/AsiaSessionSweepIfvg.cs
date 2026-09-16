// MNQ Asia Session Liquidity Sweep + Inverse Fair Value Gap
//
// Port of asia_atm/engine.py. The rules live in SPEC.md; this file follows that
// document. See ninjatrader/SETUP.md for install, compile and backtest steps.
//
// Marks the 6:00-7:00 PM New York range, then between 7:00 and 8:30 PM waits for
// a sweep of either level, a fair value gap on 30s/1m/2m/3m/5m, and a close back
// through that gap. Enters on the confirming close, stops at the swing extreme of
// the sweep, targets the opposite level, sized to a fixed dollar risk.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public enum AsiaIfvgEntryMode
	{
		WaitForHighestTimeframe,
		FirstConfirmation
	}

	public enum AsiaIfvgTargetMode
	{
		OppositeLevel,
		FixedRMultiple,
		FixedPoints
	}

	public class AsiaSessionSweepIfvg : Strategy
	{
		#region Nested state

		private class Gap
		{
			public bool		Bearish;
			public double	Bottom;
			public double	Top;
			public DateTime	FormedAt;
			public bool		Spent;

			public double Boundary { get { return Bearish ? Top : Bottom; } }

			public bool InvertedBy(double close, DateTime closeTime)
			{
				if (Spent || closeTime <= FormedAt)
					return false;
				return Bearish ? close > Top : close < Bottom;
			}
		}

		private class Slot
		{
			public int		Seconds;
			public int		Bip;
			public string	Label;
			public Gap		Bearish;
			public Gap		Bullish;

			public Gap Directional(bool wantBearish)
			{
				return wantBearish ? Bearish : Bullish;
			}
		}

		private class Inversion
		{
			public Slot		Slot;
			public Gap		Gap;
			public double	Close;
		}

		#endregion

		private const string SignalName = "AsiaIfvg";

		private List<Slot>		slots;
		private List<Inversion>	pendingInversions;

		private DateTime	sessionDate		= DateTime.MinValue;
		private double		rangeHigh;
		private double		rangeLow;
		private bool		rangeValid;
		private bool		sessionDone;
		private bool		entryTaken;
		private bool		sessionLocked;		// ended for a reason no re-arm can undo
		private int			tradesTaken;
		private DateTime	armedFromTime	= DateTime.MinValue;
		private double		armExtreme;
		private double		beEntry;
		private double		beRisk;
		private int			beDirection;
		private bool		beDone;
		private bool		sawQualifyingGap;
		private bool		rangeAnnounced;

		private int			direction;					// 1 long, -1 short, 0 undecided
		private DateTime	lowSweptAt		= DateTime.MinValue;
		private DateTime	highSweptAt		= DateTime.MinValue;
		private double		sweepExtreme;

		private DateTime	pendingEvalTime	= DateTime.MinValue;
		private int			pendingCount;

		private int			rangeStartSec;
		private int			rangeEndSec;
		private int			tradeEndSec;

		#region Lifecycle

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Asia session liquidity sweep into an inverse fair value gap.";
				Name						= "AsiaSessionSweepIfvg";
				Calculate					= Calculate.OnBarClose;
				EntriesPerDirection			= 1;
				EntryHandling				= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = false;	// the spec has no time-based exit
				IsFillLimitOnTouch			= false;
				BarsRequiredToTrade			= 4;
				StartBehavior				= StartBehavior.WaitUntilFlat;
				TimeInForce					= TimeInForce.Gtc;
				TraceOrders					= false;
				RealtimeErrorHandling		= RealtimeErrorHandling.StopCancelCloseIgnoreRejects;
				StopTargetHandling			= StopTargetHandling.PerEntryExecution;

				RiskDollars					= 250;
				EntryMode					= AsiaIfvgEntryMode.WaitForHighestTimeframe;
				MinimumRewardRisk			= 0;			// 0 disables the filter
				MaxBarsToInvert				= 0;			// 0 disables the freshness rule
				MaxEntryDistanceFromLevel	= 0;			// 0 disables the proximity rule
				FlattenBeforeNextSession	= false;
				MaxContracts				= 0;			// 0 means no cap
				MaxTradesPerSession			= 1;
				TargetMode					= AsiaIfvgTargetMode.OppositeLevel;
				TargetRMultiple				= 1.0;
				TargetPoints				= 20;
				StopBufferTicks				= 0;			// ticks beyond the sweep extreme
				MaxSweepDepthPoints			= 0;			// 0 disables the depth cap
				MaxCloseDistancePastGap		= 0;			// 0 disables the chase guard
				BreakEvenAtR				= 0;			// 0 disables break even
				PointValueOverride			= 0;			// 0 uses the instrument's own

				Use30Second					= true;
				Use1Minute					= true;
				Use2Minute					= true;
				Use3Minute					= true;
				Use5Minute					= true;

				RangeStartTime				= 1800;
				RangeEndTime				= 1900;
				TradeEndTime				= 2030;

				TradeSunday					= true;
				TradeMonday					= true;
				TradeTuesday				= true;
				TradeWednesday				= true;
				TradeThursday				= true;

				ShowDrawings				= true;
				LogDetail					= true;
				DiagnosticMode				= false;
				ExportCsvPath				= string.Empty;
			}
			else if (State == State.Configure)
			{
				slots				= new List<Slot>();
				pendingInversions	= new List<Inversion>();

				// Added smallest first. Order does not affect correctness: evaluation
				// waits until every series closing on a timestamp has been processed.
				if (Use30Second)	AddSlot(30,		"30s");
				if (Use1Minute)		AddSlot(60,		"1m");
				if (Use2Minute)		AddSlot(120,	"2m");
				if (Use3Minute)		AddSlot(180,	"3m");
				if (Use5Minute)		AddSlot(300,	"5m");

				rangeStartSec	= ToSeconds(RangeStartTime);
				rangeEndSec		= ToSeconds(RangeEndTime);
				tradeEndSec		= ToSeconds(TradeEndTime);
			}
			else if (State == State.DataLoaded)
			{
				ResolveSeriesIndexes();
			}
		}

		private void AddSlot(int seconds, string label)
		{
			if (seconds % 60 == 0)
				AddDataSeries(BarsPeriodType.Minute, seconds / 60);
			else
				AddDataSeries(BarsPeriodType.Second, seconds);

			// The real index is resolved in State.DataLoaded by inspecting each
			// loaded series, because NinjaTrader may reuse the primary series when
			// an added one matches it rather than creating a duplicate.
			slots.Add(new Slot { Seconds = seconds, Label = label, Bip = -1 });
		}

		/// <summary>
		/// Match every timeframe to the series that actually got loaded, rather than
		/// assuming series are indexed in the order they were added.
		/// </summary>
		private void ResolveSeriesIndexes()
		{
			if (slots == null || slots.Count == 0)
			{
				Print("AsiaSessionSweepIfvg: no timeframes enabled, this will never trade.");
				return;
			}

			foreach (Slot slot in slots)
			{
				slot.Bip = -1;
				for (int i = 0; i < BarsArray.Length; i++)
				{
					if (BarsArray[i] == null)
						continue;
					if (SecondsOf(BarsArray[i].BarsPeriod) == slot.Seconds)
					{
						slot.Bip = i;
						break;
					}
				}
			}

			// Two timeframes must never share a series, or their gaps would collide.
			for (int a = 0; a < slots.Count; a++)
				for (int b = a + 1; b < slots.Count; b++)
					if (slots[a].Bip >= 0 && slots[a].Bip == slots[b].Bip)
						slots[b].Bip = -1;

			Print("");
			Print("AsiaSessionSweepIfvg loaded. Series mapping:");
			foreach (Slot slot in slots)
				Print(string.Format("    {0,-4} -> {1}", slot.Label,
					slot.Bip < 0 ? "NOT LOADED, this timeframe is inactive" : "series " + slot.Bip));
			Print(string.Format("    session window {0} to {1}, last entry {2}, chart time",
				RangeStartTime, RangeEndTime, TradeEndTime));
			Print("");
		}

		private static int SecondsOf(BarsPeriod period)
		{
			if (period.BarsPeriodType == BarsPeriodType.Minute)
				return period.Value * 60;
			if (period.BarsPeriodType == BarsPeriodType.Second)
				return period.Value;
			return 0;
		}

		private static int ToSeconds(int hhmm)
		{
			return (hhmm / 100) * 3600 + (hhmm % 100) * 60;
		}

		#endregion

		#region Bar handling

		protected override void OnBarUpdate()
		{
			if (slots == null)
				return;

			// The primary series is used when it matches one of our timeframes,
			// and ignored otherwise. SlotFor decides.
			Slot slot = SlotFor(BarsInProgress);
			if (slot == null || CurrentBars[BarsInProgress] < 3)
				return;

			// Runs before the session window filter, because a trade can reach its
			// break even trigger long after the last entry time has passed.
			ManageBreakEven();

			// NinjaTrader stamps a bar with its CLOSE time.
			DateTime closeTime	= Times[BarsInProgress][0];
			int tod				= closeTime.Hour * 3600 + closeTime.Minute * 60 + closeTime.Second;

			bool inRange	= tod > rangeStartSec && tod <= rangeEndSec;
			bool inWindow	= tod > rangeEndSec && tod <= tradeEndSec;
			bool afterHours	= tod > tradeEndSec && tod <= tradeEndSec + 3600;

			if (!inRange && !inWindow && !afterHours)
				return;

			if (closeTime.Date != sessionDate)
			{
				FinalizeSession();
				FlattenStalePosition();
				StartSession(closeTime.Date);
			}

			if (sessionLocked)
				return;

			// Deliberately after the session roll, so a timestamp left pending when
			// a session ended cannot be evaluated against the next session's state.
			FlushPending(closeTime);

			if (inRange)
			{
				rangeHigh	= rangeValid ? Math.Max(rangeHigh, Highs[BarsInProgress][0]) : Highs[BarsInProgress][0];
				rangeLow	= rangeValid ? Math.Min(rangeLow, Lows[BarsInProgress][0])   : Lows[BarsInProgress][0];
				rangeValid	= true;
				return;
			}

			if (afterHours)
			{
				ExpireSession();
				return;
			}

			if (!rangeValid)
			{
				FinishSession("no_range");
				return;
			}

			if (!rangeAnnounced)
			{
				rangeAnnounced = true;
				LogLine(string.Format("range locked, {0} high / {1} low",
					Format(rangeHigh), Format(rangeLow)));
			}

			DrawLevels(closeTime);

			// Sweeps keep tracking after an entry, because a deeper one is what
			// re-arms the session for a second attempt.
			UpdateSweeps(BarsInProgress, closeTime);
			ConsiderRearm(closeTime);

			// Hard stop on a second simultaneous entry. Position.MarketPosition
			// cannot be used for this: on bar close the entry order does not fill
			// until the next bar, so the position still reads Flat while another
			// signal is being evaluated.
			if (sessionDone || entryTaken)
				return;

			RegisterGap(slot, BarsInProgress, closeTime);
			CollectInversion(slot, BarsInProgress, closeTime);

			if (pendingEvalTime != closeTime)
			{
				pendingEvalTime	= closeTime;
				pendingCount	= 0;
			}
			pendingCount++;

			if (pendingCount >= ExpectedClosers(closeTime))
			{
				Evaluate(closeTime);
				pendingEvalTime	= DateTime.MinValue;
				pendingCount	= 0;
			}
		}

		private Slot SlotFor(int bip)
		{
			foreach (Slot slot in slots)
				if (slot.Bip == bip && bip >= 0)
					return slot;
			return null;
		}

		/// <summary>How many enabled series close on this timestamp.</summary>
		private int ExpectedClosers(DateTime closeTime)
		{
			int secondsIntoHour = closeTime.Minute * 60 + closeTime.Second;
			int count = 0;
			foreach (Slot slot in slots)
				if (slot.Bip >= 0 && secondsIntoHour % slot.Seconds == 0)
					count++;
			return Math.Max(count, 1);
		}

		/// <summary>
		/// Evaluate a timestamp whose series did not all report, which happens when
		/// a period contained no trades and NinjaTrader produced no bar for it.
		/// </summary>
		private void FlushPending(DateTime now)
		{
			if (pendingEvalTime != DateTime.MinValue && now > pendingEvalTime)
			{
				Evaluate(pendingEvalTime);
				pendingEvalTime	= DateTime.MinValue;
				pendingCount	= 0;
			}
		}

		#endregion

		#region Session state

		private void StartSession(DateTime date)
		{
			sessionDate			= date;
			rangeHigh			= 0;
			rangeLow			= 0;
			rangeValid			= false;
			sessionDone			= false;
			entryTaken			= false;
			sessionLocked		= false;
			tradesTaken			= 0;
			armedFromTime		= DateTime.MinValue;
			armExtreme			= 0;
			beRisk				= 0;
			beDone				= false;
			sawQualifyingGap	= false;
			rangeAnnounced		= false;
			direction			= 0;
			lowSweptAt			= DateTime.MinValue;
			highSweptAt			= DateTime.MinValue;
			sweepExtreme		= 0;
			pendingEvalTime		= DateTime.MinValue;
			pendingCount		= 0;
			pendingInversions.Clear();

			foreach (Slot slot in slots)
			{
				slot.Bearish = null;
				slot.Bullish = null;
			}

			if (!IsWeekdayEnabled(date.DayOfWeek))
				FinishSession("weekday_disabled");
		}

		private bool IsWeekdayEnabled(DayOfWeek day)
		{
			switch (day)
			{
				case DayOfWeek.Sunday:		return TradeSunday;
				case DayOfWeek.Monday:		return TradeMonday;
				case DayOfWeek.Tuesday:		return TradeTuesday;
				case DayOfWeek.Wednesday:	return TradeWednesday;
				case DayOfWeek.Thursday:	return TradeThursday;
				default:					return false;
			}
		}

		private void FinishSession(string reason)
		{
			if (sessionDone)
				return;
			sessionDone = true;
			if (reason != "traded")
				sessionLocked = true;
			if (LogDetail && reason != "traded")
				Print(string.Format("{0:yyyy-MM-dd}  no trade: {1}", sessionDate, reason));
		}

		private void ExpireSession()
		{
			if (sessionDone)
				return;
			if (!rangeValid)			FinishSession("no_window_data");
			else if (direction == 0)	FinishSession("no_sweep");
			else if (!sawQualifyingGap)	FinishSession("sweep_no_fvg");
			else						FinishSession("no_inversion");
		}

		private void FinalizeSession()
		{
			if (sessionDate != DateTime.MinValue)
				ExpireSession();
		}

		/// <summary>
		/// Give the session a second attempt once the first trade is closed and
		/// price has pushed beyond the extreme that defined its stop. A shallower
		/// move is the same sweep continuing, not a new one, so it does not re-arm.
		/// Gaps are cleared so only ones built after the new sweep can qualify.
		/// </summary>
		/// <summary>
		/// Pull the stop to the entry price once the trade has run the configured
		/// multiple of its own risk. Turns a full loser into a scratch at the cost
		/// of being stopped out of trades that would have recovered.
		/// </summary>
		private void ManageBreakEven()
		{
			if (BreakEvenAtR <= 0 || beDone || beRisk <= 0)
				return;
			if (Position.MarketPosition == MarketPosition.Flat)
				return;

			double trigger = beDirection == 1
				? beEntry + BreakEvenAtR * beRisk
				: beEntry - BreakEvenAtR * beRisk;

			bool reached = beDirection == 1
				? Highs[BarsInProgress][0] >= trigger
				: Lows[BarsInProgress][0] <= trigger;
			if (!reached)
				return;

			beDone = true;
			double moved = Instrument.MasterInstrument.RoundToTickSize(beEntry);
			SetStopLoss(SignalName, CalculationMode.Price, moved, false);
			LogLine(string.Format("reached {0:F2}R, stop moved to break even at {1}",
				BreakEvenAtR, Format(moved)));
		}

		private void ConsiderRearm(DateTime closeTime)
		{
			if (!entryTaken || sessionLocked || direction == 0)
				return;
			if (tradesTaken >= MaxTradesPerSession)
				return;
			if (Position.MarketPosition != MarketPosition.Flat)
				return;

			bool deeper = direction == 1 ? sweepExtreme < armExtreme : sweepExtreme > armExtreme;
			if (!deeper)
				return;

			entryTaken			= false;
			sessionDone			= false;
			sawQualifyingGap	= false;
			armedFromTime		= closeTime;
			pendingInversions.Clear();
			pendingEvalTime		= DateTime.MinValue;
			pendingCount		= 0;

			foreach (Slot each in slots)
			{
				each.Bearish = null;
				each.Bullish = null;
			}

			LogLine(string.Format("re-armed at {0:HH:mm:ss}, price swept on to {1}, attempt {2}",
				closeTime, Format(sweepExtreme), tradesTaken + 1));
		}

		/// <summary>
		/// Close a position still open when the next session begins. Without this a
		/// trade can sit for days waiting on its target, carrying risk through
		/// sessions that were never analysed and blocking every setup in between.
		/// </summary>
		private void FlattenStalePosition()
		{
			if (!FlattenBeforeNextSession || Position.MarketPosition == MarketPosition.Flat)
				return;

			LogLine(string.Format("flattening a position still open at the next session, {0} @ {1}",
				Position.Quantity, Format(Position.AveragePrice)));

			if (Position.MarketPosition == MarketPosition.Long)
				ExitLong(0, Position.Quantity, "AsiaFlat", SignalName);
			else
				ExitShort(0, Position.Quantity, "AsiaFlat", SignalName);
		}

		#endregion

		#region Sweeps

		private void UpdateSweeps(int bip, DateTime closeTime)
		{
			double high	= Highs[bip][0];
			double low	= Lows[bip][0];

			if (lowSweptAt == DateTime.MinValue && low < rangeLow)
			{
				lowSweptAt = closeTime;
				if (direction == 0)
				{
					direction = 1;
					armedFromTime = closeTime;
					LogLine(string.Format("sweep of the low at {0:HH:mm:ss}, {1} traded below {2}",
						closeTime, Format(low), Format(rangeLow)));
					DrawSweepMarker(closeTime, low);
				}
			}

			if (highSweptAt == DateTime.MinValue && high > rangeHigh)
			{
				highSweptAt = closeTime;
				if (direction == 0)
				{
					direction = -1;
					armedFromTime = closeTime;
					LogLine(string.Format("sweep of the high at {0:HH:mm:ss}, {1} traded above {2}",
						closeTime, Format(high), Format(rangeHigh)));
					DrawSweepMarker(closeTime, high);
				}
			}

			// Only fatal when the opposite level is the target. A fixed R or point
			// target does not care that the other side was taken.
			if (lowSweptAt != DateTime.MinValue && highSweptAt != DateTime.MinValue
				&& TargetMode == AsiaIfvgTargetMode.OppositeLevel)
			{
				FinishSession("both_levels_swept");
				return;
			}

			if (direction == 1)
				sweepExtreme = sweepExtreme == 0 ? low : Math.Min(sweepExtreme, low);
			else if (direction == -1)
				sweepExtreme = sweepExtreme == 0 ? high : Math.Max(sweepExtreme, high);

			// Past a certain depth the level was not swept, it was broken. ICT calls
			// this a run rather than a raid, and the reversal premise is gone.
			if (MaxSweepDepthPoints > 0 && direction != 0 && !entryTaken)
			{
				double level = direction == 1 ? rangeLow : rangeHigh;
				double depth = direction == 1 ? level - sweepExtreme : sweepExtreme - level;
				if (depth > MaxSweepDepthPoints)
				{
					LogLine(string.Format("price ran {0} points past the level, this is a breakdown",
						Format(depth)));
					FinishSession("sweep_too_deep");
				}
			}
		}

		#endregion

		#region Gaps

		private void RegisterGap(Slot slot, int bip, DateTime closeTime)
		{
			double c1High = Highs[bip][2], c1Low = Lows[bip][2];
			double c3High = Highs[bip][0], c3Low = Lows[bip][0];

			Gap formed = null;
			if (c3High < c1Low)
				formed = slot.Bearish = new Gap { Bearish = true, Bottom = c3High, Top = c1Low, FormedAt = closeTime };
			else if (c3Low > c1High)
				formed = slot.Bullish = new Gap { Bearish = false, Bottom = c1High, Top = c3Low, FormedAt = closeTime };

			if (formed != null && DiagnosticMode)
			{
				LogLine(string.Format("{0:HH:mm:ss} {1} {2} gap formed {3} to {4}",
					closeTime, slot.Label, formed.Bearish ? "bearish" : "bullish",
					Format(formed.Bottom), Format(formed.Top)));
				DrawDiagnosticGap(slot, formed, closeTime);
			}
		}

		/// <summary>
		/// Shade every gap the engine registers, labelled by timeframe, so its
		/// reading of the chart can be checked against yours candle by candle.
		/// Bearish gaps draw warm, bullish cool. Off by default: on a multi-year
		/// run this produces thousands of objects.
		/// </summary>
		private void DrawDiagnosticGap(Slot slot, Gap gap, DateTime closeTime)
		{
			if (!ShowDrawings)
				return;

			string tag = "dg" + slot.Label + closeTime.Ticks;
			Draw.Rectangle(this, tag, false, closeTime, gap.Bottom,
				closeTime.AddSeconds(slot.Seconds * 8), gap.Top,
				Brushes.Transparent, gap.Bearish ? Brushes.OrangeRed : Brushes.DodgerBlue, 12);
			Draw.Text(this, tag + "L", false, slot.Label, closeTime, gap.Top, 6,
				gap.Bearish ? Brushes.OrangeRed : Brushes.DodgerBlue,
				new SimpleFont("Arial", 9), System.Windows.TextAlignment.Left,
				Brushes.Transparent, Brushes.Transparent, 0);
		}

		private void CollectInversion(Slot slot, int bip, DateTime closeTime)
		{
			double close = Closes[bip][0];

			if (slot.Bearish != null && slot.Bearish.InvertedBy(close, closeTime))
				pendingInversions.Add(new Inversion { Slot = slot, Gap = slot.Bearish, Close = close });

			if (slot.Bullish != null && slot.Bullish.InvertedBy(close, closeTime))
				pendingInversions.Add(new Inversion { Slot = slot, Gap = slot.Bullish, Close = close });
		}

		#endregion

		#region Evaluation

		/// <summary>
		/// A gap must belong to the sweep, and still be fresh enough to act on.
		///
		/// Belonging means it formed at or after the sweep. A gap left higher up
		/// during the earlier decline is not the setup, and waiting for price to
		/// close through it enters far from the sweep, long after the move.
		///
		/// Freshness means price closed through it within MaxBarsToInvert candles
		/// of that gap's own timeframe. An immediate reaction is the signal. It
		/// also lets wide higher timeframe gaps age out on their own rather than
		/// holding the entry hostage.
		/// </summary>
		private bool Qualifies(Gap gap, int seconds, DateTime now)
		{
			if (gap == null || gap.Spent || direction == 0)
				return false;

			if (armedFromTime == DateTime.MinValue || gap.FormedAt < armedFromTime)
				return false;

			if (MaxBarsToInvert > 0)
			{
				double age = (now - gap.FormedAt).TotalSeconds / seconds;
				if (age > MaxBarsToInvert)
					return false;
			}
			return true;
		}

		private void Evaluate(DateTime closeTime)
		{
			if (sessionDone)
			{
				pendingInversions.Clear();
				return;
			}

			bool wantBearish	= direction == 1;
			bool haveDirection	= direction != 0;
			int requiredSeconds	= 0;

			if (haveDirection)
			{
				int highestLive = 0;
				foreach (Slot slot in slots)
				{
					if (Qualifies(slot.Directional(wantBearish), slot.Seconds, closeTime)
						&& slot.Seconds > highestLive)
						highestLive = slot.Seconds;
				}

				if (highestLive > 0)
				{
					sawQualifyingGap = true;
					if (EntryMode == AsiaIfvgEntryMode.WaitForHighestTimeframe)
						requiredSeconds = highestLive;
				}
			}

			// Highest timeframe first, so a shared timestamp resolves in its favour.
			pendingInversions.Sort(delegate(Inversion a, Inversion b)
			{
				return b.Slot.Seconds.CompareTo(a.Slot.Seconds);
			});

			bool entered = false;
			foreach (Inversion inversion in pendingInversions)
			{
				bool belongedToSweep = Qualifies(inversion.Gap, inversion.Slot.Seconds, closeTime);
				// An inversion consumes the gap whether or not it is traded.
				inversion.Gap.Spent = true;

				if (entered || sessionDone || !haveDirection)
					continue;
				if (inversion.Gap.Bearish != wantBearish || !belongedToSweep)
					continue;
				if (requiredSeconds > 0 && inversion.Slot.Seconds != requiredSeconds)
				{
					LogLine(string.Format("{0:HH:mm:ss} skipped {1} inversion, waiting on the {2} gap",
						closeTime, inversion.Slot.Label, SecondsLabel(requiredSeconds)));
					continue;
				}

				entered = TryEnter(inversion, closeTime);
			}

			pendingInversions.Clear();
		}

		/// <summary>
		/// The opposite level is the original model and gives the largest winners,
		/// but two thirds of trades give back most of their favorable excursion
		/// reaching for it. A fixed R or point target trades that upside for a much
		/// higher hit rate, which suits a prop account better.
		/// </summary>
		private double ResolveTarget(double entry, double risk)
		{
			switch (TargetMode)
			{
				case AsiaIfvgTargetMode.FixedRMultiple:
					return direction == 1
						? entry + TargetRMultiple * risk
						: entry - TargetRMultiple * risk;

				case AsiaIfvgTargetMode.FixedPoints:
					return direction == 1 ? entry + TargetPoints : entry - TargetPoints;

				default:
					return direction == 1 ? rangeHigh : rangeLow;
			}
		}

		private bool TryEnter(Inversion inversion, DateTime closeTime)
		{
			double entry	= inversion.Close;
			// ICT: a stop parked exactly at the swept level often gets hunted again
			// on the second test, so allow a buffer beyond the extreme.
			double buffer	= StopBufferTicks * TickSize;
			double stop		= direction == 1 ? sweepExtreme - buffer : sweepExtreme + buffer;
			double risk		= Math.Abs(entry - stop);

			if (risk <= 0)
			{
				FinishSession("size_zero");
				return false;
			}

			double target	= ResolveTarget(entry, risk);
			double reward	= Math.Abs(target - entry);

			double pointValue	= PointValueOverride > 0
				? PointValueOverride
				: Instrument.MasterInstrument.PointValue;
			int quantity		= (int)Math.Floor(RiskDollars / (risk * pointValue));
			if (MaxContracts > 0)
				quantity = Math.Min(quantity, MaxContracts);

			if (quantity < 1)
			{
				FinishSession("size_zero");
				return false;
			}

			if (MinimumRewardRisk > 0 && reward / risk < MinimumRewardRisk)
			{
				FinishSession("min_rr");
				return false;
			}

			// A sweep that keeps running is a breakdown, not a stop hunt. If price
			// never came back toward the level, the reversal premise is gone.
			if (MaxEntryDistanceFromLevel > 0)
			{
				double level		= direction == 1 ? rangeLow : rangeHigh;
				double beyondLevel	= direction == 1 ? level - entry : entry - level;
				double allowance	= (rangeHigh - rangeLow) * MaxEntryDistanceFromLevel;
				if (beyondLevel > allowance)
				{
					LogLine(string.Format(
						"rejected, entry {0} sits {1} beyond the level, allowance is {2}",
						Format(entry), Format(beyondLevel), Format(allowance)));
					FinishSession("entry_too_far");
					return false;
				}
			}

			// A close far beyond the gap it inverted is not a controlled reversal,
			// it is one candle that already made the move. Skip it, but leave the
			// session open: a later gap may still set up properly.
			if (MaxCloseDistancePastGap > 0)
			{
				double boundary	= inversion.Gap.Boundary;
				double past		= direction == 1 ? entry - boundary : boundary - entry;
				double room		= (rangeHigh - rangeLow) * MaxCloseDistancePastGap;
				if (past > room)
				{
					LogLine(string.Format(
						"skipped {0} inversion, close sits {1} past the gap, room is {2}",
						inversion.Slot.Label, Format(past), Format(room)));
					return false;
				}
			}

			if (entryTaken || Position.MarketPosition != MarketPosition.Flat)
			{
				FinishSession("position_open");
				return false;
			}

			stop	= Instrument.MasterInstrument.RoundToTickSize(stop);
			target	= Instrument.MasterInstrument.RoundToTickSize(target);

			beEntry		= entry;
			beRisk		= risk;
			beDirection	= direction;
			beDone		= false;

			entryTaken = true;
			tradesTaken++;
			armExtreme = sweepExtreme;
			FinishSession("traded");

			SetStopLoss(SignalName, CalculationMode.Price, stop, false);
			SetProfitTarget(SignalName, CalculationMode.Price, target);

			Print(string.Format("{0:yyyy-MM-dd} ENTRY SUBMITTED {1:HH:mm:ss} {2} {3} @ {4} stop {5}",
				sessionDate, closeTime, direction == 1 ? "long" : "short", quantity,
				Format(entry), Format(stop)));

			if (direction == 1)
				EnterLong(0, quantity, SignalName);
			else
				EnterShort(0, quantity, SignalName);

			ReportEntry(inversion, closeTime, entry, stop, target, quantity, risk, reward, pointValue);
			DrawTrade(inversion, closeTime, entry, stop, target);
			return true;
		}

		#endregion

		#region Reporting

		private void ReportEntry(Inversion inversion, DateTime closeTime, double entry, double stop,
			double target, int quantity, double risk, double reward, double pointValue)
		{
			DateTime sweptAt = direction == 1 ? lowSweptAt : highSweptAt;

			if (LogDetail)
			{
				Print("");
				Print(string.Format("{0:yyyy-MM-dd}  {1} {2} contract(s) on the {3}",
					sessionDate, direction == 1 ? "LONG" : "SHORT", quantity, inversion.Slot.Label));
				Print(string.Format("    range          {0} high / {1} low", Format(rangeHigh), Format(rangeLow)));
				Print(string.Format("    swept          {0} at {1:HH:mm:ss}",
					direction == 1 ? "the low" : "the high", sweptAt));
				Print(string.Format("    sweep extreme  {0}", Format(sweepExtreme)));
				Print(string.Format("    {0} gap        {1} to {2}, formed {3:HH:mm:ss}",
					inversion.Slot.Label, Format(inversion.Gap.Bottom), Format(inversion.Gap.Top),
					inversion.Gap.FormedAt));
				Print(string.Format("    confirmed      {0:HH:mm:ss} closing at {1}", closeTime, Format(entry)));
				Print(string.Format("    entry {0}   stop {1}   target {2}",
					Format(entry), Format(stop), Format(target)));
				Print(string.Format("    risk {0} pts (${1:F2})   reward {2} pts   {3:F2}R",
					Format(risk), risk * pointValue * quantity, Format(reward), reward / risk));
			}

			AppendCsv(inversion, closeTime, entry, stop, target, quantity, risk, reward, pointValue, sweptAt);
		}

		private void AppendCsv(Inversion inversion, DateTime closeTime, double entry, double stop,
			double target, int quantity, double risk, double reward, double pointValue, DateTime sweptAt)
		{
			if (string.IsNullOrWhiteSpace(ExportCsvPath))
				return;

			try
			{
				if (!File.Exists(ExportCsvPath))
					File.AppendAllText(ExportCsvPath,
						"session_date,direction,timeframe,swept_at,sweep_extreme,gap_bottom,gap_top," +
						"gap_formed_at,confirmed_at,entry,stop,target,contracts,risk_points," +
						"reward_points,rr,risk_usd,range_high,range_low" + Environment.NewLine);

				File.AppendAllText(ExportCsvPath, string.Format(
					"{0:yyyy-MM-dd},{1},{2},{3:HH:mm:ss},{4},{5},{6},{7:HH:mm:ss},{8:HH:mm:ss}," +
					"{9},{10},{11},{12},{13},{14},{15:F2},{16:F2},{17},{18}{19}",
					sessionDate, direction == 1 ? "long" : "short", inversion.Slot.Label, sweptAt,
					sweepExtreme, inversion.Gap.Bottom, inversion.Gap.Top, inversion.Gap.FormedAt,
					closeTime, entry, stop, target, quantity, risk, reward, reward / risk,
					risk * pointValue * quantity, rangeHigh, rangeLow, Environment.NewLine));
			}
			catch (Exception error)
			{
				Print("CSV export failed: " + error.Message);
			}
		}

		private void LogLine(string message)
		{
			if (LogDetail)
				Print(string.Format("{0:yyyy-MM-dd}  {1}", sessionDate, message));
		}

		private string Format(double price)
		{
			return price.ToString("F2");
		}

		private string SecondsLabel(int seconds)
		{
			return seconds % 60 == 0 ? (seconds / 60) + "m" : seconds + "s";
		}

		#endregion

		#region Drawing

		private void DrawSweepMarker(DateTime closeTime, double price)
		{
			if (!ShowDrawings || !DiagnosticMode)
				return;
			Draw.Text(this, "sweep" + closeTime.Ticks, false, "SWEEP", closeTime, price, -14,
				Brushes.Yellow, new SimpleFont("Arial", 10), System.Windows.TextAlignment.Center,
				Brushes.Transparent, Brushes.Transparent, 0);
		}

		private void DrawLevels(DateTime closeTime)
		{
			if (!ShowDrawings || !rangeValid)
				return;

			string stamp		= sessionDate.ToString("yyyyMMdd");
			DateTime windowOpen	= sessionDate.AddSeconds(rangeEndSec);
			DateTime windowEnd	= sessionDate.AddSeconds(tradeEndSec);

			Draw.Line(this, "asiaHigh" + stamp, false, windowOpen, rangeHigh, windowEnd, rangeHigh,
				Brushes.Goldenrod, DashStyleHelper.Dash, 2);
			Draw.Line(this, "asiaLow" + stamp, false, windowOpen, rangeLow, windowEnd, rangeLow,
				Brushes.IndianRed, DashStyleHelper.Dash, 2);
		}

		private void DrawTrade(Inversion inversion, DateTime closeTime, double entry, double stop, double target)
		{
			if (!ShowDrawings)
				return;

			string stamp	= sessionDate.ToString("yyyyMMdd") + "_" + tradesTaken;
			DateTime start	= inversion.Gap.FormedAt;
			DateTime finish	= closeTime.AddMinutes(45);

			Draw.Rectangle(this, "asiaGap" + stamp, false, start, inversion.Gap.Bottom, closeTime,
				inversion.Gap.Top, Brushes.DimGray, Brushes.SlateGray, 30);
			Draw.Line(this, "asiaEntry" + stamp, false, closeTime, entry, finish, entry,
				Brushes.White, DashStyleHelper.Solid, 2);
			Draw.Line(this, "asiaStop" + stamp, false, closeTime, stop, finish, stop,
				Brushes.Firebrick, DashStyleHelper.Solid, 2);
			Draw.Line(this, "asiaTarget" + stamp, false, closeTime, target, finish, target,
				Brushes.SeaGreen, DashStyleHelper.Solid, 2);
			Draw.Text(this, "asiaLabel" + stamp, false, inversion.Slot.Label + " IFVG", closeTime, entry, 12,
				Brushes.White, new SimpleFont("Arial", 11), System.Windows.TextAlignment.Left,
				Brushes.Transparent, Brushes.Transparent, 0);
		}

		#endregion

		#region Properties

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Risk per trade ($)", Order = 1, GroupName = "1. Risk")]
		public double RiskDollars { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Max contracts (0 = no cap)", Order = 2, GroupName = "1. Risk")]
		public int MaxContracts { get; set; }

		// MNQ only has history back to May 2019. Backtesting on NQ, which runs to
		// 1999 with the same prices at ten times the multiplier, needs sizing to
		// use MNQ's $2 point value rather than NQ's $20.
		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Point value override (0 = instrument)", Order = 4, GroupName = "1. Risk")]
		public double PointValueOverride { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Minimum reward:risk (0 = off)", Order = 3, GroupName = "1. Risk")]
		public double MinimumRewardRisk { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Stop buffer (ticks past the extreme)", Order = 5, GroupName = "1. Risk")]
		public int StopBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Break even at R multiple (0 = off)", Order = 6, GroupName = "1. Risk")]
		public double BreakEvenAtR { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Max sweep depth past level, points (0 = off)", Order = 10,
			GroupName = "2. Entry")]
		public double MaxSweepDepthPoints { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Max close distance past gap, x range (0 = off)", Order = 11,
			GroupName = "2. Entry")]
		public double MaxCloseDistancePastGap { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Max candles to invert (0 = off)", Order = 7, GroupName = "2. Entry")]
		public int MaxBarsToInvert { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Max entry distance past level, x range (0 = off)", Order = 8,
			GroupName = "2. Entry")]
		public double MaxEntryDistanceFromLevel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Flatten before next session", Order = 4, GroupName = "3. Session")]
		public bool FlattenBeforeNextSession { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Entry model", Order = 1, GroupName = "2. Entry")]
		public AsiaIfvgEntryMode EntryMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Max trades per session", Order = 9, GroupName = "2. Entry")]
		public int MaxTradesPerSession { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Target mode", Order = 1, GroupName = "6. Target")]
		public AsiaIfvgTargetMode TargetMode { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 100)]
		[Display(Name = "Target R multiple (FixedRMultiple)", Order = 2, GroupName = "6. Target")]
		public double TargetRMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, 10000)]
		[Display(Name = "Target points (FixedPoints)", Order = 3, GroupName = "6. Target")]
		public double TargetPoints { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use 30 second", Order = 2, GroupName = "2. Entry")]
		public bool Use30Second { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use 1 minute", Order = 3, GroupName = "2. Entry")]
		public bool Use1Minute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use 2 minute", Order = 4, GroupName = "2. Entry")]
		public bool Use2Minute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use 3 minute", Order = 5, GroupName = "2. Entry")]
		public bool Use3Minute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use 5 minute", Order = 6, GroupName = "2. Entry")]
		public bool Use5Minute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Range start (HHmm, chart time)", Order = 1, GroupName = "3. Session")]
		public int RangeStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Range end (HHmm, chart time)", Order = 2, GroupName = "3. Session")]
		public int RangeEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Last entry (HHmm, chart time)", Order = 3, GroupName = "3. Session")]
		public int TradeEndTime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Sunday", Order = 1, GroupName = "4. Days")]
		public bool TradeSunday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Monday", Order = 2, GroupName = "4. Days")]
		public bool TradeMonday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Tuesday", Order = 3, GroupName = "4. Days")]
		public bool TradeTuesday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Wednesday", Order = 4, GroupName = "4. Days")]
		public bool TradeWednesday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Thursday", Order = 5, GroupName = "4. Days")]
		public bool TradeThursday { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Draw levels and gaps", Order = 1, GroupName = "5. Output")]
		public bool ShowDrawings { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Log to output window", Order = 2, GroupName = "5. Output")]
		public bool LogDetail { get; set; }

		// Verification aid. Never leave this on for a multi-year run.
		[NinjaScriptProperty]
		[Display(Name = "Diagnostic mode (draw every gap and sweep)", Order = 4,
			GroupName = "5. Output")]
		public bool DiagnosticMode { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Export CSV path (blank = off)", Order = 3, GroupName = "5. Output")]
		public string ExportCsvPath { get; set; }

		#endregion
	}
}
