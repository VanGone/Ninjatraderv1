from dataclasses import dataclass


@dataclass
class Config:
    # Data
    symbol: str = "NQ=F"
    bias_tf: str = "1h"       # Higher timeframe for bias
    exec_tf: str = "5m"       # Execution timeframe
    period: str = "60d"       # How far back to fetch

    # Leg detection
    min_leg_candles: int = 25  # Minimum candles from swing low to swing high
    swing_n: int = 5           # Bars each side for swing point confirmation
    warmup_bars: int = 100     # Skip first N bars (leg detection needs history)

    # Strategy rules
    max_ext_levels: int = 3    # Check first N externes lows/highs
    cisd_fvg_window: int = 10  # Max candles after sweep to find iFVG+CISD
    fvg_inverse_window: int = 4  # Max candles for iFVG to be inversed (CISD close)

    # Trade management
    sl_buffer_pts: float = 5.0  # Extra buffer below/above sweep low/high for SL

    # Risk
    risk_pct: float = 0.01        # 1% risk per trade
    account_size: float = 100_000.0
    point_value: float = 20.0     # NQ Futures: $20 per point
