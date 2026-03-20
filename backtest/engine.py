"""
Backtesting engine — state machine that processes each bar in the
execution timeframe and applies the ICT strategy logic.

States:
    SCANNING         → looking for last unbalanced leg aligned with bias
    WAITING_SWEEP    → leg found, waiting for externes low/high to be swept
    WAITING_FVG      → swept, looking for iFVG (within cisd_fvg_window bars)
    WAITING_CISD     → FVG found, waiting for CISD close (within fvg_inverse_window)
    IN_TRADE         → managing open position
"""

import pandas as pd
import numpy as np
from dataclasses import dataclass, field
from typing import Optional, List

from strategies.ict import (
    PriceLeg, find_price_legs, get_last_unbalanced_leg,
    find_externe_levels, find_sweep, detect_ifvg_cisd,
)
from config import Config


# ---------------------------------------------------------------------------
# Trade record
# ---------------------------------------------------------------------------

@dataclass
class Trade:
    entry_idx: int
    entry_price: float
    sl_price: float
    tp_price: float
    direction: str          # 'long' | 'short'
    contracts: float
    risk_usd: float

    # Filled on close
    exit_idx: Optional[int]    = None
    exit_price: Optional[float] = None
    exit_reason: Optional[str]  = None   # 'tp' | 'sl'
    pnl_usd: Optional[float]    = None
    r_multiple: Optional[float] = None

    # Debug / analysis
    sweep_idx: Optional[int]    = None
    fvg_top: Optional[float]    = None
    fvg_bottom: Optional[float] = None
    leg_start: Optional[float]  = None
    leg_top: Optional[float]    = None


def _position_size(account: float, risk_pct: float, entry: float, sl: float, point_value: float) -> float:
    risk_usd = account * risk_pct
    risk_pts = abs(entry - sl)
    if risk_pts == 0:
        return 0.0
    return risk_usd / (risk_pts * point_value)


def _get_bias(bias_series: pd.Series, timestamp) -> Optional[str]:
    """Look up the most recent bias value at or before the given timestamp."""
    idx = bias_series.index.searchsorted(timestamp, side='right') - 1
    if idx < 0:
        return None
    return bias_series.iloc[idx]


# ---------------------------------------------------------------------------
# Bias series pre-computation
# ---------------------------------------------------------------------------

def build_bias_series(df_bias: pd.DataFrame, cfg: Config) -> pd.Series:
    """
    Pre-compute the bias direction for every bar in df_bias.
    Result: pd.Series indexed like df_bias, values 'bull' | 'bear' | None.
    """
    legs = find_price_legs(df_bias, cfg.min_leg_candles, cfg.swing_n)
    bias = pd.Series(index=df_bias.index, dtype=object)

    for i in range(cfg.warmup_bars, len(df_bias)):
        leg = get_last_unbalanced_leg(legs, df_bias, i)
        bias.iloc[i] = leg.direction if leg else None

    return bias


# ---------------------------------------------------------------------------
# Main backtest loop
# ---------------------------------------------------------------------------

def run(df_exec: pd.DataFrame, df_bias: pd.DataFrame, cfg: Config):
    """
    Run the backtest.

    Returns:
        trades      : list of completed Trade objects
        equity_curve: pd.Series of account value after each closed trade
    """
    print("Building bias series …")
    bias_series = build_bias_series(df_bias, cfg)

    print("Pre-computing execution timeframe legs …")
    exec_legs = find_price_legs(df_exec, cfg.min_leg_candles, cfg.swing_n)

    account = cfg.account_size
    trades: List[Trade] = []
    equity = [account]

    # State
    state = 'SCANNING'
    active_leg: Optional[PriceLeg]            = None
    active_ext_levels: List                   = []
    active_ext_price: Optional[float]         = None
    sweep_idx: Optional[int]                  = None
    sweep_extreme: Optional[float]            = None   # actual sweep bar low/high
    fvg_top: Optional[float]                  = None
    fvg_bottom: Optional[float]               = None
    fvg_found_idx: Optional[int]              = None
    open_trade: Optional[Trade]               = None

    print(f"Running backtest on {len(df_exec)} bars …")

    for i in range(cfg.warmup_bars, len(df_exec)):
        bar = df_exec.iloc[i]
        ts  = df_exec.index[i]

        # ── Manage open trade ──────────────────────────────────────────────
        if open_trade is not None:
            hit_sl = (open_trade.direction == 'long'  and bar['low']  <= open_trade.sl_price) or \
                     (open_trade.direction == 'short' and bar['high'] >= open_trade.sl_price)
            hit_tp = (open_trade.direction == 'long'  and bar['high'] >= open_trade.tp_price) or \
                     (open_trade.direction == 'short' and bar['low']  <= open_trade.tp_price)

            if hit_sl or hit_tp:
                reason = 'tp' if hit_tp and not hit_sl else 'sl'
                exit_px = open_trade.tp_price if reason == 'tp' else open_trade.sl_price

                sign = 1 if open_trade.direction == 'long' else -1
                pnl  = sign * (exit_px - open_trade.entry_price) * open_trade.contracts * cfg.point_value
                risk = abs(open_trade.entry_price - open_trade.sl_price) * open_trade.contracts * cfg.point_value

                open_trade.exit_idx    = i
                open_trade.exit_price  = exit_px
                open_trade.exit_reason = reason
                open_trade.pnl_usd     = pnl
                open_trade.r_multiple  = pnl / risk if risk > 0 else 0

                account += pnl
                trades.append(open_trade)
                equity.append(account)
                open_trade = None
                state = 'SCANNING'
            else:
                continue

        # ── Get current bias ───────────────────────────────────────────────
        bias = _get_bias(bias_series, ts)
        if bias is None:
            continue

        # ── State machine ──────────────────────────────────────────────────
        if state == 'SCANNING':
            leg = get_last_unbalanced_leg(exec_legs, df_exec, i)
            if leg and leg.direction == bias:
                ext_levels = find_externe_levels(leg, df_exec, cfg.swing_n, cfg.max_ext_levels)
                if ext_levels:
                    active_leg        = leg
                    active_ext_levels = ext_levels
                    state             = 'WAITING_SWEEP'

        elif state == 'WAITING_SWEEP':
            # Re-validate leg still aligned with current bias
            current_leg = get_last_unbalanced_leg(exec_legs, df_exec, i)
            if not current_leg or current_leg.start_idx != active_leg.start_idx:
                state = 'SCANNING'
                continue

            # Only look for sweep after the leg has ended
            if i <= active_leg.end_idx:
                continue

            # Check if any externe level is swept this bar
            for ext_idx, ext_price in active_ext_levels:
                swept = (bias == 'bull' and bar['low']  < ext_price) or \
                        (bias == 'bear' and bar['high'] > ext_price)
                if swept:
                    sweep_idx     = i
                    sweep_extreme = bar['low'] if bias == 'bull' else bar['high']
                    active_ext_price = ext_price
                    state         = 'WAITING_FVG'
                    break

        elif state == 'WAITING_FVG':
            if i - sweep_idx > cfg.cisd_fvg_window:
                state = 'SCANNING'
                continue

            # Detect iFVG starting from this bar
            highs  = df_exec['high'].values
            lows   = df_exec['low'].values

            if i >= sweep_idx + 2:
                if bias == 'bull' and highs[i - 2] < lows[i]:
                    fvg_bottom    = highs[i - 2]
                    fvg_top       = lows[i]
                    fvg_found_idx = i
                    state         = 'WAITING_CISD'

                elif bias == 'bear' and lows[i - 2] > highs[i]:
                    fvg_top       = lows[i - 2]
                    fvg_bottom    = highs[i]
                    fvg_found_idx = i
                    state         = 'WAITING_CISD'

        elif state == 'WAITING_CISD':
            if i - fvg_found_idx > cfg.fvg_inverse_window:
                # FVG not inversed in time — scan for another FVG
                state = 'WAITING_FVG'
                continue

            # Check CISD: candle wicks into FVG AND closes on the other side
            cisd_bull = (bias == 'bull' and bar['low'] <= fvg_top and bar['close'] > fvg_top)
            cisd_bear = (bias == 'bear' and bar['high'] >= fvg_bottom and bar['close'] < fvg_bottom)

            if cisd_bull or cisd_bear:
                entry_price = bar['close']
                if bias == 'long' or bias == 'bull':
                    sl_price = sweep_extreme - cfg.sl_buffer_pts
                    tp_price = active_leg.end_price
                    direction = 'long'
                else:
                    sl_price = sweep_extreme + cfg.sl_buffer_pts
                    tp_price = active_leg.end_price
                    direction = 'short'

                # TP must be beyond entry
                if (direction == 'long'  and tp_price <= entry_price) or \
                   (direction == 'short' and tp_price >= entry_price):
                    state = 'SCANNING'
                    continue

                contracts = _position_size(
                    account, cfg.risk_pct, entry_price, sl_price, cfg.point_value
                )
                if contracts <= 0:
                    state = 'SCANNING'
                    continue

                open_trade = Trade(
                    entry_idx   = i,
                    entry_price = entry_price,
                    sl_price    = sl_price,
                    tp_price    = tp_price,
                    direction   = direction,
                    contracts   = contracts,
                    risk_usd    = account * cfg.risk_pct,
                    sweep_idx   = sweep_idx,
                    fvg_top     = fvg_top,
                    fvg_bottom  = fvg_bottom,
                    leg_start   = active_leg.start_price,
                    leg_top     = active_leg.end_price,
                )
                state = 'IN_TRADE'

    return trades, pd.Series(equity)
