// ICT-V5.cs
// ICT Price Leg Strategy for NQ Futures — NinjaTrader 8
//
// ╔══════════════════════════════════════╗
// ║  VERSION: v5                         ║
// ║  DATE:    2026-03-20                 ║
// ╚══════════════════════════════════════╝
//
// v5 fixes (vs v4):
//   - BUGFIX: removed EQ balance check from WaitingSweep1
//     (externe level is below EQ → price must cross EQ to sweep it,
//      old check fired before every sweep making it impossible)
//   - WaitingSweep1 now only resets on: leg top break OR Sweep1Timeout
//   - Added Sweep1Timeout parameter (default 80 bars)
//   - Version printed on DataLoaded for easy identification
//   - Renamed file/class to ICT-V5 / ICTV5
//
// v4 changes:
//   - Double sweep: Externe Low (LL1) → LL1 swept again (LL2) → entry
//   - CISD-only entry (no iFVG)
//   - Bias Option C: 4H Market Structure + Previous Day H/L (both must align)

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ICTV5 : Strategy
    {
        private const string StrategyVersion = "v5";

        // ─── Parameters ────────────────────────────────────────────────────

        [NinjaScriptProperty][Range(1,20)]
        [Display(Name="Swing Strength (Exec TF)",      Order=1, GroupName="Leg Detection")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty][Range(1,10)]
        [Display(Name="Externes Level Swing Strength", Order=2, GroupName="Leg Detection")]
        public int ExtSwingStrength { get; set; }

        [NinjaScriptProperty][Range(5,500)]
        [Display(Name="Min Leg Bars (Exec TF)",        Order=3, GroupName="Leg Detection")]
        public int MinLegBars { get; set; }

        [NinjaScriptProperty][Range(1,20)]
        [Display(Name="Bias Swing Strength (4H)",      Order=4, GroupName="Leg Detection")]
        public int BiasSwingStrength { get; set; }

        [NinjaScriptProperty][Range(60,1440)]
        [Display(Name="Bias Timeframe (Minutes)",      Order=5, GroupName="Leg Detection",
            Description="240 = 4H")]
        public int BiasTFMinutes { get; set; }

        [NinjaScriptProperty][Range(1,200)]
        [Display(Name="Sweep1 Timeout (Bars)",         Order=1, GroupName="Entry Rules",
            Description="Max bars after leg end to wait for Sweep1")]
        public int Sweep1Timeout { get; set; }

        [NinjaScriptProperty][Range(1,50)]
        [Display(Name="CISD Window (Bars after LL2)",  Order=2, GroupName="Entry Rules",
            Description="Max bars after second sweep to find CISD close")]
        public int CisdWindow { get; set; }

        [NinjaScriptProperty][Range(0,100)]
        [Display(Name="SL Buffer (Ticks)",             Order=3, GroupName="Entry Rules")]
        public int SlBufferTicks { get; set; }

        [NinjaScriptProperty][Range(0.1,10.0)]
        [Display(Name="Risk Per Trade (%)",            Order=4, GroupName="Entry Rules")]
        public double RiskPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Debug Mode",                    Order=1, GroupName="Debug")]
        public bool DebugMode { get; set; }

        // ─── State machine ─────────────────────────────────────────────────

        private enum S { Scanning, WaitingSweep1, WaitingSweep2, WaitingCISD }
        private S setupState;

        // Active leg
        private double legStartPrice, legEndPrice, legEq;
        private int    legStartBar,   legEndBar;
        private bool   legIsBull;
        private double activeExtLevel;

        // Sweep 1 (Externe Low swept → creates LL1)
        private int    sweep1Bar;
        private double sweep1Extreme;

        // Sweep 2 (LL1 swept → creates LL2)
        private int    sweep2Bar;
        private double sweep2Extreme;

        // CISD level = highest high (bull) / lowest low (bear) between sweep1 and sweep2
        private double cisdLevel;

        // ─── Bias ──────────────────────────────────────────────────────────

        private bool   biasIsBull;
        private bool   biasAvailable;

        // 4H market structure
        private int    bias4hBarCount;
        private List<int>    biasShBars,   biasSlBars;
        private List<double> biasShPrices, biasSlPrices;
        private string struct4h; // "bull" | "bear" | "none"

        // Previous day high/low (series 2 = Daily)
        private double prevDayHigh;
        private double prevDayLow;
        private bool   prevDaySet;

        // Exec TF swing lists
        private List<int>    execShBars,   execSlBars;
        private List<double> execShPrices, execSlPrices;

        // ─── Lifecycle ─────────────────────────────────────────────────────

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                         = "ICTV5";
                Description                  = "ICT Double Sweep + CISD — NQ Futures " + StrategyVersion;
                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                MaximumBarsLookBack          = MaximumBarsLookBack.Infinite;

                SwingStrength     = 3;
                ExtSwingStrength  = 2;
                MinLegBars        = 25;
                BiasSwingStrength = 3;
                BiasTFMinutes     = 240;
                Sweep1Timeout     = 80;
                CisdWindow        = 15;
                SlBufferTicks     = 20;
                RiskPercent       = 1.0;
                DebugMode         = true;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, BiasTFMinutes); // series 1 = 4H
                AddDataSeries(BarsPeriodType.Day,    1);             // series 2 = Daily
            }
            else if (State == State.DataLoaded)
            {
                setupState     = S.Scanning;
                bias4hBarCount = 0;
                struct4h       = "none";
                prevDayHigh    = double.MaxValue;
                prevDayLow     = double.MinValue;
                prevDaySet     = false;
                biasAvailable  = false;

                execShBars   = new List<int>(); execShPrices = new List<double>();
                execSlBars   = new List<int>(); execSlPrices = new List<double>();
                biasShBars   = new List<int>(); biasShPrices = new List<double>();
                biasSlBars   = new List<int>(); biasSlPrices = new List<double>();

                Print("================================================");
                Print("  ICT-V5 loaded — ICT Double Sweep + CISD");
                Print("  Bias: 4H Structure + Prev Day H/L (Option C)");
                Print("================================================");
                D("Waiting for bias data ...");
            }
        }

        // ─── Bar update ────────────────────────────────────────────────────

        protected override void OnBarUpdate()
        {
            // ── Series 1: 4H — track swings + market structure ─────────────
            if (BarsInProgress == 1)
            {
                bias4hBarCount++;
                if (bias4hBarCount < BiasSwingStrength * 2 + 1) return;
                Update4HSwings();
                Update4HStructure();
                return;
            }

            // ── Series 2: Daily — track previous day high/low ──────────────
            if (BarsInProgress == 2)
            {
                if (BarsArray[2].Count > 1)
                {
                    prevDayHigh = Highs[2][1];
                    prevDayLow  = Lows[2][1];
                    prevDaySet  = true;
                    D($"  [DAY] Prev day H={prevDayHigh:F2} L={prevDayLow:F2}");
                }
                return;
            }

            // ── Series 0: Execution TF ─────────────────────────────────────
            if (BarsInProgress != 0) return;
            int minWarmup = Math.Max(SwingStrength * 2, MinLegBars) + 10;
            if (CurrentBar < minWarmup) return;

            UpdateExecSwings();
            UpdateCombinedBias();

            if (!biasAvailable)
            {
                if (DebugMode && CurrentBar % 500 == 0)
                    D($"Bar {CurrentBar}: bias not available (struct4h={struct4h} prevDaySet={prevDaySet})");
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat) return;

            switch (setupState)
            {
                case S.Scanning:      RunScanning();      break;
                case S.WaitingSweep1: RunWaitingSweep1(); break;
                case S.WaitingSweep2: RunWaitingSweep2(); break;
                case S.WaitingCISD:   RunWaitingCISD();   break;
            }
        }

        // ─── Bias: 4H Market Structure ─────────────────────────────────────

        private void Update4HSwings()
        {
            int cur0 = bias4hBarCount - 1;

            if (Bias4HIsSwingHigh(BiasSwingStrength))
            {
                int bar = cur0 - BiasSwingStrength;
                if (biasShBars.Count == 0 || biasShBars[biasShBars.Count - 1] != bar)
                {
                    biasShBars.Add(bar);
                    biasShPrices.Add(Highs[1][BiasSwingStrength]);
                    D($"  [4H] Swing HIGH bar={bar} price={Highs[1][BiasSwingStrength]:F2}");
                }
            }
            if (Bias4HIsSwingLow(BiasSwingStrength))
            {
                int bar = cur0 - BiasSwingStrength;
                if (biasSlBars.Count == 0 || biasSlBars[biasSlBars.Count - 1] != bar)
                {
                    biasSlBars.Add(bar);
                    biasSlPrices.Add(Lows[1][BiasSwingStrength]);
                    D($"  [4H] Swing LOW  bar={bar} price={Lows[1][BiasSwingStrength]:F2}");
                }
            }
        }

        private void Update4HStructure()
        {
            // Need at least 2 swing highs and 2 swing lows
            if (biasShBars.Count < 2 || biasSlBars.Count < 2) return;

            double lastSH = biasShPrices[biasShPrices.Count - 1];
            double prevSH = biasShPrices[biasShPrices.Count - 2];
            double lastSL = biasSlPrices[biasSlPrices.Count - 1];
            double prevSL = biasSlPrices[biasSlPrices.Count - 2];

            string prev = struct4h;

            if (lastSH > prevSH && lastSL > prevSL)
                struct4h = "bull"; // Higher High + Higher Low
            else if (lastSH < prevSH && lastSL < prevSL)
                struct4h = "bear"; // Lower High + Lower Low
            else
                struct4h = "none"; // mixed structure

            if (struct4h != prev)
                D($"  [4H] Structure -> {struct4h} (SH: {prevSH:F0}->{lastSH:F0}  SL: {prevSL:F0}->{lastSL:F0})");
        }

        private void UpdateCombinedBias()
        {
            if (!prevDaySet || struct4h == "none") { biasAvailable = false; return; }

            bool structBull = struct4h == "bull";
            bool structBear = struct4h == "bear";

            // Previous day confirmation: current price broke prev day H or L
            bool dayBull = Close[0] > prevDayHigh;
            bool dayBear = Close[0] < prevDayLow;

            bool newBull = structBull && dayBull;
            bool newBear = structBear && dayBear;

            if (!newBull && !newBear) { biasAvailable = false; return; }

            bool prev = biasIsBull;
            biasIsBull    = newBull;
            biasAvailable = true;

            if (biasIsBull != prev)
                D($"Bar {CurrentBar}: [BIAS] -> {(biasIsBull ? "BULL" : "BEAR")} " +
                  $"(4H={struct4h} price={Close[0]:F2} prevDH={prevDayHigh:F2} prevDL={prevDayLow:F2})");
        }

        // ─── Exec TF swing detection ────────────────────────────────────────

        private void UpdateExecSwings()
        {
            if (ExecIsSwingHigh(SwingStrength))
            {
                int bar = CurrentBar - SwingStrength;
                if (execShBars.Count == 0 || execShBars[execShBars.Count - 1] != bar)
                { execShBars.Add(bar); execShPrices.Add(High[SwingStrength]); }
            }
            if (ExecIsSwingLow(SwingStrength))
            {
                int bar = CurrentBar - SwingStrength;
                if (execSlBars.Count == 0 || execSlBars[execSlBars.Count - 1] != bar)
                { execSlBars.Add(bar); execSlPrices.Add(Low[SwingStrength]); }
            }
        }

        // ─── State machine ──────────────────────────────────────────────────

        private void RunScanning()
        {
            var merged = BuildMergedSwings(execShBars, execShPrices, execSlBars, execSlPrices);
            if (merged.Count < 2) return;

            for (int i = merged.Count - 2; i >= 0; i--)
            {
                var s = merged[i];
                var e = merged[i + 1];
                if (e.BarIdx - s.BarIdx < MinLegBars) continue;

                bool legBull = s.IsLow && !e.IsLow;
                bool legBear = !s.IsLow && e.IsLow;
                if (!legBull && !legBear) continue;
                if (legBull != biasIsBull) continue; // must match bias

                double eq = (s.Price + e.Price) / 2.0;

                // Check if leg is already balanced
                bool balanced = false;
                for (int k = 1; (e.BarIdx + k) <= CurrentBar; k++)
                {
                    int ba = CurrentBar - (e.BarIdx + k);
                    if (ba < 0) break;
                    if (legBull && Low[ba]  <= eq) { balanced = true; break; }
                    if (legBear && High[ba] >= eq) { balanced = true; break; }
                }
                if (balanced) continue;

                // Find externe level in discount (bull) / premium (bear)
                double extLevel = FindExterneLevel(s.BarIdx, e.BarIdx, legBull, eq);
                if (double.IsNaN(extLevel)) continue;

                legStartPrice  = s.Price; legEndPrice = e.Price; legEq = eq;
                legStartBar    = s.BarIdx; legEndBar   = e.BarIdx;
                legIsBull      = legBull;
                activeExtLevel = extLevel;
                setupState     = S.WaitingSweep1;

                D($"Bar {CurrentBar}: [SCAN] {(legBull ? "BULL" : "BEAR")} leg " +
                  $"{s.Price:F0}->{e.Price:F0} EQ={eq:F0} ExtLevel={extLevel:F2}");
                break;
            }
        }

        private void RunWaitingSweep1()
        {
            if (CurrentBar <= legEndBar) return;

            // Timeout
            if (CurrentBar - legEndBar > Sweep1Timeout)
            {
                D($"Bar {CurrentBar}: [S1] Timeout — reset.");
                setupState = S.Scanning; return;
            }

            // Invalidate only if price breaks THROUGH the leg top/bottom
            // (do NOT check EQ balance here — the externe level is in discount/premium,
            //  so price must cross EQ to reach it; that does NOT invalidate the setup)
            if (legIsBull  && High[0] > legEndPrice) { D($"Bar {CurrentBar}: [S1] Price above leg top — reset."); setupState = S.Scanning; return; }
            if (!legIsBull && Low[0]  < legEndPrice) { D($"Bar {CurrentBar}: [S1] Price below leg bottom — reset."); setupState = S.Scanning; return; }

            // Check sweep of Externe Low / High
            bool swept = legIsBull ? Low[0] < activeExtLevel : High[0] > activeExtLevel;
            if (swept)
            {
                sweep1Bar     = CurrentBar;
                sweep1Extreme = legIsBull ? Low[0] : High[0];
                setupState    = S.WaitingSweep2;
                D($"Bar {CurrentBar}: [S1] ExtLevel {activeExtLevel:F2} swept → LL1={sweep1Extreme:F2}. Waiting S2 ...");
            }
        }

        private void RunWaitingSweep2()
        {
            // Check sweep of LL1 (second sweep)
            bool swept2 = legIsBull ? Low[0] < sweep1Extreme : High[0] > sweep1Extreme;
            if (swept2)
            {
                sweep2Bar     = CurrentBar;
                sweep2Extreme = legIsBull ? Low[0] : High[0];

                // CISD level = highest high (bull) / lowest low (bear) between S1 and S2
                cisdLevel = FindReactionLevel(sweep1Bar, sweep2Bar, legIsBull);

                setupState = S.WaitingCISD;
                D($"Bar {CurrentBar}: [S2] LL1 swept → LL2={sweep2Extreme:F2}. CISD level={cisdLevel:F2}");
                return;
            }

            // Reset if price breaks through leg top/bottom (setup invalidated)
            if (legIsBull  && High[0] > legEndPrice) { D($"Bar {CurrentBar}: [S2] Price above leg top — reset."); setupState = S.Scanning; }
            if (!legIsBull && Low[0]  < legEndPrice) { D($"Bar {CurrentBar}: [S2] Price below leg bottom — reset."); setupState = S.Scanning; }
        }

        private void RunWaitingCISD()
        {
            int barsSinceS2 = CurrentBar - sweep2Bar;
            if (barsSinceS2 > CisdWindow)
            {
                D($"Bar {CurrentBar}: [CISD] Timeout ({barsSinceS2} bars) — reset.");
                setupState = S.Scanning;
                return;
            }

            // CISD: close above reaction high (bull) / below reaction low (bear)
            bool cisdBull = legIsBull  && Close[0] > cisdLevel;
            bool cisdBear = !legIsBull && Close[0] < cisdLevel;
            if (!cisdBull && !cisdBear) return;

            double entry = Close[0];
            double sl, tp;

            if (legIsBull)
            {
                sl = sweep2Extreme - SlBufferTicks * TickSize;
                tp = legEndPrice;
                if (tp <= entry) { D($"Bar {CurrentBar}: [CISD] TP {tp:F2} <= Entry {entry:F2} — reset."); setupState = S.Scanning; return; }
            }
            else
            {
                sl = sweep2Extreme + SlBufferTicks * TickSize;
                tp = legEndPrice;
                if (tp >= entry) { D($"Bar {CurrentBar}: [CISD] TP {tp:F2} >= Entry {entry:F2} — reset."); setupState = S.Scanning; return; }
            }

            int qty = CalcContracts(entry, sl);
            if (qty < 1) qty = 1;

            SetStopLoss    ("ICT", CalculationMode.Price, sl, false);
            SetProfitTarget("ICT", CalculationMode.Price, tp);

            if (legIsBull) EnterLong (qty, "ICT");
            else           EnterShort(qty, "ICT");

            double rr = Math.Abs(tp - entry) / Math.Abs(entry - sl);
            D($"Bar {CurrentBar}: *** {(legIsBull ? "LONG" : "SHORT")} {qty}ct " +
              $"Entry={entry:F2} SL={sl:F2} TP={tp:F2} R:R={rr:F1}R ***");

            setupState = S.Scanning;
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        private double FindReactionLevel(int s1Bar, int s2Bar, bool isBull)
        {
            double level = isBull ? double.MinValue : double.MaxValue;

            for (int abs = s1Bar + 1; abs < s2Bar; abs++)
            {
                int barsAgo = CurrentBar - abs;
                if (barsAgo < 0 || barsAgo > CurrentBar) continue;

                if (isBull  && High[barsAgo] > level) level = High[barsAgo];
                if (!isBull && Low[barsAgo]  < level) level = Low[barsAgo];
            }

            // Fallback: immediate re-sweep (no bars between S1 and S2)
            if (isBull  && level == double.MinValue) level = High[CurrentBar - s1Bar];
            if (!isBull && level == double.MaxValue) level = Low[CurrentBar - s1Bar];

            return level;
        }

        private double FindExterneLevel(int legStartAbs, int legEndAbs, bool isBull, double eq)
        {
            for (int abs = legStartAbs + ExtSwingStrength; abs <= legEndAbs - ExtSwingStrength; abs++)
            {
                int ba = CurrentBar - abs;
                if (ba < 0 || ba > CurrentBar) continue;

                if (isBull  && Low[ba]  < eq && ExecIsSwingLowAt(ba,  ExtSwingStrength)) return Low[ba];
                if (!isBull && High[ba] > eq && ExecIsSwingHighAt(ba, ExtSwingStrength)) return High[ba];
            }
            return double.NaN;
        }

        private bool ExecIsSwingHighAt(int barsAgo, int n)
        {
            double r = High[barsAgo];
            for (int i = 1; i <= n; i++)
            {
                int newer = barsAgo - i, older = barsAgo + i;
                if (newer < 0 || older > CurrentBar) return false;
                if (High[newer] >= r || High[older] >= r) return false;
            }
            return true;
        }

        private bool ExecIsSwingLowAt(int barsAgo, int n)
        {
            double r = Low[barsAgo];
            for (int i = 1; i <= n; i++)
            {
                int newer = barsAgo - i, older = barsAgo + i;
                if (newer < 0 || older > CurrentBar) return false;
                if (Low[newer] <= r || Low[older] <= r) return false;
            }
            return true;
        }

        private bool ExecIsSwingHigh(int n)
        {
            if (CurrentBar < n * 2) return false;
            double r = High[n];
            for (int i = 1; i <= n; i++)
                if (High[n - i] >= r || High[n + i] >= r) return false;
            return true;
        }

        private bool ExecIsSwingLow(int n)
        {
            if (CurrentBar < n * 2) return false;
            double r = Low[n];
            for (int i = 1; i <= n; i++)
                if (Low[n - i] <= r || Low[n + i] <= r) return false;
            return true;
        }

        private bool Bias4HIsSwingHigh(int n)
        {
            if (bias4hBarCount < n * 2 + 1) return false;
            double r = Highs[1][n];
            for (int i = 1; i <= n; i++)
                if (Highs[1][n - i] >= r || Highs[1][n + i] >= r) return false;
            return true;
        }

        private bool Bias4HIsSwingLow(int n)
        {
            if (bias4hBarCount < n * 2 + 1) return false;
            double r = Lows[1][n];
            for (int i = 1; i <= n; i++)
                if (Lows[1][n - i] <= r || Lows[1][n + i] <= r) return false;
            return true;
        }

        private List<SwingPoint> BuildMergedSwings(
            List<int> shBars, List<double> shPrices,
            List<int> slBars, List<double> slPrices)
        {
            var all = new List<SwingPoint>();
            for (int i = 0; i < shBars.Count; i++) all.Add(new SwingPoint(shBars[i], shPrices[i], false));
            for (int i = 0; i < slBars.Count; i++) all.Add(new SwingPoint(slBars[i], slPrices[i], true));
            all.Sort((a, b) => a.BarIdx.CompareTo(b.BarIdx));

            var clean = new List<SwingPoint>();
            foreach (var sp in all)
            {
                if (clean.Count > 0 && clean[clean.Count - 1].IsLow == sp.IsLow)
                {
                    var prev = clean[clean.Count - 1];
                    if (sp.IsLow  && sp.Price < prev.Price) clean[clean.Count - 1] = sp;
                    if (!sp.IsLow && sp.Price > prev.Price) clean[clean.Count - 1] = sp;
                }
                else clean.Add(sp);
            }
            return clean;
        }

        private int CalcContracts(double entry, double sl)
        {
            double accountVal  = Account.Get(AccountItem.CashValue, Currency.UsDollar);
            double riskUsd     = accountVal * (RiskPercent / 100.0);
            double riskPerCont = Math.Abs(entry - sl) * Instrument.MasterInstrument.PointValue;
            if (riskPerCont <= 0) return 1;
            return Math.Max(1, (int)(riskUsd / riskPerCont));
        }

        private void D(string msg) { if (DebugMode) Print(msg); }

        private struct SwingPoint
        {
            public int BarIdx; public double Price; public bool IsLow;
            public SwingPoint(int b, double p, bool l) { BarIdx = b; Price = p; IsLow = l; }
        }
    }
}
