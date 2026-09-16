// iFVG Model - liquidity level registry
//
// Step one of the generalised inverse fair value gap model specified in
// SPEC-INVERSION.md. This version marks and logs liquidity levels only and
// places no orders. A level the code gets wrong invalidates every trade that
// would reference it, so the registry is verified against a hand markup before
// entries are built on top of it.
//
// Killzone windows and the mitigation behaviour follow the ICT Killzones &
// Pivots indicator (tradeforopp) that the same levels are read from by hand.
// A level is live until price trades through it; after that it is mitigated,
// its line stops extending, and it is no longer a sweep or target candidate.

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

	public class IfvgModel : Strategy
	{
		#region Nested state

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
			public bool				Enabled;
			public List<double>		Highs = new List<double>();
			public List<double>		Lows = new List<double>();
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
		private List<PivotSeries>	pivots;
		private List<Killzone>		killzones;
		private int					fineBip		= -1;
		private int					dailyBip	= -1;

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

				// Defaults taken from the ICT Killzones & Pivots indicator.
				AsiaWindow			= "2000-0000";
				LondonWindow		= "0200-0500";
				NewYorkAmWindow		= "0930-1100";
				NewYorkLunchWindow	= "1200-1300";
				NewYorkPmWindow		= "1330-1600";

				ShowLevels			= true;
				LogLevels			= true;
			}
			else if (State == State.Configure)
			{
				AddDataSeries(BarsPeriodType.Minute, 1);
				AddDataSeries(BarsPeriodType.Minute, 15);
				AddDataSeries(BarsPeriodType.Minute, 60);
				AddDataSeries(BarsPeriodType.Minute, 240);
				AddDataSeries(BarsPeriodType.Day, 1);
			}
			else if (State == State.DataLoaded)
			{
				levels = new List<Level>();
				pivots = new List<PivotSeries>
				{
					new PivotSeries { Minutes = 15,  Source = IfvgLevelSource.FifteenMinute, Rank = 2, Enabled = UseFifteenMinute },
					new PivotSeries { Minutes = 60,  Source = IfvgLevelSource.OneHour,       Rank = 3, Enabled = UseOneHour },
					new PivotSeries { Minutes = 240, Source = IfvgLevelSource.FourHour,      Rank = 4, Enabled = UseFourHour },
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
			}
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
			fineBip = dailyBip = -1;
			for (int i = 0; i < BarsArray.Length; i++)
			{
				if (BarsArray[i] == null)
					continue;
				BarsPeriod bp = BarsArray[i].BarsPeriod;
				if (bp.BarsPeriodType == BarsPeriodType.Day && dailyBip < 0)
					dailyBip = i;
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
					if (bp.BarsPeriodType == BarsPeriodType.Minute && bp.Value == each.Minutes)
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
			{
				RegisterPreviousDay();
				return;
			}

			foreach (PivotSeries each in pivots)
				if (each.Bip == BarsInProgress)
				{
					TrackPivots(each);
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

		/// <summary>
		/// A pivot is confirmed once SwingStrength bars have closed either side of
		/// it, so it registers with the timestamp of the pivot bar rather than the
		/// bar that confirmed it.
		/// </summary>
		private void TrackPivots(PivotSeries each)
		{
			if (!each.Enabled || each.Bip < 0)
				return;

			each.Highs.Add(Highs[each.Bip][0]);
			each.Lows.Add(Lows[each.Bip][0]);
			each.Times.Add(Times[each.Bip][0]);

			int window = SwingStrength * 2 + 1;
			if (each.Highs.Count > Math.Max(window, 60))
			{
				each.Highs.RemoveAt(0);
				each.Lows.RemoveAt(0);
				each.Times.RemoveAt(0);
			}
			if (each.Highs.Count < window)
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

			Prune(stamp);
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

		private void Prune(DateTime now)
		{
			DateTime cutoff = now.AddDays(-LevelLookbackDays);
			List<Level> stale = levels.FindAll(
				delegate(Level l) { return l.CreatedAt < cutoff; });
			foreach (Level each in stale)
				Erase(each);
			levels.RemoveAll(delegate(Level l) { return l.CreatedAt < cutoff; });
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
			NinjaTrader.NinjaScript.DrawingTools.Draw.Text(this, level.Tag + "T", false, Describe(level.Source),
				level.CreatedAt, level.Price, level.IsHigh ? 6 : -6, brush,
				new SimpleFont("Arial", 9), System.Windows.TextAlignment.Left,
				Brushes.Transparent, Brushes.Transparent, 0);
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
		[Display(Name = "4 hour swings", Order = 2, GroupName = "2. Sources")]
		public bool UseFourHour { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "1 hour swings", Order = 3, GroupName = "2. Sources")]
		public bool UseOneHour { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "15 minute swings", Order = 4, GroupName = "2. Sources")]
		public bool UseFifteenMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Asia high/low", Order = 5, GroupName = "2. Sources")]
		public bool UseAsia { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "London high/low", Order = 6, GroupName = "2. Sources")]
		public bool UseLondon { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "NY AM high/low", Order = 7, GroupName = "2. Sources")]
		public bool UseNewYorkAm { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "NY Lunch high/low", Order = 8, GroupName = "2. Sources")]
		public bool UseNewYorkLunch { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "NY PM high/low", Order = 9, GroupName = "2. Sources")]
		public bool UseNewYorkPm { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Relative equal highs/lows", Order = 10, GroupName = "2. Sources")]
		public bool UseEqualHighLow { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Equal tolerance (ticks)", Order = 11, GroupName = "2. Sources")]
		public int EqualToleranceTicks { get; set; }

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
		[Display(Name = "Log levels and mitigations", Order = 2, GroupName = "4. Output")]
		public bool LogLevels { get; set; }

		#endregion
	}
}
