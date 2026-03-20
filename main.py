"""
Ninjatraderv1 — ICT Strategy Backtester for NQ Futures

Usage:
    python main.py

Configuration is in config.py.
"""

from config import Config
from data.fetcher import fetch
from backtest.engine import run
from backtest.metrics import compute, print_summary
from visualization.charts import plot_results


def main():
    cfg = Config()

    print(f"Fetching {cfg.symbol} data …")
    print(f"  Bias TF  : {cfg.bias_tf} ({cfg.period})")
    print(f"  Exec TF  : {cfg.exec_tf} ({cfg.period})")

    df_bias = fetch(cfg.symbol, cfg.bias_tf, cfg.period)
    df_exec = fetch(cfg.symbol, cfg.exec_tf, cfg.period)

    print(f"  Bias bars: {len(df_bias)}")
    print(f"  Exec bars: {len(df_exec)}")

    trades, equity = run(df_exec, df_bias, cfg)

    print(f"\nCompleted trades: {len(trades)}")

    metrics = compute(trades, cfg.account_size, equity.iloc[-1])
    print_summary(metrics)

    if trades:
        plot_results(df_exec, trades, equity)
    else:
        print("No trades generated. Try adjusting Config parameters.")


if __name__ == "__main__":
    main()
