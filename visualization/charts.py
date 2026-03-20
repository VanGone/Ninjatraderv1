import pandas as pd
import numpy as np
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
from typing import List
from backtest.engine import Trade


def plot_results(
    df_exec: pd.DataFrame,
    trades: List[Trade],
    equity_curve: pd.Series,
):
    fig, axes = plt.subplots(
        3, 1,
        figsize=(16, 12),
        gridspec_kw={'height_ratios': [4, 1, 2]},
    )
    fig.patch.set_facecolor('#0d1117')
    for ax in axes:
        ax.set_facecolor('#161b22')
        ax.tick_params(colors='#8b949e')
        ax.spines[:].set_color('#30363d')

    # ── Panel 1: Price + Trades ────────────────────────────────────────────
    ax1 = axes[0]
    ax1.set_title('NQ Futures — ICT Backtest', color='#e6edf3', fontsize=13, pad=10)

    # Price line
    ax1.plot(df_exec.index, df_exec['close'], color='#58a6ff', linewidth=0.6, alpha=0.7)

    for t in trades:
        entry_ts = df_exec.index[t.entry_idx]
        exit_ts  = df_exec.index[t.exit_idx] if t.exit_idx else df_exec.index[-1]
        color    = '#3fb950' if t.exit_reason == 'tp' else '#f85149'

        # Entry marker
        ax1.axvline(entry_ts, color=color, alpha=0.3, linewidth=0.8)
        ax1.scatter(entry_ts, t.entry_price, marker='^' if t.direction == 'long' else 'v',
                    color=color, s=60, zorder=5)

        # SL / TP lines
        ax1.hlines(t.sl_price, entry_ts, exit_ts, colors='#f85149', linewidths=0.8,
                   linestyles='dashed', alpha=0.6)
        ax1.hlines(t.tp_price, entry_ts, exit_ts, colors='#3fb950', linewidths=0.8,
                   linestyles='dashed', alpha=0.6)

        # FVG box
        if t.fvg_top and t.fvg_bottom:
            ax1.axhspan(t.fvg_bottom, t.fvg_top, alpha=0.12, color='#bc8cff',
                        xmin=(t.entry_idx - 2) / len(df_exec),
                        xmax=t.entry_idx / len(df_exec))

    win_patch  = mpatches.Patch(color='#3fb950', label='TP hit')
    loss_patch = mpatches.Patch(color='#f85149', label='SL hit')
    ax1.legend(handles=[win_patch, loss_patch], facecolor='#21262d',
               labelcolor='#e6edf3', framealpha=0.8)
    ax1.set_ylabel('Price', color='#8b949e')

    # ── Panel 2: Per-trade PnL bars ────────────────────────────────────────
    ax2 = axes[1]
    ax2.set_title('Per-Trade PnL ($)', color='#e6edf3', fontsize=10, pad=6)
    for t in trades:
        if t.pnl_usd is None:
            continue
        color = '#3fb950' if t.pnl_usd > 0 else '#f85149'
        ax2.bar(t.entry_idx, t.pnl_usd, color=color, alpha=0.8, width=3)
    ax2.axhline(0, color='#8b949e', linewidth=0.6)
    ax2.set_ylabel('PnL ($)', color='#8b949e')

    # ── Panel 3: Equity curve ──────────────────────────────────────────────
    ax3 = axes[2]
    ax3.set_title('Equity Curve', color='#e6edf3', fontsize=10, pad=6)
    ax3.plot(equity_curve.values, color='#58a6ff', linewidth=1.5)
    ax3.fill_between(range(len(equity_curve)), equity_curve.values,
                     equity_curve.values[0], alpha=0.15, color='#58a6ff')
    ax3.axhline(equity_curve.values[0], color='#8b949e', linewidth=0.6, linestyle='--')
    ax3.set_ylabel('Account ($)', color='#8b949e')
    ax3.set_xlabel('Trade #', color='#8b949e')

    plt.tight_layout(pad=2.0)
    plt.savefig('results.png', dpi=150, bbox_inches='tight', facecolor='#0d1117')
    print("Chart saved → results.png")
    plt.show()
