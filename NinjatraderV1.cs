// NinjatraderV1.cs
// ICT Price Leg Strategy for NQ Futures — NinjaTrader 8
//
// Logic:
//   1. 4H bias  : Last unbalanced price leg (balanced = 50% EQ revisited)
//   2. 1M setup : Externes Low sweep -> Inverse FVG -> CISD close -> Long entry
//
// How to install:
//   Tools > Import > NinjaScript Add-On > select this file
//   OR: copy to Documents\NinjaTrader 8\bin\Custom\Strategies\

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

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Swing Strength (Exec TF)", Order = 1, GroupName = "Leg Detection")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Externes Level Swing Strength", Order = 2, GroupName = "Leg Detection")]
        public int ExtSwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Min Leg Bars (Exec TF)", Order = 3, GroupName = "Leg Detection")]
        public int MinLegBars { get; set; }

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "Min Leg Bars (Bias TF)", Order = 4, GroupName = "Leg Detection")]
        public int BiasMinLegBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Bias Swing Strength", Order = 5, GroupName = "Leg Detection")]
        public int BiasSwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(240, 240)]
        [Display(Name = "Bias Timeframe (Minutes)", Order = 6, GroupName = "Leg Detection",
            Description = "Higher timeframe for bias. Default: 240 = 4H")]
        public int BiasTFMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "CISD / FVG Window (Bars)", Order = 1, GroupName = "Entry Rules")]
        public int CisdFvgWindow { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "FVG Inverse Window (Bars)", Order = 2, GroupName = "Entry Rules")]
        public int FvgInverseWindow { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "SL Buffer (Ticks)", Order = 3, GroupName = "Entry Rules")]
        public int SlBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Risk Per Trade (%)", Order = 4, GroupName = "Entry Rules")]
        public double RiskPercent { get; set; }

        // ─── State machine ─────────────────────────────────────────────────

        private enum State4 { Scanning, WaitingSweep, WaitingFVG, WaitingCISD }
        private State4 setupState;

        // Active leg info
        private double legStartPrice, legEndPrice, legEq;
        private int    legStartBar,   legEndBar;
        private bool   legIsBull;

        // Externes level
        private double activeExtLevel;

        // Sweep info
        private int    sweepBar;
        private double sweepExtreme;   // low (bull) or high (bear) of sweep bar

        // FVG info
        private double fvgTop, fvgBottom;
        private int    fvgFoundBar;

        // Current bias from 4H
        private bool   biasIsBull;
        private bool   biasAvailable;

        // Swing point lists — exec TF (absolute CurrentBar indices)
        private List<int>    execShBars,   execSlBars;
        private List<double> execShPrices, execSlPrices;

        // Swing point lists — bias TF (absolute BarsArray[1] bar indices)
        private List<int>    biasShBars,   biasSlBars;
        private List<double> biasShPrices, biasSlPrices;

        // ─── Lifecycle ─────────────────────────────────────────────────────

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                        = "NinjatraderV1";
                Description                 = "ICT Price Leg Strategy — NQ Futures\n" +
                                              "4H Bias + Externes Low Sweep + iFVG + CISD";
                Calculate                   = Calculate.OnBarClose;
                EntriesPerDirection         = 1;
                EntryHandling               = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds   = 30;

                // Defaults
                SwingStrength    = 3;
                ExtSwingStrength = 2;
                MinLegBars       = 25;
                BiasMinLegBars   = 8;
                BiasSwingStrength = 3;
                BiasTFMinutes    = 240;
                CisdFvgWindow    = 10;
                FvgInverseWindow = 4;
                SlBufferTicks    = 20;
                RiskPercent      = 1.0;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, BiasTFMinutes);
            }
            else if (State == State.DataLoaded)
            {
                setupState = State4.Scanning;

                execShBars   = new List<int>();
                execShPrices = new List<double>();
                execSlBars   = new List<int>();
                execSlPrices = new List<double>();

                biasShBars   = new List<int>();
                biasShPrices = new List<double>();
                biasSlBars   = new List<int>();
                biasSlPrices = new List<double>();

                biasAvailable = false;
            }
        }

        // ─── Bar update ────────────────────────────────────────────────────

        protected override void OnBarUpdate()
        {
            // ── Bias TF: track swings ──────────────────────────────────────
            if (BarsInProgress == 1)
            {
                UpdateBiasSwings();
                UpdateBias();
                return;
            }

            // ── Exec TF only below ─────────────────────────────────────────
            if (BarsInProgress != 0) return;
            if (CurrentBar < Math.Max(SwingStrength * 2, MinLegBars) + 10) return;

            UpdateExecSwings();

            if (!biasAvailable) return;

            // Manage open position: NT handles SL/TP automatically via SetStopLoss/SetProfitTarget
            // Just run the setup state machine when flat
            if (Position.MarketPosition != MarketPosition.Flat) return;

            switch (setupState)
            {
                case State4.Scanning:      RunScanning();      break;
                case State4.WaitingSweep:  RunWaitingSweep();  break;
                case State4.WaitingFVG:    RunWaitingFVG();    break;
                case State4.WaitingCISD:   RunWaitingCISD();   break;
            }
        }

        // ─── Swing detection ───────────────────────────────────────────────

        private void UpdateExecSwings()
        {
            if (CurrentBar < SwingStrength * 2) return;

            if (IsSwingHighExec(SwingStrength))
            {
                int bar = CurrentBar - SwingStrength;
                if (execShBars.Count == 0 || execShBars[execShBars.Count - 1] != bar)
                {
                    execShBars.Add(bar);
                    execShPrices.Add(High[SwingStrength]);
                }
            }
            if (IsSwingLowExec(SwingStrength))
            {
                int bar = CurrentBar - SwingStrength;
                if (execSlBars.Count == 0 || execSlBars[execSlBars.Count - 1] != bar)
                {
                    execSlBars.Add(bar);
                    execSlPrices.Add(Low[SwingStrength]);
                }
            }
        }

        private bool IsSwingHighExec(int n)
        {
            double refH = High[n];
            for (int i = 1; i <= n; i++)
                if (High[n - i] >= refH || High[n + i] >= refH) return false;
            return true;
        }

        private bool IsSwingLowExec(int n)
        {
            double refL = Low[n];
            for (int i = 1; i <= n; i++)
                if (Low[n - i] <= refL || Low[n + i] <= refL) return false;
            return true;
        }

        private void UpdateBiasSwings()
        {
            if (BarsArray[1].Count < BiasSwingStrength * 2 + 1) return;

            if (IsBiasSwingHigh(BiasSwingStrength))
            {
                int bar = BarsArray[1].Count - 1 - BiasSwingStrength;
                if (biasShBars.Count == 0 || biasShBars[biasShBars.Count - 1] != bar)
                {
                    biasShBars.Add(bar);
                    biasShPrices.Add(Highs[1][BiasSwingStrength]);
                }
            }
            if (IsBiasSwingLow(BiasSwingStrength))
            {
                int bar = BarsArray[1].Count - 1 - BiasSwingStrength;
                if (biasSlBars.Count == 0 || biasSlBars[biasSlBars.Count - 1] != bar)
                {
                    biasSlBars.Add(bar);
                    biasSlPrices.Add(Lows[1][BiasSwingStrength]);
                }
            }
        }

        private bool IsBiasSwingHigh(int n)
        {
            double refH = Highs[1][n];
            for (int i = 1; i <= n; i++)
                if (Highs[1][n - i] >= refH || Highs[1][n + i] >= refH) return false;
            return true;
        }

        private bool IsBiasSwingLow(int n)
        {
            double refL = Lows[1][n];
            for (int i = 1; i <= n; i++)
                if (Lows[1][n - i] <= refL || Lows[1][n + i] <= refL) return false;
            return true;
        }

        // ─── Bias detection ────────────────────────────────────────────────

        private void UpdateBias()
        {
            // Build merged + cleaned swing sequence from 4H data
            var merged = BuildMergedSwings(biasShBars, biasShPrices, biasSlBars, biasSlPrices);
            if (merged.Count < 2) return;

            // Find last unbalanced leg (or fallback to last leg)
            int totalBias4hBars = BarsArray[1].Count;

            for (int i = merged.Count - 2; i >= 0; i--)
            {
                var s = merged[i];
                var e = merged[i + 1];
                if (e.BarIdx - s.BarIdx < BiasMinLegBars) continue;

                bool isBull = (s.IsLow && !e.IsLow);
                bool isBear = (!s.IsLow && e.IsLow);
                if (!isBull && !isBear) continue;

                double startP = s.Price;
                double endP   = e.Price;
                double eq     = (startP + endP) / 2.0;

                // Check if balanced: did price revisit EQ after leg end?
                bool balanced = false;
                int legEndBarIdx = e.BarIdx;
                int barsToCheck  = totalBias4hBars - 1 - legEndBarIdx;
                for (int k = 1; k <= barsToCheck && k < BarsArray[1].Count; k++)
                {
                    if (isBull && Lows[1][BarsArray[1].Count - 1 - (legEndBarIdx + k)] <= eq)
                    { balanced = true; break; }
                    if (isBear && Highs[1][BarsArray[1].Count - 1 - (legEndBarIdx + k)] >= eq)
                    { balanced = true; break; }
                }

                // Use this leg (even if balanced — fallback)
                biasIsBull    = isBull;
                biasAvailable = true;

                if (!balanced) break; // unbalanced found — use it and stop
            }
        }

        // ─── State machine ─────────────────────────────────────────────────

        private void RunScanning()
        {
            // Find last unbalanced exec TF leg aligned with bias
            var merged = BuildMergedSwings(execShBars, execShPrices, execSlBars, execSlPrices);
            if (merged.Count < 2) return;

            for (int i = merged.Count - 2; i >= 0; i--)
            {
                var s = merged[i];
                var e = merged[i + 1];
                if (e.BarIdx - s.BarIdx < MinLegBars) continue;

                bool legBull = (s.IsLow && !e.IsLow);
                bool legBear = (!s.IsLow && e.IsLow);
                if (!legBull && !legBear) continue;

                // Must align with bias
                if (legBull != biasIsBull) continue;

                double startP = s.Price, endP = e.Price;
                double eq     = (startP + endP) / 2.0;

                // Check if balanced
                int afterEnd = CurrentBar - e.BarIdx;
                bool balanced = false;
                for (int k = 1; k < afterEnd; k++)
                {
                    int barsAgo = CurrentBar - (e.BarIdx + k);
                    if (barsAgo < 0) break;
                    if (legBull && Low[barsAgo] <= eq)  { balanced = true; break; }
                    if (legBear && High[barsAgo] >= eq) { balanced = true; break; }
                }
                if (balanced) continue; // look for a more recent unbalanced leg

                // Find externes level within this leg in discount (bull) / premium (bear)
                double extLevel = FindExterneLevel(e.BarIdx, s.BarIdx, legBull, startP, endP, eq);
                if (double.IsNaN(extLevel)) continue; // no externe level found

                // All conditions met — activate this leg
                legStartPrice  = startP;
                legEndPrice    = endP;
                legEq          = eq;
                legStartBar    = s.BarIdx;
                legEndBar      = e.BarIdx;
                legIsBull      = legBull;
                activeExtLevel = extLevel;

                setupState = State4.WaitingSweep;
                break;
            }
        }

        private void RunWaitingSweep()
        {
            // Only look for sweep after the leg has ended
            if (CurrentBar <= legEndBar) return;

            // Re-validate: if leg is now balanced, reset
            int afterEnd = CurrentBar - legEndBar;
            for (int k = 1; k < afterEnd; k++)
            {
                int barsAgo = CurrentBar - (legEndBar + k);
                if (barsAgo < 0) break;
                if (legIsBull  && Low[barsAgo]  <= legEq) { setupState = State4.Scanning; return; }
                if (!legIsBull && High[barsAgo] >= legEq) { setupState = State4.Scanning; return; }
            }

            // Check if current bar sweeps the externe level
            bool swept = legIsBull ? Low[0] < activeExtLevel : High[0] > activeExtLevel;
            if (swept)
            {
                sweepBar      = CurrentBar;
                sweepExtreme  = legIsBull ? Low[0] : High[0];
                setupState    = State4.WaitingFVG;
            }
        }

        private void RunWaitingFVG()
        {
            int barsSinceSweep = CurrentBar - sweepBar;
            if (barsSinceSweep > CisdFvgWindow)
            {
                setupState = State4.Scanning;
                return;
            }

            if (CurrentBar < sweepBar + 2) return;

            // Detect FVG on current bar (confirmed 3-candle pattern)
            // Bull FVG: High[2] < Low[0]  (gap between 2-bars-ago high and current low)
            // Bear FVG: Low[2]  > High[0]
            if (legIsBull && High[2] < Low[0])
            {
                fvgBottom    = High[2];
                fvgTop       = Low[0];
                fvgFoundBar  = CurrentBar;
                setupState   = State4.WaitingCISD;
            }
            else if (!legIsBull && Low[2] > High[0])
            {
                fvgTop       = Low[2];
                fvgBottom    = High[0];
                fvgFoundBar  = CurrentBar;
                setupState   = State4.WaitingCISD;
            }
        }

        private void RunWaitingCISD()
        {
            int barsSinceFvg = CurrentBar - fvgFoundBar;
            if (barsSinceFvg > FvgInverseWindow)
            {
                // Timeout — go back and look for another FVG in the same sweep window
                setupState = State4.WaitingFVG;
                return;
            }

            bool cisdBull = legIsBull  && Low[0] <= fvgTop    && Close[0] > fvgTop;
            bool cisdBear = !legIsBull && High[0] >= fvgBottom && Close[0] < fvgBottom;

            if (!cisdBull && !cisdBear) return;

            double entryPrice = Close[0];
            double slPrice, tpPrice;

            if (legIsBull)
            {
                slPrice = sweepExtreme - SlBufferTicks * TickSize;
                tpPrice = legEndPrice;
                if (tpPrice <= entryPrice) { setupState = State4.Scanning; return; }
            }
            else
            {
                slPrice = sweepExtreme + SlBufferTicks * TickSize;
                tpPrice = legEndPrice;
                if (tpPrice >= entryPrice) { setupState = State4.Scanning; return; }
            }

            int qty = CalcContracts(entryPrice, slPrice);
            if (qty < 1) qty = 1;

            // Set SL/TP before entry
            SetStopLoss("ICT",  CalculationMode.Price, slPrice, false);
            SetProfitTarget("ICT", CalculationMode.Price, tpPrice);

            if (legIsBull)
                EnterLong(qty, "ICT");
            else
                EnterShort(qty, "ICT");

            setupState = State4.Scanning; // reset to look for next setup after this trade closes
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Find the first externe level within the price leg that is in
        /// discount (bull leg) or premium (bear leg) zone.
        /// Returns NaN if none found.
        /// </summary>
        private double FindExterneLevel(int legEndBarAbs, int legStartBarAbs, bool isBull,
                                         double startPrice, double endPrice, double eq)
        {
            // Scan bars inside the leg for swing lows (bull) or highs (bear)
            // Must be in discount (below eq) for bull, premium (above eq) for bear
            for (int absBar = legStartBarAbs + ExtSwingStrength;
                     absBar <= legEndBarAbs - ExtSwingStrength;
                     absBar++)
            {
                int barsAgo = CurrentBar - absBar;
                if (barsAgo < 0 || barsAgo >= CurrentBar) continue;

                if (isBull)
                {
                    double price = Low[barsAgo];
                    if (price >= eq) continue; // must be in discount
                    if (IsSwingLowAt(barsAgo, ExtSwingStrength))
                        return price;
                }
                else
                {
                    double price = High[barsAgo];
                    if (price <= eq) continue; // must be in premium
                    if (IsSwingHighAt(barsAgo, ExtSwingStrength))
                        return price;
                }
            }
            return double.NaN;
        }

        private bool IsSwingHighAt(int barsAgo, int n)
        {
            double refH = High[barsAgo];
            for (int i = 1; i <= n; i++)
            {
                int agoMinus = barsAgo - i; // more recent
                int agoPlus  = barsAgo + i; // older
                if (agoMinus < 0 || agoPlus >= CurrentBar) return false;
                if (High[agoMinus] >= refH || High[agoPlus] >= refH) return false;
            }
            return true;
        }

        private bool IsSwingLowAt(int barsAgo, int n)
        {
            double refL = Low[barsAgo];
            for (int i = 1; i <= n; i++)
            {
                int agoMinus = barsAgo - i;
                int agoPlus  = barsAgo + i;
                if (agoMinus < 0 || agoPlus >= CurrentBar) return false;
                if (Low[agoMinus] <= refL || Low[agoPlus] <= refL) return false;
            }
            return true;
        }

        /// <summary>
        /// Merge and clean swing high/low lists into an alternating sequence.
        /// Consecutive same-type entries are resolved by keeping the more extreme.
        /// </summary>
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

            var cleaned = new List<SwingPoint>();
            foreach (var sp in all)
            {
                if (cleaned.Count > 0 && cleaned[cleaned.Count - 1].IsLow == sp.IsLow)
                {
                    var prev = cleaned[cleaned.Count - 1];
                    if (sp.IsLow && sp.Price < prev.Price)
                        cleaned[cleaned.Count - 1] = sp;
                    else if (!sp.IsLow && sp.Price > prev.Price)
                        cleaned[cleaned.Count - 1] = sp;
                }
                else
                {
                    cleaned.Add(sp);
                }
            }
            return cleaned;
        }

        private int CalcContracts(double entry, double sl)
        {
            double riskUsd     = Account.Get(AccountItem.CashValue, Currency.UsDollar) * (RiskPercent / 100.0);
            double riskPerCont = Math.Abs(entry - sl) * Instrument.MasterInstrument.PointValue;
            if (riskPerCont <= 0) return 1;
            return Math.Max(1, (int)(riskUsd / riskPerCont));
        }

        // ─── Nested helper struct ──────────────────────────────────────────

        private struct SwingPoint
        {
            public int    BarIdx;
            public double Price;
            public bool   IsLow;

            public SwingPoint(int bar, double price, bool isLow)
            {
                BarIdx = bar;
                Price  = price;
                IsLow  = isLow;
            }
        }
    }
}
