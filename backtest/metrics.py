import numpy as np
import pandas as pd
from typing import List
from backtest.engine import Trade


def compute(trades: List[Trade], initial_account: float, final_account: float) -> dict:
    if not trades:
        return {"error": "No trades found."}

    pnls     = [t.pnl_usd for t in trades]
    wins     = [p for p in pnls if p > 0]
    losses   = [p for p in pnls if p <= 0]
    r_mults  = [t.r_multiple for t in trades if t.r_multiple is not None]

    win_rate        = len(wins) / len(trades)
    avg_win         = np.mean(wins)   if wins   else 0.0
    avg_loss        = np.mean(losses) if losses else 0.0
    profit_factor   = abs(sum(wins) / sum(losses)) if sum(losses) != 0 else float('inf')
    total_return    = (final_account - initial_account) / initial_account * 100
    avg_r           = np.mean(r_mults) if r_mults else 0.0

    # Max drawdown from equity curve
    equity = np.array(
        [initial_account] + [initial_account + sum(pnls[:i+1]) for i in range(len(pnls))]
    )
    peak       = np.maximum.accumulate(equity)
    drawdowns  = (equity - peak) / peak * 100
    max_dd     = drawdowns.min()

    # Sharpe (daily grouping not possible without timestamps, use trade-level PnL)
    if len(pnls) > 1:
        sharpe = (np.mean(pnls) / np.std(pnls)) * np.sqrt(252) if np.std(pnls) != 0 else 0.0
    else:
        sharpe = 0.0

    return {
        "Total Trades":      len(trades),
        "Win Rate":          f"{win_rate:.1%}",
        "Profit Factor":     f"{profit_factor:.2f}",
        "Total Return":      f"{total_return:.2f}%",
        "Total PnL ($)":     f"{sum(pnls):,.0f}",
        "Avg Win ($)":       f"{avg_win:,.0f}",
        "Avg Loss ($)":      f"{avg_loss:,.0f}",
        "Avg R-Multiple":    f"{avg_r:.2f}R",
        "Max Drawdown":      f"{max_dd:.2f}%",
        "Sharpe Ratio":      f"{sharpe:.2f}",
        "Final Account ($)": f"{final_account:,.0f}",
    }


def print_summary(metrics: dict):
    print("\n" + "═" * 40)
    print("  BACKTEST RESULTS — NQ Futures ICT")
    print("═" * 40)
    for k, v in metrics.items():
        print(f"  {k:<22} {v}")
    print("═" * 40 + "\n")
