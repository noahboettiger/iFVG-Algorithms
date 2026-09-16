// iFVG Model - liquidity level registry
//
// Step one of the generalised inverse fair value gap model specified in
// SPEC-INVERSION.md. This version marks and logs liquidity levels only and
// places no orders. A level the code gets wrong invalidates every trade that
// would reference it, so the registry is verified against a hand markup before
// entries are built on top of it.
//
// A level is live until price trades through it; after that it is mitigated,
// its line stops extending, and it is no longer a sweep or target candidate.
//
// Gaps are not levels and do not share that lifecycle. They answer two separate
// questions with two separate rules: consequent encroachment decides whether a
// gap is still a draw worth targeting, and first touch plus a confirm delay
// decides whether it is still a virgin PDA worth delivering from. Session
// windows and the gap rules follow iFVG Ultimate (DodgysDD); see the cross-check
// section of SPEC-INVERSION.md for what was adopted from it and what was not.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
	public enum IfvgLevelSource
	{
		PreviousDay,
		PreviousWeek,
		Daily,
		FourHour,
		OneHour,
		FifteenMinute,
		Asia,
		London,
		NewYorkAm,
		NewYorkLunch,
		NewYorkPm,
		EqualHighLow
	}

	/// <summary>
	/// A gap is still valid while price only taps it; each tap drains it a little.
	/// Consequent encroachment, the 50% midpoint, is the line. Trading to CE
	/// without a close past the far edge leaves the gap mitigated and dead.
	/// A close past the far edge inverts it instead, which is a role change rather
	/// than a death, and inversion wins when one bar does both.
	/// </summary>
	public enum IfvgGapState
	{
		Unfilled,
		Tapped,
		Mitigated,
		Inverted
	}

	/// <summary>
	/// Which gaps get drawn, by their own timeframe against the chart's. Detection
	/// is unaffected: a hidden gap is still registered, still tracked and still a
	/// target. This only decides what is worth putting on the screen.
	/// </summary>
	public enum IfvgGapVisibility
	{
		All,
		ChartAndHigher,
		ChartOnly
	}

	/// <summary>Minimum gap size as a multiple of ATR on the gap's own timeframe.</summary>
	public enum IfvgGapSensitivity
	{
		Off,
		Sensitive,
		Normal,
		Strict
	}

	/// <summary>
	/// Single treats each three candle gap on its own. Series merges adjacent and
	/// overlapping gaps of the same direction into one zone, so an inversion has
	/// to close through the whole cluster rather than through one slice of it.
	/// </summary>
	public enum IfvgGapDetection
	{
		Single,
		Series
	}

	public class IfvgModel : Strategy
	{
		#region Nested state

		private class Zone
		{
			public double			Top;
			public double			Bottom;
			public DateTime			CreatedAt;
			public DateTime			StartsAt;
			public DateTime			ClosedAt;
			public IfvgLevelSource	Source;
			public int				Rank;
			public int				Minutes;
			public bool				Bearish;
			public IfvgGapState		State;
			public int				Taps;
			public bool				Inside;
			public DateTime			FirstTouchAt;
			public string			Tag;

			public double Height { get { return Top - Bottom; } }

			/// <summary>Consequent encroachment: the 50% midpoint.</summary>
			public double Ce { get { return (Top + Bottom) / 2; } }

			/// <summary>The edge a close must pass to invert the zone.</summary>
			public double FarEdge { get { return Bearish ? Top : Bottom; } }

			/// <summary>
			/// Is there still a draw left here to target? Consequent encroachment
			/// answers this: half the imbalance rebalanced and the gap is spent. An
			/// inverted gap is an entry object on the low timeframes, not a target.
			/// </summary>
			public bool IsDrawOnLiquidity
			{
				get { return State == IfvgGapState.Unfilled || State == IfvgGapState.Tapped; }
			}

			/// <summary>
			/// Is this a virgin PDA we are delivering from? A different question, so
			/// a different rule: the gap stays virgin until price has been inside it
			/// for longer than the confirm delay. The delay is the point. An entry
			/// forming off a fresh tap is delivery from an untouched gap, and a rule
			/// that killed the gap on contact would reject the very setup it is
			/// meant to find.
			/// </summary>
			public bool IsVirginPda(DateTime now, int confirmBars)
			{
				if (FirstTouchAt == DateTime.MinValue)
					return true;
				return (now - FirstTouchAt).TotalMinutes <= (double)confirmBars * Minutes;
			}
		}

		private class Level
		{
			public double			Price;
			public DateTime			CreatedAt;
			public IfvgLevelSource	Source;
			public string			Tag;
			public bool				IsHigh;
			public int				Rank;
			public bool				Mitigated;
			public DateTime			MitigatedAt;
		}

		/// <summary>A monitored timeframe, with the rolling window used to find pivots.</summary>
		private class PivotSeries
		{
			public int				Minutes;
			public int				Bip;
			public IfvgLevelSource	Source;
			public int				Rank;
			public bool				Enabled;		// swing levels from this series
			public bool				GapsEnabled;	// gaps from this series
			public bool				IsDaily;
			public List<double>		Highs = new List<double>();
			public List<double>		Lows = new List<double>();
			public List<double>		Closes = new List<double>();
			public List<DateTime>	Times = new List<DateTime>();
		}

		/// <summary>A killzone whose extremes become levels once the window closes.</summary>
		private class Killzone
		{
			public string			Name;
			public IfvgLevelSource	Source;
			public int				StartSec;
			public int				EndSec;
			public bool				Enabled;
			public double			High;
			public double			Low;
			public bool				Building;
		}

		#endregion

		private List<Level>			levels;
		private List<Zone>			zones;
		private List<PivotSeries>	pivots;
		private List<Killzone>		killzones;
		private int					fineBip		= -1;
		private int					dailyBip	= -1;
		private int					weeklyBip	= -1;
		private int					chartMinutes;

		#region Lifecycle

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description		= "Liquidity level registry for the iFVG model. Marks levels, places no orders.";
				Name			= "IfvgModel";
				Calculate		= Calculate.OnBarClose;
				IsExitOnSessionCloseStrategy = false;
				BarsRequiredToTrade = 20;

				SwingStrength		= 3;
				LevelLookbackDays	= 5;
				KeepMitigated		= false;

				UsePreviousDay		= true;
				UsePreviousWeek		= true;
				UseFourHour			= true;
				UseOneHour			= true;
				UseFifteenMinute	= true;
				UseAsia				= true;
				UseLondon			= true;
				UseNewYorkAm		= true;
				UseNewYorkLunch		= false;
				UseNewYorkPm		= false;
				UseEqualHighLow		= false;
				EqualToleranceTicks	= 5;

				// The timeframe is the filter, not the size. 15m and up only.
				UseFifteenMinuteGaps	= true;
				UseOneHourGaps			= true;
				UseFourHourGaps			= true;
				UseDailyGaps			= true;
				GapVisibility			= IfvgGapVisibility.ChartAndHigher;
				ShowGapLabels			= false;	// the labels were most of the mess
				GapExtendBars			= 12;
				GapLookbackDays			= 30;
				GapSensitivity			= IfvgGapSensitivity.Normal;
				GapDetection			= IfvgGapDetection.Single;
				AtrPeriod				= 14;
				MitigationConfirmBars	= 15;	// from the indicator
				MinGapPoints			= 0;	// the ATR floor is the real filter

				// Defaults taken from the ICT Killzones & Pivots indicator.
				AsiaWindow			= "2000-0000";
				LondonWindow		= "0200-0500";
				NewYorkAmWindow		= "0900-1130";
				NewYorkLunchWindow	= "1200-1300";
				NewYorkPmWindow		= "1300-1600";

				ShowLevels			= true;
				ShowLevelLabels		= true;
				LogLevels			= true;
			}
			else if (State == State.Configure)
			{
				AddDataSeries(BarsPeriodType.Minute, 1);
				AddDataSeries(BarsPeriodType.Minute, 15);
				AddDataSeries(BarsPeriodType.Minute, 60);
				AddDataSeries(BarsPeriodType.Minute, 240);
				AddDataSeries(BarsPeriodType.Day, 1);
				AddDataSeries(BarsPeriodType.Week, 1);
			}
			else if (State == State.DataLoaded)
			{
				levels = new List<Level>();
				zones = new List<Zone>();
				// 15m and up only. Below that a gap is an entry object on the low
				// timeframe ladder, not a liquidity target, so it has no place here.
				pivots = new List<PivotSeries>
				{
					new PivotSeries { Minutes = 15,   Source = IfvgLevelSource.FifteenMinute, Rank = 2,
						Enabled = UseFifteenMinute, GapsEnabled = UseFifteenMinuteGaps },
					new PivotSeries { Minutes = 60,   Source = IfvgLevelSource.OneHour,       Rank = 3,
						Enabled = UseOneHour,       GapsEnabled = UseOneHourGaps },
					new PivotSeries { Minutes = 240,  Source = IfvgLevelSource.FourHour,      Rank = 4,
						Enabled = UseFourHour,      GapsEnabled = UseFourHourGaps },
					new PivotSeries { Minutes = 1440, Source = IfvgLevelSource.Daily,         Rank = 6,
						Enabled = false,            GapsEnabled = UseDailyGaps, IsDaily = true },
				};
				killzones = new List<Killzone>
				{
					MakeKillzone("Asia",      IfvgLevelSource.Asia,         AsiaWindow,          UseAsia),
					MakeKillzone("London",    IfvgLevelSource.London,       LondonWindow,        UseLondon),
					MakeKillzone("NY AM",     IfvgLevelSource.NewYorkAm,    NewYorkAmWindow,     UseNewYorkAm),
					MakeKillzone("NY Lunch",  IfvgLevelSource.NewYorkLunch, NewYorkLunchWindow,  UseNewYorkLunch),
					MakeKillzone("NY PM",     IfvgLevelSource.NewYorkPm,    NewYorkPmWindow,     UseNewYorkPm),
				};
				ResolveSeriesIndexes();
				chartMinutes = PeriodMinutes(BarsArray[0]);
			}
		}

		/// <summary>
		/// The chart series in minutes, for the drawing filter. Anything that does
		/// not map onto a duration (tick, volume, range) returns 0, which shows
		/// every gap rather than guessing.
		/// </summary>
		private static int PeriodMinutes(Bars bars)
		{
			if (bars == null)
				return 0;
			BarsPeriod bp = bars.BarsPeriod;
			switch (bp.BarsPeriodType)
			{
				case BarsPeriodType.Minute:	return bp.Value;
				case BarsPeriodType.Day:	return bp.Value * 1440;
				case BarsPeriodType.Week:	return bp.Value * 1440 * 7;
				case BarsPeriodType.Month:	return bp.Value * 1440 * 30;
				case BarsPeriodType.Second:	return bp.Value / 60;
				default:					return 0;
			}
		}

		/// <summary>
		/// A 15m gap on a daily chart is invisible clutter: too small to see, and
		/// there are hundreds of them. Each hidden gap is also two fewer draw
		/// objects, which is where the lag on a higher timeframe chart comes from.
		/// </summary>
		private bool IsVisible(Zone zone)
		{
			if (chartMinutes <= 0 || GapVisibility == IfvgGapVisibility.All)
				return true;
			return GapVisibility == IfvgGapVisibility.ChartOnly
				? zone.Minutes == chartMinutes
				: zone.Minutes >= chartMinutes;
		}

		private Killzone MakeKillzone(string name, IfvgLevelSource source, string window, bool enabled)
		{
			int dash	= window.IndexOf('-');
			int start	= int.Parse(window.Substring(0, dash));
			int end		= int.Parse(window.Substring(dash + 1));
			return new Killzone
			{
				Name		= name,
				Source		= source,
				StartSec	= ToSeconds(start),
				EndSec		= ToSeconds(end),
				Enabled		= enabled,
			};
		}

		/// <summary>
		/// Find each series by its loaded period rather than assuming the order in
		/// which AddDataSeries handed out indexes, which shifts when the primary
		/// series happens to match one of them.
		/// </summary>
		private void ResolveSeriesIndexes()
		{
			fineBip = dailyBip = weeklyBip = -1;
			for (int i = 0; i < BarsArray.Length; i++)
			{
				if (BarsArray[i] == null)
					continue;
				BarsPeriod bp = BarsArray[i].BarsPeriod;
				if (bp.BarsPeriodType == BarsPeriodType.Day && dailyBip < 0)
					dailyBip = i;
				else if (bp.BarsPeriodType == BarsPeriodType.Week && weeklyBip < 0)
					weeklyBip = i;
				else if (bp.BarsPeriodType == BarsPeriodType.Minute && bp.Value == 1 && fineBip < 0)
					fineBip = i;
			}

			foreach (PivotSeries each in pivots)
			{
				each.Bip = -1;
				for (int i = 0; i < BarsArray.Length; i++)
				{
					if (BarsArray[i] == null)
						continue;
					BarsPeriod bp = BarsArray[i].BarsPeriod;
					bool match = each.IsDaily
						? bp.BarsPeriodType == BarsPeriodType.Day
						: bp.BarsPeriodType == BarsPeriodType.Minute && bp.Value == each.Minutes;
					if (match)
					{
						each.Bip = i;
						break;
					}
				}
			}

			Print("");
			Print("IfvgModel level registry loaded:");
			Print(string.Format("    1m   -> {0}", Describe(fineBip)));
			foreach (PivotSeries each in pivots)
				Print(string.Format("    {0,-4} -> {1}{2}", each.Minutes + "m", Describe(each.Bip),
					each.Enabled ? "" : "  (off)"));
			Print(string.Format("    day  -> {0}", Describe(dailyBip)));
			foreach (Killzone kz in killzones)
				Print(string.Format("    {0,-9} {1:00}:{2:00} to {3:00}:{4:00}{5}", kz.Name,
					kz.StartSec / 3600, kz.StartSec % 3600 / 60,
					kz.EndSec / 3600, kz.EndSec % 3600 / 60,
					kz.Enabled ? "" : "  (off)"));
			Print("");
		}

		private string Describe(int bip)
		{
			return bip < 0 ? "NOT LOADED" : "series " + bip;
		}

		private static int ToSeconds(int hhmm)
		{
			return (hhmm / 100) * 3600 + (hhmm % 100) * 60;
		}

		#endregion

		#region Bar handling

		protected override void OnBarUpdate()
		{
			if (levels == null)
				return;

			if (BarsInProgress == dailyBip)
				RegisterPreviousDay();

			if (BarsInProgress == weeklyBip)
			{
				RegisterPreviousWeek();
				return;
			}

			foreach (PivotSeries each in pivots)
				if (each.Bip == BarsInProgress)
				{
					TrackSeries(each);
					return;
				}

			if (BarsInProgress == fineBip)
			{
				TrackKillzones();
				MarkMitigations();
			}
		}

		private void RegisterPreviousDay()
		{
			if (!UsePreviousDay || CurrentBars[dailyBip] < 1)
				return;

			DateTime stamp = Times[dailyBip][0];
			AddLevel(Highs[dailyBip][0], stamp, IfvgLevelSource.PreviousDay, true, 5);
			AddLevel(Lows[dailyBip][0], stamp, IfvgLevelSource.PreviousDay, false, 5);
		}

		private void RegisterPreviousWeek()
		{
			if (!UsePreviousWeek || weeklyBip < 0 || CurrentBars[weeklyBip] < 1)
				return;

			DateTime stamp = Times[weeklyBip][0];
			AddLevel(Highs[weeklyBip][0], stamp, IfvgLevelSource.PreviousWeek, true, 6);
			AddLevel(Lows[weeklyBip][0], stamp, IfvgLevelSource.PreviousWeek, false, 6);
		}

		/// <summary>
		/// A pivot is confirmed once SwingStrength bars have closed either side of
		/// it, so it registers with the timestamp of the pivot bar rather than the
		/// bar that confirmed it.
		/// </summary>
		private void TrackSeries(PivotSeries each)
		{
			if (each.Bip < 0 || (!each.Enabled && !each.GapsEnabled))
				return;

			each.Highs.Add(Highs[each.Bip][0]);
			each.Lows.Add(Lows[each.Bip][0]);
			each.Closes.Add(Closes[each.Bip][0]);
			each.Times.Add(Times[each.Bip][0]);

			int window = SwingStrength * 2 + 1;
			if (each.Highs.Count > Math.Max(window, 60))
			{
				each.Highs.RemoveAt(0);
				each.Lows.RemoveAt(0);
				each.Closes.RemoveAt(0);
				each.Times.RemoveAt(0);
			}
			// Gap work runs on every closed bar; pivots need the confirmation window.
			DetectGap(each);
			ResolveGapStates(each);

			if (!each.Enabled || each.Highs.Count < window)
				return;

			int pivot = each.Highs.Count - 1 - SwingStrength;
			bool isHigh = true, isLow = true;
			for (int i = pivot - SwingStrength; i <= pivot + SwingStrength; i++)
			{
				if (i == pivot)
					continue;
				if (each.Highs[i] >= each.Highs[pivot]) isHigh = false;
				if (each.Lows[i] <= each.Lows[pivot])   isLow = false;
			}

			if (isHigh)
				AddLevel(each.Highs[pivot], each.Times[pivot], each.Source, true, each.Rank);
			if (isLow)
				AddLevel(each.Lows[pivot], each.Times[pivot], each.Source, false, each.Rank);
		}

		/// <summary>
		/// The standard three candle gap, measured on the most recent completed
		/// bars of this timeframe. What makes a gap worth marking is the timeframe
		/// it printed on, not its size, so the series list is the filter and the
		/// size floors below are off by default.
		/// </summary>
		/// <summary>
		/// Average true range over the series' own last AtrPeriod bars, computed
		/// from the buffers already kept rather than a per series indicator
		/// instance. Returns 0 when there is not enough history, which disables the
		/// size filter rather than rejecting everything.
		/// </summary>
		private double SeriesAtr(PivotSeries each)
		{
			int n = each.Highs.Count;
			if (n < AtrPeriod + 1)
				return 0;

			double sum = 0;
			for (int i = n - AtrPeriod; i < n; i++)
			{
				double prevClose = each.Closes[i - 1];
				double tr = Math.Max(each.Highs[i] - each.Lows[i],
					Math.Max(Math.Abs(each.Highs[i] - prevClose),
						Math.Abs(prevClose - each.Lows[i])));
				sum += tr;
			}
			return sum / AtrPeriod;
		}

		private double SensitivityMultiple()
		{
			switch (GapSensitivity)
			{
				case IfvgGapSensitivity.Sensitive:	return 0.15;
				case IfvgGapSensitivity.Normal:		return 0.35;
				case IfvgGapSensitivity.Strict:		return 0.75;
				default:							return 0;
			}
		}

		private void DetectGap(PivotSeries each)
		{
			if (!each.GapsEnabled || each.Highs.Count < 3)
				return;

			int last = each.Highs.Count - 1;
			double c1High = each.Highs[last - 2], c1Low = each.Lows[last - 2];
			double c3High = each.Highs[last],     c3Low = each.Lows[last];

			// The gap only becomes known when c3 closes, but it belongs on the
			// chart where it opened. c1's close time is c2's open, which is where
			// the killzone indicator and a hand markup both start the box.
			DateTime created = each.Times[last];
			DateTime startsAt = each.Times[last - 2];

			if (c3High < c1Low)
				AddZone(c3High, c1Low, created, startsAt, each, true);
			else if (c3Low > c1High)
				AddZone(c1High, c3Low, created, startsAt, each, false);
		}

		/// <summary>
		/// Inversion is a close past the far edge on the gap's own timeframe. It is
		/// checked even on an already mitigated zone, because a bar that reaches CE
		/// and then closes right through the gap inverted it rather than spent it,
		/// and the state set by the fine series earlier in that bar was provisional.
		/// </summary>
		private void ResolveGapStates(PivotSeries each)
		{
			double close = Closes[each.Bip][0];
			DateTime stamp = Times[each.Bip][0];

			foreach (Zone zone in zones)
			{
				if (zone.Source != each.Source || zone.State == IfvgGapState.Inverted)
					continue;
				bool through = zone.Bearish ? close > zone.Top : close < zone.Bottom;
				if (!through)
					continue;

				zone.State		= IfvgGapState.Inverted;
				zone.ClosedAt	= stamp;
				if (LogLevels)
					Print(string.Format("{0:yyyy-MM-dd HH:mm}  INVERTED {1} {2} gap {3} to {4}",
						stamp, Describe(zone.Source), zone.Bearish ? "bearish" : "bullish",
						Format(zone.Bottom), Format(zone.Top)));
				DrawZone(zone);
			}
		}

		/// <summary>
		/// Killzone extremes accumulate while the window is open and register as
		/// levels the moment it closes, matching how the indicator draws them.
		/// </summary>
		private void TrackKillzones()
		{
			DateTime stamp	= Times[fineBip][0];
			int tod			= stamp.Hour * 3600 + stamp.Minute * 60 + stamp.Second;
			double high		= Highs[fineBip][0];
			double low		= Lows[fineBip][0];

			foreach (Killzone kz in killzones)
			{
				bool inside = InWindow(tod, kz.StartSec, kz.EndSec);

				if (inside)
				{
					kz.High		= kz.Building ? Math.Max(kz.High, high) : high;
					kz.Low		= kz.Building ? Math.Min(kz.Low, low)   : low;
					kz.Building	= true;
				}
				else if (kz.Building)
				{
					kz.Building = false;
					if (kz.Enabled)
					{
						AddLevel(kz.High, stamp, kz.Source, true, 2);
						AddLevel(kz.Low, stamp, kz.Source, false, 2);
					}
				}
			}
		}

		/// <summary>Windows that cross midnight wrap, so the test flips accordingly.</summary>
		private static bool InWindow(int tod, int start, int end)
		{
			return start <= end
				? tod >= start && tod < end
				: tod >= start || tod < end;
		}

		/// <summary>
		/// Once price trades through a level the pool behind it is gone. The level
		/// stops extending and drops out of the candidate set entirely: it can no
		/// longer arm a setup, and it can no longer be a target.
		/// </summary>
		private void MarkMitigations()
		{
			DateTime stamp	= Times[fineBip][0];
			double high		= Highs[fineBip][0];
			double low		= Lows[fineBip][0];

			foreach (Level level in levels)
			{
				if (level.Mitigated)
					continue;
				bool taken = level.IsHigh ? high > level.Price : low < level.Price;
				if (!taken)
					continue;

				level.Mitigated		= true;
				level.MitigatedAt	= stamp;

				if (LogLevels)
					Print(string.Format("{0:yyyy-MM-dd HH:mm}  MITIGATED {1} {2} at {3}",
						stamp, Describe(level.Source), level.IsHigh ? "high" : "low",
						Format(level.Price)));

				if (KeepMitigated)
					StopExtending(level);
				else
					Erase(level);
			}

			if (!KeepMitigated)
				levels.RemoveAll(delegate(Level l) { return l.Mitigated; });

			TrackGapTouches(stamp, high, low);
			Prune(stamp);
		}

		/// <summary>
		/// A tap is not a kill. Price entering the gap and leaving keeps it valid,
		/// just less so each time, which is what Taps records. Reaching consequent
		/// encroachment is the kill: half the imbalance is rebalanced and the gap
		/// stops being a draw. Only a close past the far edge, resolved on the
		/// gap's own timeframe, overrides that into an inversion.
		/// </summary>
		private void TrackGapTouches(DateTime stamp, double high, double low)
		{
			bool died = false;
			foreach (Zone zone in zones)
			{
				if (zone.State == IfvgGapState.Mitigated || zone.State == IfvgGapState.Inverted)
					continue;

				bool inside = high >= zone.Bottom && low <= zone.Top;
				if (inside && !zone.Inside)
					zone.Taps++;
				if (inside && zone.FirstTouchAt == DateTime.MinValue)
					zone.FirstTouchAt = stamp;
				zone.Inside = inside;

				bool reachedCe = zone.Bearish ? high >= zone.Ce : low <= zone.Ce;

				if (reachedCe)
				{
					zone.State		= IfvgGapState.Mitigated;
					zone.ClosedAt	= stamp;
					if (LogLevels)
						Print(string.Format("{0:yyyy-MM-dd HH:mm}  MITIGATED {1} {2} gap {3} to {4}  (CE {5}, {6} tap{7})",
							stamp, Describe(zone.Source), zone.Bearish ? "bearish" : "bullish",
							Format(zone.Bottom), Format(zone.Top), Format(zone.Ce),
							zone.Taps, zone.Taps == 1 ? "" : "s"));
					if (KeepMitigated)
					{
						DrawZone(zone);
					}
					else
					{
						EraseZone(zone);
						died = true;
					}
					continue;
				}

				if (inside && zone.State == IfvgGapState.Unfilled)
				{
					zone.State = IfvgGapState.Tapped;
					DrawZone(zone);
				}
			}

			// This loop runs on every one minute bar, so a dead zone left in the
			// list is paid for thousands of times over. A CE-dead gap still has to
			// survive its confirm delay though: it is no longer a target, but it is
			// still the virgin PDA an entry may be delivering from.
			if (died)
				zones.RemoveAll(delegate(Zone z)
				{
					return z.State == IfvgGapState.Mitigated
						&& !z.IsVirginPda(stamp, MitigationConfirmBars);
				});
		}

		#endregion

		#region Registry

		private void AddLevel(double price, DateTime created, IfvgLevelSource source,
			bool isHigh, int rank)
		{
			foreach (Level existing in levels)
				if (existing.Source == source && existing.IsHigh == isHigh
					&& Math.Abs(existing.Price - price) < TickSize / 2)
					return;

			Level level = new Level
			{
				Price		= price,
				CreatedAt	= created,
				Source		= source,
				IsHigh		= isHigh,
				Rank		= rank,
				Tag			= "lvl" + source + created.Ticks + (isHigh ? "H" : "L"),
			};
			levels.Add(level);

			if (LogLevels)
				Print(string.Format("{0:yyyy-MM-dd HH:mm}  {1} {2} at {3}",
					created, Describe(source), isHigh ? "high" : "low", Format(price)));

			DrawLevel(level, created.AddDays(LevelLookbackDays));
			if (UseEqualHighLow)
				ScanForEqualPairs(level);
		}

		/// <summary>
		/// Equal highs need the first to sit above the second, within tolerance. A
		/// second extreme that exceeds the first is a break, not an equal pair.
		/// </summary>
		private void ScanForEqualPairs(Level latest)
		{
			if (latest.Source == IfvgLevelSource.EqualHighLow)
				return;

			double tolerance = EqualToleranceTicks * TickSize;
			foreach (Level prior in levels)
			{
				if (prior == latest || prior.IsHigh != latest.IsHigh || prior.Mitigated)
					continue;
				if (prior.CreatedAt >= latest.CreatedAt)
					continue;
				if (Math.Abs(prior.Price - latest.Price) > tolerance)
					continue;

				bool ordered = latest.IsHigh
					? prior.Price > latest.Price
					: prior.Price < latest.Price;
				if (!ordered)
					continue;

				AddLevel(prior.Price, latest.CreatedAt, IfvgLevelSource.EqualHighLow,
					latest.IsHigh, 1);
				return;
			}
		}

		private void AddZone(double bottom, double top, DateTime created,
			DateTime startsAt, PivotSeries series, bool bearish)
		{
			double height = top - bottom;
			if (MinGapPoints > 0 && height < MinGapPoints)
				return;

			// Size in ATR, not points or percent. A points threshold right at
			// 29,000 is wrong at 7,000, and a percentage ignores how much the
			// instrument is actually moving.
			double atr = SeriesAtr(series);
			double floor = atr * SensitivityMultiple();
			if (floor > 0 && height < floor)
				return;

			foreach (Zone existing in zones)
				if (existing.Source == series.Source
					&& Math.Abs(existing.Top - top) < TickSize / 2
					&& Math.Abs(existing.Bottom - bottom) < TickSize / 2)
					return;

			// In series mode a fresh gap that touches a live one of the same
			// direction absorbs into it, so the inversion has to close through the
			// whole cluster rather than through one slice.
			if (GapDetection == IfvgGapDetection.Series)
				foreach (Zone existing in zones)
				{
					if (existing.Source != series.Source || existing.Bearish != bearish
						|| existing.State != IfvgGapState.Unfilled)
						continue;
					if (top < existing.Bottom || bottom > existing.Top)
						continue;

					existing.Top	= Math.Max(existing.Top, top);
					existing.Bottom	= Math.Min(existing.Bottom, bottom);
					existing.CreatedAt = created;
					DrawZone(existing);
					return;
				}

			Zone zone = new Zone
			{
				Top			= top,
				Bottom		= bottom,
				CreatedAt	= created,
				StartsAt	= startsAt,
				Source		= series.Source,
				Rank		= series.Rank,
				Minutes		= series.Minutes,
				Bearish		= bearish,
				State		= IfvgGapState.Unfilled,
				Tag			= "gap" + series.Source + created.Ticks + (bearish ? "B" : "U"),
			};
			zones.Add(zone);

			if (LogLevels)
				Print(string.Format("{0:yyyy-MM-dd HH:mm}  {1} {2} gap {3} to {4}  ({5} pts, CE {6})",
					created, Describe(series.Source), bearish ? "bearish" : "bullish",
					Format(bottom), Format(top), Format(height), Format(zone.Ce)));

			DrawZone(zone);
		}

		private void Prune(DateTime now)
		{
			DateTime cutoff = now.AddDays(-LevelLookbackDays);
			List<Level> stale = levels.FindAll(
				delegate(Level l) { return l.CreatedAt < cutoff; });
			foreach (Level each in stale)
				Erase(each);
			levels.RemoveAll(delegate(Level l) { return l.CreatedAt < cutoff; });

			DateTime gapCutoff = now.AddDays(-GapLookbackDays);
			List<Zone> expired = zones.FindAll(
				delegate(Zone z) { return z.CreatedAt < gapCutoff; });
			foreach (Zone each in expired)
				EraseZone(each);
			zones.RemoveAll(delegate(Zone z) { return z.CreatedAt < gapCutoff; });
		}

		#endregion

		#region Drawing

		private void DrawLevel(Level level, DateTime finish)
		{
			if (!ShowLevels)
				return;

			Brush brush = level.Rank >= 5 ? Brushes.Gold
				: level.Rank == 4 ? Brushes.OrangeRed
				: level.Rank == 3 ? Brushes.DeepSkyBlue
				: level.Rank == 2 ? Brushes.MediumPurple
				: Brushes.Gray;

			NinjaTrader.NinjaScript.DrawingTools.Draw.Line(this, level.Tag, false, level.CreatedAt, level.Price, finish, level.Price,
				brush, level.Rank >= 4 ? DashStyleHelper.Solid : DashStyleHelper.Dot,
				level.Rank >= 5 ? 2 : 1);

			if (!ShowLevelLabels)
				return;
			NinjaTrader.NinjaScript.DrawingTools.Draw.Text(this, level.Tag + "T", false, Describe(level.Source),
				level.CreatedAt, level.Price, level.IsHigh ? 6 : -6, brush,
				new SimpleFont("Arial", 9), System.Windows.TextAlignment.Left,
				Brushes.Transparent, Brushes.Transparent, 0);
		}

		/// <summary>
		/// A short box, not a band running to the right edge. These are numerous
		/// enough that extending them all turns the chart into noise, and the box
		/// only has to say where the gap is. It stops early if the gap died before
		/// GapExtendBars elapsed.
		/// </summary>
		private void DrawZone(Zone zone)
		{
			if (!ShowLevels || !IsVisible(zone))
				return;

			Brush edge = zone.State == IfvgGapState.Inverted
				? (zone.Bearish ? Brushes.LimeGreen : Brushes.Crimson)
				: zone.State == IfvgGapState.Mitigated
					? Brushes.DimGray
					: (zone.Bearish ? Brushes.IndianRed : Brushes.CornflowerBlue);
			int opacity = zone.State == IfvgGapState.Unfilled ? 25
				: zone.State == IfvgGapState.Tapped ? 15
				: zone.State == IfvgGapState.Mitigated ? 8 : 35;

			DateTime cap = zone.StartsAt.AddMinutes((double)zone.Minutes * GapExtendBars);
			DateTime finish = zone.ClosedAt > zone.StartsAt && zone.ClosedAt < cap
				? zone.ClosedAt : cap;

			NinjaTrader.NinjaScript.DrawingTools.Draw.Rectangle(this, zone.Tag, false,
				zone.StartsAt, zone.Bottom, finish, zone.Top,
				Brushes.Transparent, edge, opacity);

			if (!ShowGapLabels)
				return;
			NinjaTrader.NinjaScript.DrawingTools.Draw.Text(this, zone.Tag + "T", false,
				Describe(zone.Source) + (zone.State == IfvgGapState.Inverted ? " iFVG" : " FVG"),
				zone.StartsAt, zone.Top, 4, edge, new SimpleFont("Arial", 9),
				System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
		}

		private void EraseZone(Zone zone)
		{
			if (!ShowLevels)
				return;
			RemoveDrawObject(zone.Tag);
			RemoveDrawObject(zone.Tag + "T");
		}

		private void StopExtending(Level level)
		{
			DrawLevel(level, level.MitigatedAt);
		}

		private void Erase(Level level)
		{
			if (!ShowLevels)
				return;
			RemoveDrawObject(level.Tag);
			RemoveDrawObject(level.Tag + "T");
		}

		private static string Describe(IfvgLevelSource source)
		{
			switch (source)
			{
				case IfvgLevelSource.PreviousDay:	return "PD";
				case IfvgLevelSource.PreviousWeek:	return "PW";
				case IfvgLevelSource.Daily:			return "1D";
				case IfvgLevelSource.FourHour:		return "4H";
				case IfvgLevelSource.OneHour:		return "1H";
				case IfvgLevelSource.FifteenMinute:	return "15m";
				case IfvgLevelSource.Asia:			return "AS";
				case IfvgLevelSource.London:		return "LO";
				case IfvgLevelSource.NewYorkAm:		return "NYAM";
				case IfvgLevelSource.NewYorkLunch:	return "NYL";
				case IfvgLevelSource.NewYorkPm:		return "NYPM";
				default:							return "EQ";
			}
		}

		private string Format(double price)
		{
			return price.ToString("F2");
		}

		#endregion

		#region Properties

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Swing strength (bars each side)", Order = 1, GroupName = "1. Levels")]
		public int SwingStrength { get; set; }

		[NinjaScriptProperty]
		[Range(1, 60)]
		[Display(Name = "Level lookback (days)", Order = 2, GroupName = "1. Levels")]
		public int LevelLookbackDays { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Keep mitigated levels on chart", Order = 3, GroupName = "1. Levels")]
		public bool KeepMitigated { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Previous day high/low", Order = 1, GroupName = "2. Sources")]
		public bool UsePreviousDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Previous week high/low", Order = 2, GroupName = "2. Sources")]
		public bool UsePreviousWeek { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "4 hour swings", Order = 3, GroupName = "2. Sources")]
		public bool UseFourHour { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "1 hour swings", Order = 4, GroupName = "2. Sources")]
		public bool UseOneHour { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "15 minute swings", Order = 5, GroupName = "2. Sources")]
		public bool UseFifteenMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Asia high/low", Order = 6, GroupName = "2. Sources")]
		public bool UseAsia { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "London high/low", Order = 7, GroupName = "2. Sources")]
		public bool UseLondon { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "NY AM high/low", Order = 8, GroupName = "2. Sources")]
		public bool UseNewYorkAm { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "NY Lunch high/low", Order = 9, GroupName = "2. Sources")]
		public bool UseNewYorkLunch { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "NY PM high/low", Order = 10, GroupName = "2. Sources")]
		public bool UseNewYorkPm { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Relative equal highs/lows", Order = 11, GroupName = "2. Sources")]
		public bool UseEqualHighLow { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Equal tolerance (ticks)", Order = 12, GroupName = "2. Sources")]
		public int EqualToleranceTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show gaps on", Order = 0, GroupName = "2b. Gaps",
			Description = "Chart and higher hides a 15m gap on a 4H chart. Detection is unaffected.")]
		public IfvgGapVisibility GapVisibility { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Gap labels", Order = 9, GroupName = "2b. Gaps")]
		public bool ShowGapLabels { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "15m gaps", Order = 1, GroupName = "2b. Gaps")]
		public bool UseFifteenMinuteGaps { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "1H gaps", Order = 2, GroupName = "2b. Gaps")]
		public bool UseOneHourGaps { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "4H gaps", Order = 3, GroupName = "2b. Gaps")]
		public bool UseFourHourGaps { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Daily gaps", Order = 4, GroupName = "2b. Gaps")]
		public bool UseDailyGaps { get; set; }

		[NinjaScriptProperty]
		[Range(1, 500)]
		[Display(Name = "Gap box length (bars)", Order = 5, GroupName = "2b. Gaps")]
		public int GapExtendBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, 400)]
		[Display(Name = "Gap lookback (days)", Order = 6, GroupName = "2b. Gaps")]
		public int GapLookbackDays { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Gap sensitivity (size in ATR)", Order = 7, GroupName = "2b. Gaps")]
		public IfvgGapSensitivity GapSensitivity { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Gap detection", Order = 8, GroupName = "2b. Gaps",
			Description = "Series merges adjacent gaps of the same direction into one zone.")]
		public IfvgGapDetection GapDetection { get; set; }

		[NinjaScriptProperty]
		[Range(2, 200)]
		[Display(Name = "ATR period", Order = 10, GroupName = "2b. Gaps")]
		public int AtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Mitigation confirm delay (bars)", Order = 11, GroupName = "2b. Gaps",
			Description = "Bars after price first enters a gap before it stops counting as a virgin PDA for delivery. Targeting uses consequent encroachment instead.")]
		public int MitigationConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 10000)]
		[Display(Name = "Minimum gap size (points, 0 = off)", Order = 12, GroupName = "2b. Gaps")]
		public double MinGapPoints { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Asia window (HHmm-HHmm)", Order = 1, GroupName = "3. Killzones")]
		public string AsiaWindow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "London window", Order = 2, GroupName = "3. Killzones")]
		public string LondonWindow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "NY AM window", Order = 3, GroupName = "3. Killzones")]
		public string NewYorkAmWindow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "NY Lunch window", Order = 4, GroupName = "3. Killzones")]
		public string NewYorkLunchWindow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "NY PM window", Order = 5, GroupName = "3. Killzones")]
		public string NewYorkPmWindow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Draw levels", Order = 1, GroupName = "4. Output")]
		public bool ShowLevels { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Level labels", Order = 2, GroupName = "4. Output")]
		public bool ShowLevelLabels { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Log levels and mitigations", Order = 2, GroupName = "4. Output")]
		public bool LogLevels { get; set; }

		#endregion
	}
}
