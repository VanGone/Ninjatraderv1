"""
ICT concept detectors used by the backtesting engine.

Concepts implemented:
  - Price Leg detection (swing-based, min N candles)
  - Unbalanced leg check (50% EQ not yet visited)
  - Externes Low / High (swing points in discount / premium zone)
  - Sweep detection
  - Inverse FVG + CISD detection
"""

import numpy as np
import pandas as pd
from dataclasses import dataclass
from typing import Optional, List, Tuple


# ---------------------------------------------------------------------------
# Data structures
# ---------------------------------------------------------------------------

@dataclass
class PriceLeg:
    start_idx: int
    end_idx: int
    direction: str       # 'bull' | 'bear'
    start_price: float   # swing low (bull) or swing high (bear)
    end_price: float     # swing high (bull) or swing low (bear)

    @property
    def eq(self) -> float:
        """50% equilibrium level."""
        return (self.start_price + self.end_price) / 2

    def is_balanced(self, df: pd.DataFrame, as_of: int) -> bool:
        """Has price returned to the 50% level between leg end and as_of bar?"""
        future = df.iloc[self.end_idx + 1: as_of + 1]
        if self.direction == 'bull':
            return bool((future['low'] <= self.eq).any())
        return bool((future['high'] >= self.eq).any())


# ---------------------------------------------------------------------------
# Swing point detection
# ---------------------------------------------------------------------------

def _swing_highs(highs: np.ndarray, n: int) -> List[int]:
    result = []
    for i in range(n, len(highs) - n):
        if highs[i] == highs[i - n: i + n + 1].max():
            result.append(i)
    return result


def _swing_lows(lows: np.ndarray, n: int) -> List[int]:
    result = []
    for i in range(n, len(lows) - n):
        if lows[i] == lows[i - n: i + n + 1].min():
            result.append(i)
    return result


# ---------------------------------------------------------------------------
# Price leg detection
# ---------------------------------------------------------------------------

def find_price_legs(df: pd.DataFrame, min_candles: int, swing_n: int) -> List[PriceLeg]:
    """
    Pair alternating swing highs and lows into legs.
    Only keeps legs spanning at least min_candles bars.
    """
    highs = df['high'].values
    lows  = df['low'].values

    sh = _swing_highs(highs, swing_n)
    sl = _swing_lows(lows, swing_n)

    # Merge and sort all swings
    swings = [(i, 'H', highs[i]) for i in sh] + [(i, 'L', lows[i]) for i in sl]
    swings.sort(key=lambda x: x[0])

    # Remove consecutive same-type swings — keep the more extreme one
    cleaned: List[Tuple[int, str, float]] = []
    for idx, typ, price in swings:
        if cleaned and cleaned[-1][1] == typ:
            prev_idx, prev_typ, prev_price = cleaned[-1]
            if (typ == 'H' and price > prev_price) or (typ == 'L' and price < prev_price):
                cleaned[-1] = (idx, typ, price)
        else:
            cleaned.append((idx, typ, price))

    legs: List[PriceLeg] = []
    for i in range(len(cleaned) - 1):
        s_idx, s_typ, s_price = cleaned[i]
        e_idx, e_typ, e_price = cleaned[i + 1]

        if e_idx - s_idx < min_candles:
            continue

        if s_typ == 'L' and e_typ == 'H':
            legs.append(PriceLeg(s_idx, e_idx, 'bull', s_price, e_price))
        elif s_typ == 'H' and e_typ == 'L':
            legs.append(PriceLeg(s_idx, e_idx, 'bear', s_price, e_price))

    return legs


def get_last_unbalanced_leg(
    legs: List[PriceLeg],
    df: pd.DataFrame,
    as_of: int,
) -> Optional[PriceLeg]:
    """
    Among legs confirmed before as_of, return the most recent one
    that has NOT yet been balanced (price hasn't returned to 50% EQ).
    """
    completed = [l for l in legs if l.end_idx < as_of]
    for leg in reversed(completed):
        if not leg.is_balanced(df, as_of):
            return leg
    return None


# ---------------------------------------------------------------------------
# Externes Low / High
# ---------------------------------------------------------------------------

def find_externe_levels(
    leg: PriceLeg,
    df: pd.DataFrame,
    swing_n: int,
    max_count: int = 3,
) -> List[Tuple[int, float]]:
    """
    For a bullish leg: find swing lows in the DISCOUNT zone (price < EQ).
    For a bearish leg: find swing highs in the PREMIUM zone (price > EQ).

    Returns up to max_count levels as [(global_idx, price), ...] sorted by index.
    """
    leg_slice = df.iloc[leg.start_idx: leg.end_idx + 1]

    if leg.direction == 'bull':
        local_indices = _swing_lows(leg_slice['low'].values, swing_n)
        result = [
            (leg.start_idx + li, leg_slice['low'].iloc[li])
            for li in local_indices
            if leg_slice['low'].iloc[li] < leg.eq
        ]
    else:
        local_indices = _swing_highs(leg_slice['high'].values, swing_n)
        result = [
            (leg.start_idx + li, leg_slice['high'].iloc[li])
            for li in local_indices
            if leg_slice['high'].iloc[li] > leg.eq
        ]

    return result[:max_count]


# ---------------------------------------------------------------------------
# Sweep detection
# ---------------------------------------------------------------------------

def find_sweep(
    level: float,
    direction: str,   # 'bull' leg → sweep goes below level; 'bear' → above
    df: pd.DataFrame,
    from_idx: int,
) -> Optional[int]:
    """
    Scan forward from from_idx for the first bar that sweeps the given level.
    Bull setup: bar low < level
    Bear setup: bar high > level
    """
    for i in range(from_idx, len(df)):
        if direction == 'bull' and df['low'].iloc[i] < level:
            return i
        if direction == 'bear' and df['high'].iloc[i] > level:
            return i
    return None


# ---------------------------------------------------------------------------
# Inverse FVG + CISD detection
# ---------------------------------------------------------------------------

def detect_ifvg_cisd(
    df: pd.DataFrame,
    sweep_idx: int,
    direction: str,         # 'bull' | 'bear'
    cisd_window: int = 10,
    inverse_window: int = 4,
) -> Optional[Tuple[float, float, int, float]]:
    """
    After a sweep, look for an Inverse FVG and CISD confirmation.

    Bull setup:
      - Bullish FVG forms within cisd_window bars: candle[i-2].high < candle[i].low
      - Within inverse_window bars after FVG: a candle wicks INTO the FVG (low <= fvg_top)
        AND closes ABOVE fvg_top (CISD confirmed)
      - Entry: close of that candle

    Bear setup (mirrored):
      - Bearish FVG: candle[i-2].low > candle[i].high
      - CISD: close BELOW fvg_bottom

    Returns (fvg_top, fvg_bottom, cisd_bar_idx, entry_price) or None.
    """
    highs  = df['high'].values
    lows   = df['low'].values
    closes = df['close'].values
    scan_end = min(sweep_idx + cisd_window, len(df) - 1)

    for i in range(sweep_idx + 2, scan_end + 1):
        if direction == 'bull':
            # Bullish FVG: gap between candle[i-2] high and candle[i] low
            if highs[i - 2] < lows[i]:
                fvg_bottom = highs[i - 2]
                fvg_top    = lows[i]
                cisd_end   = min(i + inverse_window, len(df) - 1)
                for j in range(i + 1, cisd_end + 1):
                    if lows[j] <= fvg_top and closes[j] > fvg_top:
                        return fvg_top, fvg_bottom, j, closes[j]

        else:  # bear
            # Bearish FVG: gap between candle[i-2] low and candle[i] high
            if lows[i - 2] > highs[i]:
                fvg_top    = lows[i - 2]
                fvg_bottom = highs[i]
                cisd_end   = min(i + inverse_window, len(df) - 1)
                for j in range(i + 1, cisd_end + 1):
                    if highs[j] >= fvg_bottom and closes[j] < fvg_bottom:
                        return fvg_top, fvg_bottom, j, closes[j]

    return None
