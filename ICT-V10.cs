// ICT-V10.cs
// ICT Price Leg Strategy for NQ Futures — NinjaTrader 8
//
// ╔══════════════════════════════════════╗
// ║  VERSION: v10                        ║
// ║  DATE:    2026-03-20                 ║
// ╚══════════════════════════════════════╝
//
// v10 fixes (vs v9):
//   - BUGFIX: SCAN→"Price below leg bottom" infinite loop.
//     After bias activates, RunScanning found a valid leg, entered
//     WaitingSweep1.  The very next bar: Low[0] < legEndPrice (price
//     broke through the leg's SL) → "Price below leg bottom" reset →
//     back to Scanning → same leg found again → repeat every 2 bars.
//     Root cause: the balanced-check only tested price vs EQ, not vs
//     the leg endpoints.  A leg should also be invalidated if price
//     breaks THROUGH the leg end (below SL for bear, above SH for bull).
//     Fix: extend the balanced-check in RunScanning:
//       if (legBear && Low[ba]  <= e.Price) { balanced = true; break; }
//       if (legBull && High[ba] >= e.Price) { balanced = true; break; }
//
// v9: added freshness guard (CurrentBar - e.BarIdx > Sweep1Timeout)
// v8: removed Daily series, fix Highs[2][1] OOB crash
// v7: eliminated Highs[1][n] variable-index access (local list fix)
// v6: used BarsArray[1].Count — total history, not replay position
// v5: removed EQ balance check from WaitingSweep1
// v4: double sweep, CISD-only entry, Bias Option C

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
    public class ICTV10 : Strategy
    {
        private const string StrategyVersion = "v10";

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
        [Display(Name="Sweep1 Timeout (Bars)",         Order=1, GroupName="Entry Rules")]
        public int Sweep1Timeout { get; set; }

        [NinjaScriptProperty][Range(1,50)]
        [Display(Name="CISD Window (Bars after LL2)",  Order=2, GroupName="Entry Rules")]
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

        // Sweeps
        private int    sweep1Bar;   private double sweep1Extreme;
        private int    sweep2Bar;   private double sweep2Extreme;
        private double cisdLevel;

        // ─── Bias ──────────────────────────────────────────────────────────

        private bool   biasIsBull;
        private bool   biasAvailable;

        // 4H data — stored locally to avoid Highs[1][n] variable-index access
        private List<double> bias4hHighs, bias4hLows;
        private List<int>    biasShBars,  biasSlBars;
        private List<double> biasShPrices, biasSlPrices;
        private string struct4h;

        // Prev-Day H/L — tracked via date change in exec TF (no Daily series)
        private double prevDayHigh, prevDayLow;
        private bool   prevDaySet;
        private double todayHigh, todayLow;
        private DateTime currentDate;

        // Exec TF swing lists
        private List<int>    execShBars,   execSlBars;
        private List<double> execShPrices, execSlPrices;

        // ─── Lifecycle ─────────────────────────────────────────────────────

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                         = "ICTV10";
                Description                  = "ICT Double Sweep + CISD — NQ Futures v10";
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
                AddDataSeries(BarsPeriodType.Minute, BiasTFMinutes); // series 1 = 4H only
            }
            else if (State == State.DataLoaded)
            {
                setupState   = S.Scanning;
                struct4h     = "none";
                prevDaySet   = false;
                prevDayHigh  = double.MaxValue;
                prevDayLow   = double.MinValue;
                todayHigh    = double.MinValue;
                todayLow     = double.MaxValue;
                currentDate  = DateTime.MinValue;
                biasAvailable = false;

                bias4hHighs  = new List<double>(); bias4hLows   = new List<double>();
                biasShBars   = new List<int>();    biasShPrices = new List<double>();
                biasSlBars   = new List<int>();    biasSlPrices = new List<double>();
                execShBars   = new List<int>();    execShPrices = new List<double>();
                execSlBars   = new List<int>();    execSlPrices = new List<double>();

                Print("================================================");
                Print("  ICT-V10 loaded — ICT Double Sweep + CISD");
                Print("  Bias: 4H Structure + Prev Day H/L (Option C)");
                Print("================================================");
                D("Waiting for bias data ...");
            }
        }

        // ─── Bar update ────────────────────────────────────────────────────

        protected override void OnBarUpdate()
        {
            // ── Series 1: 4H — append to local lists, detect swings ────────
            if (BarsInProgress == 1)
            {
                // Only Highs[1][0] / Lows[1][0] used (index 0 = always valid)
                bias4hHighs.Add(Highs[1][0]);
                bias4hLows.Add(Lows[1][0]);

                int n = bias4hHighs.Count;
                if (n < BiasSwingStrength * 2 + 1) return;

                Update4HSwings(n);
                Update4HStructure();
                return;
            }

            // ── Series 0: Execution TF ─────────────────────────────────────
            if (BarsInProgress != 0) return;
            int minWarmup = Math.Max(SwingStrength * 2, MinLegBars) + 10;
            if (CurrentBar < minWarmup) return;

            UpdatePrevDayHL();   // date-change tracking — no secondary series
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

        // ─── Prev-Day H/L via date change (no Daily series) ────────────────

        private void UpdatePrevDayHL()
        {
            DateTime barDate = Time[0].Date;

            if (barDate != currentDate)
            {
                // New day started — promote today's range to prev day
                if (currentDate != DateTime.MinValue && todayHigh > double.MinValue)
                {
                    prevDayHigh = todayHigh;
                    prevDayLow  = todayLow;
                    prevDaySet  = true;
                    D($"  [DAY] New day {barDate:MM-dd}. Prev day H={prevDayHigh:F2} L={prevDayLow:F2}");
                }
                currentDate = barDate;
                todayHigh   = High[0];
                todayLow    = Low[0];
            }
            else
            {
                if (High[0] > todayHigh) todayHigh = High[0];
                if (Low[0]  < todayLow)  todayLow  = Low[0];
            }
        }

        // ─── Bias: 4H Market Structure (uses local lists) ───────────────────

        private void Update4HSwings(int n)
        {
            int swingN = BiasSwingStrength;

            if (IsSwingHighInList(bias4hHighs, n, swingN))
            {
                int bar = n - 1 - swingN;
                if (biasShBars.Count == 0 || biasShBars[biasShBars.Count - 1] != bar)
                {
                    biasShBars.Add(bar);
                    biasShPrices.Add(bias4hHighs[bar]);
                    D($"  [4H] Swing HIGH idx={bar} price={bias4hHighs[bar]:F2}");
                }
            }
            if (IsSwingLowInList(bias4hLows, n, swingN))
            {
                int bar = n - 1 - swingN;
                if (biasSlBars.Count == 0 || biasSlBars[biasSlBars.Count - 1] != bar)
                {
                    biasSlBars.Add(bar);
                    biasSlPrices.Add(bias4hLows[bar]);
                    D($"  [4H] Swing LOW  idx={bar} price={bias4hLows[bar]:F2}");
                }
            }
        }

        private bool IsSwingHighInList(List<double> data, int count, int n)
        {
            int center = count - 1 - n;
            if (center - n < 0 || center + n >= count) return false;
            double r = data[center];
            for (int i = 1; i <= n; i++)
                if (data[center - i] >= r || data[center + i] >= r) return false;
            return true;
        }

        private bool IsSwingLowInList(List<double> data, int count, int n)
        {
            int center = count - 1 - n;
            if (center - n < 0 || center + n >= count) return false;
            double r = data[center];
            for (int i = 1; i <= n; i++)
                if (data[center - i] <= r || data[center + i] <= r) return false;
            return true;
        }

        private void Update4HStructure()
        {
            if (biasShBars.Count < 2 || biasSlBars.Count < 2) return;

            double lastSH = biasShPrices[biasShPrices.Count - 1];
            double prevSH = biasShPrices[biasShPrices.Count - 2];
            double lastSL = biasSlPrices[biasSlPrices.Count - 1];
            double prevSL = biasSlPrices[biasSlPrices.Count - 2];

            string prev = struct4h;

            if      (lastSH > prevSH && lastSL > prevSL) struct4h = "bull";
            else if (lastSH < prevSH && lastSL < prevSL) struct4h = "bear";
            else                                          struct4h = "none";

            if (struct4h != prev)
                D($"  [4H] Structure -> {struct4h} (SH: {prevSH:F0}->{lastSH:F0}  SL: {prevSL:F0}->{lastSL:F0})");
        }

        private void UpdateCombinedBias()
        {
            if (!prevDaySet || struct4h == "none") { biasAvailable = false; return; }

            bool newBull = (struct4h == "bull") && (Close[0] > prevDayHigh);
            bool newBear = (struct4h == "bear") && (Close[0] < prevDayLow);

            if (!newBull && !newBear) { biasAvailable = false; return; }

            bool prev  = biasIsBull;
            biasIsBull    = newBull;
            biasAvailable = true;

            if (biasIsBull != prev)
                D($"Bar {CurrentBar}: [BIAS] -> {(biasIsBull ? "BULL" : "BEAR")} " +
                  $"(4H={struct4h} price={Close[0]:F2} prevDH={prevDayHigh:F2} prevDL={prevDayLow:F2})");
        }

        // ─── Exec TF swing detection ─────────────────────────────────────────

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

        // ─── State machine ───────────────────────────────────────────────────

        private void RunScanning()
        {
            var merged = BuildMergedSwings(execShBars, execShPrices, execSlBars, execSlPrices);
            if (merged.Count < 2) return;

            for (int i = merged.Count - 2; i >= 0; i--)
            {
                var s = merged[i];
                var e = merged[i + 1];
                if (e.BarIdx - s.BarIdx < MinLegBars) continue;

                // v9 fix: skip legs whose end is already beyond the timeout window.
                // Without this, the scan locks onto a stale leg on every bar:
                //   Scan → WaitingSweep1 → immediate Timeout → Scan → repeat.
                if (CurrentBar - e.BarIdx > Sweep1Timeout) continue;

                bool legBull = s.IsLow && !e.IsLow;
                bool legBear = !s.IsLow && e.IsLow;
                if (!legBull && !legBear) continue;
                if (legBull != biasIsBull) continue;

                double eq = (s.Price + e.Price) / 2.0;

                bool balanced = false;
                for (int k = 1; (e.BarIdx + k) <= CurrentBar; k++)
                {
                    int ba = CurrentBar - (e.BarIdx + k);
                    if (ba < 0) break;
                    if (legBull && Low[ba]  <= eq)      { balanced = true; break; } // retraced to EQ
                    if (legBull && High[ba] >= e.Price) { balanced = true; break; } // broke above SH top
                    if (legBear && High[ba] >= eq)      { balanced = true; break; } // retraced to EQ
                    if (legBear && Low[ba]  <= e.Price) { balanced = true; break; } // broke below SL bottom
                }
                if (balanced) continue;

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

            if (CurrentBar - legEndBar > Sweep1Timeout)
            { D($"Bar {CurrentBar}: [S1] Timeout — reset."); setupState = S.Scanning; return; }

            if (legIsBull  && High[0] > legEndPrice)
            { D($"Bar {CurrentBar}: [S1] Price above leg top — reset."); setupState = S.Scanning; return; }
            if (!legIsBull && Low[0]  < legEndPrice)
            { D($"Bar {CurrentBar}: [S1] Price below leg bottom — reset."); setupState = S.Scanning; return; }

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
            bool swept2 = legIsBull ? Low[0] < sweep1Extreme : High[0] > sweep1Extreme;
            if (swept2)
            {
                sweep2Bar     = CurrentBar;
                sweep2Extreme = legIsBull ? Low[0] : High[0];
                cisdLevel     = FindReactionLevel(sweep1Bar, sweep2Bar, legIsBull);
                setupState    = S.WaitingCISD;
                D($"Bar {CurrentBar}: [S2] LL1 swept → LL2={sweep2Extreme:F2}. CISD level={cisdLevel:F2}");
                return;
            }

            if (legIsBull  && High[0] > legEndPrice)
            { D($"Bar {CurrentBar}: [S2] Price above leg top — reset."); setupState = S.Scanning; }
            if (!legIsBull && Low[0]  < legEndPrice)
            { D($"Bar {CurrentBar}: [S2] Price below leg bottom — reset."); setupState = S.Scanning; }
        }

        private void RunWaitingCISD()
        {
            if (CurrentBar - sweep2Bar > CisdWindow)
            { D($"Bar {CurrentBar}: [CISD] Timeout — reset."); setupState = S.Scanning; return; }

            bool cisdBull = legIsBull  && Close[0] > cisdLevel;
            bool cisdBear = !legIsBull && Close[0] < cisdLevel;
            if (!cisdBull && !cisdBear) return;

            double entry = Close[0];
            double sl, tp;

            if (legIsBull)
            {
                sl = sweep2Extreme - SlBufferTicks * TickSize;
                tp = legEndPrice;
                if (tp <= entry)
                { D($"Bar {CurrentBar}: [CISD] TP {tp:F2} <= Entry {entry:F2} — reset."); setupState = S.Scanning; return; }
            }
            else
            {
                sl = sweep2Extreme + SlBufferTicks * TickSize;
                tp = legEndPrice;
                if (tp >= entry)
                { D($"Bar {CurrentBar}: [CISD] TP {tp:F2} >= Entry {entry:F2} — reset."); setupState = S.Scanning; return; }
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

        // ─── Helpers ─────────────────────────────────────────────────────────

        private double FindReactionLevel(int s1Bar, int s2Bar, bool isBull)
        {
            double level = isBull ? double.MinValue : double.MaxValue;
            for (int abs = s1Bar + 1; abs < s2Bar; abs++)
            {
                int barsAgo = CurrentBar - abs;
                if (barsAgo < 0 || barsAgo >= CurrentBar) continue;
                if (isBull  && High[barsAgo] > level) level = High[barsAgo];
                if (!isBull && Low[barsAgo]  < level) level = Low[barsAgo];
            }
            if (isBull  && level == double.MinValue) level = High[CurrentBar - s1Bar];
            if (!isBull && level == double.MaxValue) level = Low[CurrentBar - s1Bar];
            return level;
        }

        private double FindExterneLevel(int legStartAbs, int legEndAbs, bool isBull, double eq)
        {
            for (int abs = legStartAbs + ExtSwingStrength; abs <= legEndAbs - ExtSwingStrength; abs++)
            {
                int ba = CurrentBar - abs;
                if (ba < 0 || ba >= CurrentBar) continue;
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
