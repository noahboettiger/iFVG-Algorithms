// iFVG Model - liquidity level registry
//
// Step one of the generalised inverse fair value gap model specified in
// SPEC-INVERSION.md. This version marks and logs liquidity levels only and
// places no orders. A level the code gets wrong invalidates every trade that
// would reference it, so the registry is verified against a hand markup before
// entries are built on top of it.
//
// Sources: previous day extremes, swing highs and lows on 4H, 1H and 15m, Asia
// and London session extremes, and optionally relative equal highs and lows.

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
		AsiaSession,
		LondonSession,
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
			public bool				IsHigh;
			public int				Rank;
			public bool				Swept;
			public DateTime			SweptAt;

			public string Label
			{
				get
				{
					switch (Source)
					{
						case IfvgLevelSource.PreviousDay:		return "PDH/PDL";
						case IfvgLevelSource.FourHour:			return "4H";
						case IfvgLevelSource.OneHour:			return "1H";
						case IfvgLevelSource.FifteenMinute:		return "15m";
						case IfvgLevelSource.AsiaSession:		return "ASIA";
						case IfvgLevelSource.LondonSession:		return "LDN";
						default:								return "EQ";
					}
				}
			}
		}

		/// <summary>One monitored series, with the rolling window used to find pivots.</summary>
		private class Series
		{
			public int				Minutes;
			public int				Bip;
			public IfvgLevelSource	Source;
			public bool				Enabled;
			public List<double>		Highs = new List<double>();
			public List<double>		Lows = new List<double>();
			public List<DateTime>	Times = new List<DateTime>();
		}

		#endregion

		private List<Level>		levels;
		private List<Series>	series;
		private int				fineBip = -1;

		private DateTime		sessionDay		= DateTime.MinValue;
		private double			asiaHigh, asiaLow;
		private bool			asiaValid;
		private double			londonHigh, londonLow;
		private bool			londonValid;
		private bool			asiaPosted, londonPosted;

		private int asiaStartSec, asiaEndSec, londonStartSec, londonEndSec;

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
				MaxLevelsPerSource	= 8;
				LevelLookbackDays	= 5;

				UsePreviousDay		= true;
				UseFourHour			= true;
				UseOneHour			= true;
				UseFifteenMinute	= true;
				UseAsiaSession		= true;
				UseLondonSession	= true;
				UseEqualHighLow		= false;
				EqualTolerelanceTicks = 5;

				AsiaSessionStart	= 1800;
				AsiaSessionEnd		= 200;
				LondonSessionStart	= 200;
				LondonSessionEnd	= 800;

				ShowLevels			= true;
				LogLevels			= true;
			}
			else if (State == State.Configure)
			{
				// Finest series drives session tracking and sweep detection.
				AddDataSeries(BarsPeriodType.Minute, 1);
				AddDataSeries(BarsPeriodType.Minute, 15);
				AddDataSeries(BarsPeriodType.Minute, 60);
				AddDataSeries(BarsPeriodType.Minute, 240);
				AddDataSeries(BarsPeriodType.Day, 1);

				asiaStartSec	= ToSeconds(AsiaSessionStart);
				asiaEndSec		= ToSeconds(AsiaSessionEnd);
				londonStartSec	= ToSeconds(LondonSessionStart);
				londonEndSec	= ToSeconds(LondonSessionEnd);
			}
			else if (State == State.DataLoaded)
			{
				levels = new List<Level>();
				series = new List<Series>
				{
					new Series { Minutes = 15,  Source = IfvgLevelSource.FifteenMinute, Enabled = UseFifteenMinute },
					new Series { Minutes = 60,  Source = IfvgLevelSource.OneHour,       Enabled = UseOneHour },
					new Series { Minutes = 240, Source = IfvgLevelSource.FourHour,      Enabled = UseFourHour },
				};
				ResolveSeriesIndexes();
			}
		}

		/// <summary>
		/// Find each series by its loaded period rather than assuming the order in
		/// which AddDataSeries handed out indexes, which can shift when the primary
		/// series happens to match one of them.
		/// </summary>
		private void ResolveSeriesIndexes()
		{
			fineBip = -1;
			for (int i = 0; i < BarsArray.Length; i++)
			{
				if (BarsArray[i] == null)
					continue;
				BarsPeriod bp = BarsArray[i].BarsPeriod;
				if (bp.BarsPeriodType == BarsPeriodType.Minute && bp.Value == 1 && fineBip < 0)
					fineBip = i;
			}

			foreach (Series each in series)
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

			dailyBip = -1;
			for (int i = 0; i < BarsArray.Length; i++)
				if (BarsArray[i] != null && BarsArray[i].BarsPeriod.BarsPeriodType == BarsPeriodType.Day)
				{
					dailyBip = i;
					break;
				}

			Print("");
			Print("IfvgModel level registry loaded:");
			Print(string.Format("    1m   -> {0}", Describe(fineBip)));
			foreach (Series each in series)
				Print(string.Format("    {0,-4} -> {1}{2}", each.Minutes + "m", Describe(each.Bip),
					each.Enabled ? "" : "  (disabled)"));
			Print(string.Format("    day  -> {0}", Describe(dailyBip)));
			Print("");
		}

		private int dailyBip = -1;

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

			foreach (Series each in series)
				if (each.Bip == BarsInProgress)
				{
					TrackSwings(each);
					return;
				}

			if (BarsInProgress == fineBip)
				TrackSessions();
		}

		/// <summary>Previous trading day's extremes, taken from the completed daily bar.</summary>
		private void RegisterPreviousDay()
		{
			if (!UsePreviousDay || CurrentBars[dailyBip] < 1)
				return;

			DateTime stamp = Times[dailyBip][0];
			AddLevel(Highs[dailyBip][0], stamp, IfvgLevelSource.PreviousDay, true, 5);
			AddLevel(Lows[dailyBip][0], stamp, IfvgLevelSource.PreviousDay, false, 5);
		}

		/// <summary>
		/// A pivot is confirmed once SwingStrength bars have closed on each side of
		/// it, so the level is registered with the timestamp of the pivot bar rather
		/// than the bar that confirmed it.
		/// </summary>
		private void TrackSwings(Series each)
		{
			if (!each.Enabled || each.Bip < 0)
				return;

			each.Highs.Add(Highs[each.Bip][0]);
			each.Lows.Add(Lows[each.Bip][0]);
			each.Times.Add(Times[each.Bip][0]);

			int window = SwingStrength * 2 + 1;
			int keep = Math.Max(window, 60);
			if (each.Highs.Count > keep)
			{
				each.Highs.RemoveAt(0);
				each.Lows.RemoveAt(0);
				each.Times.RemoveAt(0);
			}
			if (each.Highs.Count < window)
				return;

			int pivot = each.Highs.Count - 1 - SwingStrength;
			int rank = each.Minutes >= 240 ? 4 : each.Minutes >= 60 ? 3 : 2;

			bool isSwingHigh = true, isSwingLow = true;
			for (int i = pivot - SwingStrength; i <= pivot + SwingStrength; i++)
			{
				if (i == pivot)
					continue;
				if (each.Highs[i] >= each.Highs[pivot]) isSwingHigh = false;
				if (each.Lows[i] <= each.Lows[pivot])   isSwingLow = false;
			}

			if (isSwingHigh)
				AddLevel(each.Highs[pivot], each.Times[pivot], each.Source, true, rank);
			if (isSwingLow)
				AddLevel(each.Lows[pivot], each.Times[pivot], each.Source, false, rank);
		}

		/// <summary>Asia and London extremes, accumulated on the 1-minute series.</summary>
		private void TrackSessions()
		{
			DateTime stamp	= Times[fineBip][0];
			int tod			= stamp.Hour * 3600 + stamp.Minute * 60 + stamp.Second;
			double high		= Highs[fineBip][0];
			double low		= Lows[fineBip][0];

			if (stamp.Date != sessionDay)
			{
				sessionDay	= stamp.Date;
				londonValid	= false;
				londonPosted = false;
			}

			if (InWindow(tod, asiaStartSec, asiaEndSec))
			{
				asiaHigh	= asiaValid ? Math.Max(asiaHigh, high) : high;
				asiaLow		= asiaValid ? Math.Min(asiaLow, low)   : low;
				asiaValid	= true;
				asiaPosted	= false;
			}
			else if (asiaValid && !asiaPosted)
			{
				asiaPosted = true;
				if (UseAsiaSession)
				{
					AddLevel(asiaHigh, stamp, IfvgLevelSource.AsiaSession, true, 2);
					AddLevel(asiaLow, stamp, IfvgLevelSource.AsiaSession, false, 2);
				}
				asiaValid = false;
			}

			if (InWindow(tod, londonStartSec, londonEndSec))
			{
				londonHigh	= londonValid ? Math.Max(londonHigh, high) : high;
				londonLow	= londonValid ? Math.Min(londonLow, low)   : low;
				londonValid	= true;
				londonPosted = false;
			}
			else if (londonValid && !londonPosted)
			{
				londonPosted = true;
				if (UseLondonSession)
				{
					AddLevel(londonHigh, stamp, IfvgLevelSource.LondonSession, true, 2);
					AddLevel(londonLow, stamp, IfvgLevelSource.LondonSession, false, 2);
				}
				londonValid = false;
			}

			MarkSweeps(high, low, stamp);
		}

		/// <summary>Windows that cross midnight wrap, so the test flips accordingly.</summary>
		private static bool InWindow(int tod, int start, int end)
		{
			return start <= end
				? tod >= start && tod < end
				: tod >= start || tod < end;
		}

		private void MarkSweeps(double high, double low, DateTime stamp)
		{
			foreach (Level level in levels)
			{
				if (level.Swept)
					continue;
				bool taken = level.IsHigh ? high > level.Price : low < level.Price;
				if (!taken)
					continue;

				level.Swept = true;
				level.SweptAt = stamp;
				if (LogLevels)
					Print(string.Format("{0:yyyy-MM-dd HH:mm}  swept {1} {2} at {3}",
						stamp, level.Label, level.IsHigh ? "high" : "low", Format(level.Price)));
			}
		}

		#endregion

		#region Registry

		private void AddLevel(double price, DateTime created, IfvgLevelSource source,
			bool isHigh, int rank)
		{
			// A level at the same price from the same source is the same level.
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
			};
			levels.Add(level);

			if (LogLevels)
				Print(string.Format("{0:yyyy-MM-dd HH:mm}  {1} {2} at {3}",
					created, level.Label, isHigh ? "high" : "low", Format(price)));

			DrawLevel(level);
			if (UseEqualHighLow)
				ScanForEqualPairs(level);
			Prune();
		}

		/// <summary>
		/// Equal highs need the first to sit above the second, within tolerance. A
		/// second extreme that exceeds the first is a break, not an equal pair.
		/// </summary>
		private void ScanForEqualPairs(Level latest)
		{
			if (latest.Source == IfvgLevelSource.EqualHighLow)
				return;

			double tolerance = EqualTolerelanceTicks * TickSize;
			foreach (Level prior in levels)
			{
				if (prior == latest || prior.IsHigh != latest.IsHigh)
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

		private void Prune()
		{
			DateTime cutoff = Times[BarsInProgress][0].AddDays(-LevelLookbackDays);
			levels.RemoveAll(delegate(Level l) { return l.CreatedAt < cutoff; });

			foreach (IfvgLevelSource source in Enum.GetValues(typeof(IfvgLevelSource)))
			{
				IfvgLevelSource captured = source;
				List<Level> ofSource = levels.FindAll(
					delegate(Level l) { return l.Source == captured; });
				if (ofSource.Count <= MaxLevelsPerSource * 2)
					continue;

				ofSource.Sort(delegate(Level a, Level b)
				{
					return a.CreatedAt.CompareTo(b.CreatedAt);
				});
				int excess = ofSource.Count - MaxLevelsPerSource * 2;
				for (int i = 0; i < excess; i++)
					levels.Remove(ofSource[i]);
			}
		}

		#endregion

		#region Drawing

		private void DrawLevel(Level level)
		{
			if (!ShowLevels)
				return;

			Brush brush = level.Rank >= 5 ? Brushes.Gold
				: level.Rank == 4 ? Brushes.OrangeRed
				: level.Rank == 3 ? Brushes.DeepSkyBlue
				: level.Rank == 2 ? Brushes.MediumPurple
				: Brushes.Gray;

			string tag = "lvl" + level.Source + level.CreatedAt.Ticks + (level.IsHigh ? "H" : "L");
			DateTime finish = level.CreatedAt.AddHours(Math.Max(LevelLookbackDays, 1) * 24);

			Draw.Line(this, tag, false, level.CreatedAt, level.Price, finish, level.Price,
				brush, level.Rank >= 4 ? DashStyleHelper.Solid : DashStyleHelper.Dot,
				level.Rank >= 5 ? 2 : 1);
			Draw.Text(this, tag + "T", false, level.Label, level.CreatedAt, level.Price,
				level.IsHigh ? 6 : -6, brush, new SimpleFont("Arial", 9),
				System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
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
		[Range(1, 50)]
		[Display(Name = "Max levels kept per source", Order = 2, GroupName = "1. Levels")]
		public int MaxLevelsPerSource { get; set; }

		[NinjaScriptProperty]
		[Range(1, 60)]
		[Display(Name = "Level lookback (days)", Order = 3, GroupName = "1. Levels")]
		public int LevelLookbackDays { get; set; }

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
		[Display(Name = "Asia session high/low", Order = 5, GroupName = "2. Sources")]
		public bool UseAsiaSession { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "London session high/low", Order = 6, GroupName = "2. Sources")]
		public bool UseLondonSession { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Relative equal highs/lows", Order = 7, GroupName = "2. Sources")]
		public bool UseEqualHighLow { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Equal tolerance (ticks)", Order = 8, GroupName = "2. Sources")]
		public int EqualTolerelanceTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Asia session start (HHmm)", Order = 1, GroupName = "3. Session windows")]
		public int AsiaSessionStart { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Asia session end (HHmm)", Order = 2, GroupName = "3. Session windows")]
		public int AsiaSessionEnd { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "London session start (HHmm)", Order = 3, GroupName = "3. Session windows")]
		public int LondonSessionStart { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "London session end (HHmm)", Order = 4, GroupName = "3. Session windows")]
		public int LondonSessionEnd { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Draw levels", Order = 1, GroupName = "4. Output")]
		public bool ShowLevels { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Log levels and sweeps", Order = 2, GroupName = "4. Output")]
		public bool LogLevels { get; set; }

		#endregion
	}
}
