import yfinance as yf
import pandas as pd


def fetch(symbol: str, interval: str, period: str) -> pd.DataFrame:
    """
    Download OHLCV data via yfinance and normalize column names.
    Index is converted to US/Eastern timezone.

    Note on yfinance limits:
        1m  → last 7 days only
        5m  → last 60 days
        1h  → last 730 days
    """
    df = yf.Ticker(symbol).history(interval=interval, period=period)

    if df.empty:
        raise ValueError(
            f"No data returned for {symbol} ({interval}, {period}). "
            "Check symbol and interval/period combination."
        )

    df.index = df.index.tz_convert("America/New_York")
    df.columns = [c.lower() for c in df.columns]
    df = df[["open", "high", "low", "close", "volume"]].dropna()
    return df
