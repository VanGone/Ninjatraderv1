// NinjatraderV1.cs  — v2
// ICT Price Leg Strategy for NQ Futures — NinjaTrader 8
//
// Fixes vs v1:
//   - MaximumBarsLookBack = Infinite (prevents out-of-range on deep lookbacks)
//   - Bias bar count now uses CurrentBar (which is series-relative in BarsInProgress==1 context)
//   - All series accesses guarded with explicit bounds checks
//   - Debug output to NinjaTrader Output window (enable via parameter)

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
    public class NinjatraderV1 : Strategy
    {
        // ─── Parameters ────────────────────────────────────────────────────

        [NinjaScriptProperty][Range(1,20)]
        [Display(Name="Swing Strength (Exec TF)",     Order=1, GroupName="Leg Detection")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty][Range(1,10)]
        [Display(Name="Externes Level Swing Strength",Order=2, GroupName="Leg Detection")]
        public int ExtSwingStrength { get; set; }

        [NinjaScriptProperty][Range(5,500)]
        [Display(Name="Min Leg Bars (Exec TF)",       Order=3, GroupName="Leg Detection")]
        public int MinLegBars { get; set; }

        [NinjaScriptProperty][Range(3,50)]
        [Display(Name="Min Leg Bars (Bias TF)",       Order=4, GroupName="Leg Detection")]
        public int BiasMinLegBars { get; set; }

        [NinjaScriptProperty][Range(1,20)]
        [Display(Name="Bias Swing Strength",          Order=5, GroupName="Leg Detection")]
        public int BiasSwingStrength { get; set; }

        [NinjaScriptProperty][Range(60,1440)]
        [Display(Name="Bias Timeframe (Minutes)",     Order=6, GroupName="Leg Detection",
            Description="240 = 4H")]
        public int BiasTFMinutes { get; set; }

        [NinjaScriptProperty][Range(1,30)]
        [Display(Name="CISD / FVG Window (Bars)",     Order=1, GroupName="Entry Rules")]
        public int CisdFvgWindow { get; set; }

        [NinjaScriptProperty][Range(1,10)]
        [Display(Name="FVG Inverse Window (Bars)",    Order=2, GroupName="Entry Rules")]
        public int FvgInverseWindow { get; set; }

        [NinjaScriptProperty][Range(0,100)]
        [Display(Name="SL Buffer (Ticks)",            Order=3, GroupName="Entry Rules")]
        public int SlBufferTicks { get; set; }

        [NinjaScriptProperty][Range(0.1,10.0)]
        [Display(Name="Risk Per Trade (%)",           Order=4, GroupName="Entry Rules")]
        public double RiskPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Debug Mode",                   Order=1, GroupName="Debug",
            Description="Print strategy state to NinjaTrader Output window")]
        public bool DebugMode { get; set; }

        // ─── State machine ─────────────────────────────────────────────────

        private enum S { Scanning, WaitingSweep, WaitingFVG, WaitingCISD }
        private S setupState;

        // Active leg
        private double legStartPrice, legEndPrice, legEq;
        private int    legStartBar,   legEndBar;
        private bool   legIsBull;
        private double activeExtLevel;

        // Sweep
        private int    sweepBar;
        private double sweepExtreme;

        // FVG
        private double fvgTop, fvgBottom;
        private int    fvgFoundBar;

        // Bias
        private bool biasIsBull;
        private bool biasAvailable;

        // Swing lists — exec TF (absolute CurrentBar values)
        private List<int>    execShBars,   execSlBars;
        private List<double> execShPrices, execSlPrices;

        // Swing lists — bias TF (absolute CurrentBar values when BarsInProgress==1)
        private List<int>    biasShBars,   biasSlBars;
        private List<double> biasShPrices, biasSlPrices;

        // ─── Lifecycle ─────────────────────────────────────────────────────

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                         = "NinjatraderV1";
                Description                  = "ICT Price Leg Strategy v2 — NQ Futures";
                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                MaximumBarsLookBack          = int.MaxValue; // needed for deep leg lookbacks

                SwingStrength     = 3;
                ExtSwingStrength  = 2;
                MinLegBars        = 25;
                BiasMinLegBars    = 8;
                BiasSwingStrength = 3;
                BiasTFMinutes     = 240;
                CisdFvgWindow     = 10;
                FvgInverseWindow  = 4;
                SlBufferTicks     = 20;
                RiskPercent       = 1.0;
                DebugMode         = true;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, BiasTFMinutes);
            }
            else if (State == State.DataLoaded)
            {
                setupState = S.Scanning;

                execShBars   = new List<int>(); execShPrices = new List<double>();
                execSlBars   = new List<int>(); execSlPrices = new List<double>();
                biasShBars   = new List<int>(); biasShPrices = new List<double>();
                biasSlBars   = new List<int>(); biasSlPrices = new List<double>();

                biasAvailable = false;
                D("Strategy DataLoaded — waiting for bias bars.");
            }
        }

        // ─── Bar update ────────────────────────────────────────────────────

        protected override void OnBarUpdate()
        {
            // ── Bias TF ────────────────────────────────────────────────────
            if (BarsInProgress == 1)
            {
                // CurrentBar here is the 4H series bar index
                if (CurrentBar < BiasSwingStrength * 2) return;
                UpdateBiasSwings();   // detect 4H swing points
                UpdateBias();         // re-derive current bias direction
                return;
            }

            // ── Primary TF (exec) ──────────────────────────────────────────
            if (BarsInProgress != 0) return;

            int minWarmup = Math.Max(SwingStrength * 2, MinLegBars) + 10;
            if (CurrentBar < minWarmup) return;

            UpdateExecSwings();

            if (!biasAvailable)
            {
                if (DebugMode && CurrentBar % 500 == 0)
                    D($"Bar {CurrentBar}: bias not yet available");
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat) return;

            switch (setupState)
            {
                case S.Scanning:     RunScanning();     break;
                case S.WaitingSweep: RunWaitingSweep(); break;
                case S.WaitingFVG:   RunWaitingFVG();   break;
                case S.WaitingCISD:  RunWaitingCISD();  break;
            }
        }

        // ─── Swing detection ───────────────────────────────────────────────

        private void UpdateExecSwings()
        {
            if (CurrentBar < SwingStrength * 2) return;

            if (ExecIsSwingHigh(SwingStrength))
            {
                int bar = CurrentBar - SwingStrength;
                if (execShBars.Count == 0 || execShBars[execShBars.Count - 1] != bar)
                {
                    execShBars.Add(bar);
                    execShPrices.Add(High[SwingStrength]);
                    D($"  [EXEC] Swing HIGH @ bar {bar} price {High[SwingStrength]:F2}");
                }
            }
            if (ExecIsSwingLow(SwingStrength))
            {
                int bar = CurrentBar - SwingStrength;
                if (execSlBars.Count == 0 || execSlBars[execSlBars.Count - 1] != bar)
                {
                    execSlBars.Add(bar);
                    execSlPrices.Add(Low[SwingStrength]);
                    D($"  [EXEC] Swing LOW  @ bar {bar} price {Low[SwingStrength]:F2}");
                }
            }
        }

        // Primary series swing helpers (use High[] / Low[])
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

        private void UpdateBiasSwings()
        {
            // CurrentBar = 4H bar index in this context (BarsInProgress==1)
            if (CurrentBar < BiasSwingStrength * 2) return;

            if (BiasIsSwingHigh(BiasSwingStrength))
            {
                int bar = CurrentBar - BiasSwingStrength;
                if (biasShBars.Count == 0 || biasShBars[biasShBars.Count - 1] != bar)
                {
                    biasShBars.Add(bar);
                    biasShPrices.Add(Highs[1][BiasSwingStrength]);
                    D($"  [BIAS] Swing HIGH @ 4H bar {bar} price {Highs[1][BiasSwingStrength]:F2}");
                }
            }
            if (BiasIsSwingLow(BiasSwingStrength))
            {
                int bar = CurrentBar - BiasSwingStrength;
                if (biasSlBars.Count == 0 || biasSlBars[biasSlBars.Count - 1] != bar)
                {
                    biasSlBars.Add(bar);
                    biasSlPrices.Add(Lows[1][BiasSwingStrength]);
                    D($"  [BIAS] Swing LOW  @ 4H bar {bar} price {Lows[1][BiasSwingStrength]:F2}");
                }
            }
        }

        // Bias series swing helpers (use Highs[1][] / Lows[1][])
        // MUST only be called when BarsInProgress==1 and CurrentBar >= n*2
        private bool BiasIsSwingHigh(int n)
        {
            if (CurrentBar < n * 2) return false;
            double r = Highs[1][n];
            for (int i = 1; i <= n; i++)
                if (Highs[1][n - i] >= r || Highs[1][n + i] >= r) return false;
            return true;
        }
        private bool BiasIsSwingLow(int n)
        {
            if (CurrentBar < n * 2) return false;
            double r = Lows[1][n];
            for (int i = 1; i <= n; i++)
                if (Lows[1][n - i] <= r || Lows[1][n + i] <= r) return false;
            return true;
        }

        // ─── Bias ──────────────────────────────────────────────────────────

        private void UpdateBias()
        {
            // BarsInProgress==1 context: CurrentBar = 4H bar index
            int cur4H = CurrentBar;

            var merged = BuildMergedSwings(biasShBars, biasShPrices, biasSlBars, biasSlPrices);
            if (merged.Count < 2) return;

            for (int i = merged.Count - 2; i >= 0; i--)
            {
                var s = merged[i];
                var e = merged[i + 1];
                if (e.BarIdx - s.BarIdx < BiasMinLegBars) continue;

                bool isBull = s.IsLow && !e.IsLow;
                bool isBear = !s.IsLow && e.IsLow;
                if (!isBull && !isBear) continue;

                double eq = (s.Price + e.Price) / 2.0;

                // Check if balanced: did price revisit 50% after leg end?
                bool balanced = false;
                // k=1 means 1 bar after legEnd; loop while index stays in range
                for (int k = 1; (e.BarIdx + k) <= cur4H; k++)
                {
                    int barsAgo = cur4H - (e.BarIdx + k); // guaranteed >= 0
                    if (isBull && Lows[1][barsAgo]  <= eq) { balanced = true; break; }
                    if (isBear && Highs[1][barsAgo] >= eq) { balanced = true; break; }
                }

                // Use this leg (unbalanced = ideal; balanced = fallback)
                biasIsBull    = isBull;
                biasAvailable = true;

                D($"  [BIAS] Leg {(isBull?"BULL":"BEAR")} {s.Price:F0}->{e.Price:F0} EQ={eq:F0} balanced={balanced}");

                if (!balanced) break; // unbalanced found — stop here
            }
        }

        // ─── State machine ─────────────────────────────────────────────────

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

                // Must align with bias
                if (legBull != biasIsBull) continue;

                double eq = (s.Price + e.Price) / 2.0;

                // Check if balanced already
                bool balanced = false;
                for (int k = 1; (e.BarIdx + k) <= CurrentBar; k++)
                {
                    int barsAgo = CurrentBar - (e.BarIdx + k);
                    if (barsAgo < 0) break;
                    if (legBull && Low[barsAgo]  <= eq) { balanced = true; break; }
                    if (legBear && High[barsAgo] >= eq) { balanced = true; break; }
                }
                if (balanced) continue;

                // Find externe level inside the leg
                double extLevel = FindExterneLevel(s.BarIdx, e.BarIdx, legBull, eq);
                if (double.IsNaN(extLevel)) continue;

                // Activate setup
                legStartPrice  = s.Price;
                legEndPrice    = e.Price;
                legEq          = eq;
                legStartBar    = s.BarIdx;
                legEndBar      = e.BarIdx;
                legIsBull      = legBull;
                activeExtLevel = extLevel;
                setupState     = S.WaitingSweep;

                D($"Bar {CurrentBar}: [SCAN] Leg found {(legBull?"BULL":"BEAR")} " +
                  $"{s.Price:F0}->{e.Price:F0} EQ={eq:F0} | ExtLevel={extLevel:F2}");
                break;
            }
        }

        private void RunWaitingSweep()
        {
            if (CurrentBar <= legEndBar) return;

            // Invalidate if leg is now balanced
            for (int k = 1; (legEndBar + k) < CurrentBar; k++)
            {
                int barsAgo = CurrentBar - (legEndBar + k);
                if (barsAgo <= 0) break;
                if (legIsBull  && Low[barsAgo]  <= legEq) { D($"Bar {CurrentBar}: [SWEEP] Leg balanced — reset."); setupState = S.Scanning; return; }
                if (!legIsBull && High[barsAgo] >= legEq) { D($"Bar {CurrentBar}: [SWEEP] Leg balanced — reset."); setupState = S.Scanning; return; }
            }

            bool swept = legIsBull ? Low[0] < activeExtLevel : High[0] > activeExtLevel;
            if (swept)
            {
                sweepBar     = CurrentBar;
                sweepExtreme = legIsBull ? Low[0] : High[0];
                setupState   = S.WaitingFVG;
                D($"Bar {CurrentBar}: [SWEEP] ExtLevel {activeExtLevel:F2} swept! extreme={sweepExtreme:F2}");
            }
        }

        private void RunWaitingFVG()
        {
            int barsSinceSweep = CurrentBar - sweepBar;
            if (barsSinceSweep > CisdFvgWindow)
            {
                D($"Bar {CurrentBar}: [FVG] Timeout after {barsSinceSweep} bars — reset.");
                setupState = S.Scanning;
                return;
            }

            if (CurrentBar < sweepBar + 2) return;

            // Bullish FVG: High[2] < Low[0]
            // Bearish FVG: Low[2]  > High[0]
            if (legIsBull && High[2] < Low[0])
            {
                fvgBottom   = High[2];
                fvgTop      = Low[0];
                fvgFoundBar = CurrentBar;
                setupState  = S.WaitingCISD;
                D($"Bar {CurrentBar}: [FVG] Bullish iFVG found: {fvgBottom:F2}-{fvgTop:F2}");
            }
            else if (!legIsBull && Low[2] > High[0])
            {
                fvgTop      = Low[2];
                fvgBottom   = High[0];
                fvgFoundBar = CurrentBar;
                setupState  = S.WaitingCISD;
                D($"Bar {CurrentBar}: [FVG] Bearish iFVG found: {fvgBottom:F2}-{fvgTop:F2}");
            }
        }

        private void RunWaitingCISD()
        {
            if (CurrentBar - fvgFoundBar > FvgInverseWindow)
            {
                D($"Bar {CurrentBar}: [CISD] Timeout — back to WaitingFVG.");
                setupState = S.WaitingFVG;
                return;
            }

            bool cisdBull = legIsBull  && Low[0] <= fvgTop    && Close[0] > fvgTop;
            bool cisdBear = !legIsBull && High[0] >= fvgBottom && Close[0] < fvgBottom;

            if (!cisdBull && !cisdBear) return;

            double entry = Close[0];
            double sl, tp;

            if (legIsBull)
            {
                sl = sweepExtreme - SlBufferTicks * TickSize;
                tp = legEndPrice;
                if (tp <= entry) { D($"Bar {CurrentBar}: [CISD] TP <= Entry — skip."); setupState = S.Scanning; return; }
            }
            else
            {
                sl = sweepExtreme + SlBufferTicks * TickSize;
                tp = legEndPrice;
                if (tp >= entry) { D($"Bar {CurrentBar}: [CISD] TP >= Entry (short) — skip."); setupState = S.Scanning; return; }
            }

            int qty = CalcContracts(entry, sl);
            if (qty < 1) qty = 1;

            SetStopLoss  ("ICT", CalculationMode.Price, sl, false);
            SetProfitTarget("ICT", CalculationMode.Price, tp);

            if (legIsBull)
            {
                EnterLong(qty, "ICT");
                D($"Bar {CurrentBar}: *** ENTER LONG {qty}ct @ {entry:F2}  SL={sl:F2}  TP={tp:F2}  R={(tp-entry)/(entry-sl):F1}R ***");
            }
            else
            {
                EnterShort(qty, "ICT");
                D($"Bar {CurrentBar}: *** ENTER SHORT {qty}ct @ {entry:F2}  SL={sl:F2}  TP={tp:F2}  R={(entry-tp)/(sl-entry):F1}R ***");
            }

            setupState = S.Scanning;
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        private double FindExterneLevel(int legStartAbs, int legEndAbs, bool isBull, double eq)
        {
            // Scan bars inside the leg for swing lows (bull) / highs (bear) in discount/premium
            for (int abs = legStartAbs + ExtSwingStrength; abs <= legEndAbs - ExtSwingStrength; abs++)
            {
                int barsAgo = CurrentBar - abs;
                if (barsAgo < 0 || barsAgo > CurrentBar) continue;

                if (isBull)
                {
                    if (Low[barsAgo] >= eq) continue; // must be in discount
                    if (ExecIsSwingLowAt(barsAgo, ExtSwingStrength))
                        return Low[barsAgo];
                }
                else
                {
                    if (High[barsAgo] <= eq) continue; // must be in premium
                    if (ExecIsSwingHighAt(barsAgo, ExtSwingStrength))
                        return High[barsAgo];
                }
            }
            return double.NaN;
        }

        private bool ExecIsSwingHighAt(int barsAgo, int n)
        {
            double r = High[barsAgo];
            for (int i = 1; i <= n; i++)
            {
                int older  = barsAgo + i;
                int newer  = barsAgo - i;
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
                int older = barsAgo + i;
                int newer = barsAgo - i;
                if (newer < 0 || older > CurrentBar) return false;
                if (Low[newer] <= r || Low[older] <= r) return false;
            }
            return true;
        }

        private List<SwingPoint> BuildMergedSwings(
            List<int> shBars, List<double> shPrices,
            List<int> slBars, List<double> slPrices)
        {
            var all = new List<SwingPoint>();
            for (int i = 0; i < shBars.Count; i++)
                all.Add(new SwingPoint(shBars[i], shPrices[i], false));
            for (int i = 0; i < slBars.Count; i++)
                all.Add(new SwingPoint(slBars[i], slPrices[i], true));
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

        // Debug helper — only prints when DebugMode is enabled
        private void D(string msg)
        {
            if (DebugMode) Print(msg);
        }

        // ─── Nested struct ─────────────────────────────────────────────────

        private struct SwingPoint
        {
            public int BarIdx; public double Price; public bool IsLow;
            public SwingPoint(int b, double p, bool l) { BarIdx=b; Price=p; IsLow=l; }
        }
    }
}
