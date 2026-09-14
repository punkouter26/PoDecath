"""One line per run: where it is and what it is doing right now.

    tools/status.py o1_explore o2_explore_gait2 o3_scale25
    tools/status.py --latest 4

Shows the columns that separate the two failure modes this project keeps landing in -- a statue has
duty_factor near 1.0 and v_toward near 0, a toppler has a short episode and a v_toward that looks
healthy -- plus rt_gait against the floor a statue collects, which is what says whether stepping has
started.
"""
import argparse
import csv
import glob
import math
import os

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOGS = os.path.join(HERE, "logs")


def tail(run, n=10):
    path = os.path.join(LOGS, run + ".csv")
    if not os.path.exists(path):
        return None
    with open(path, newline="") as fh:
        rows = list(csv.DictReader(fh))
    return rows[-n:] if rows else None


def num(rows, key):
    vals = []
    for r in rows:
        try:
            v = float(r.get(key, ""))
        except (TypeError, ValueError):
            continue
        if not math.isnan(v):
            vals.append(v)
    return sum(vals) / len(vals) if vals else float("nan")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("runs", nargs="*")
    ap.add_argument("--latest", type=int, default=0, help="instead, show the N most recent runs")
    ap.add_argument("--last", type=int, default=10, help="average over this many iterations")
    a = ap.parse_args()

    runs = a.runs
    if a.latest:
        runs = [os.path.splitext(os.path.basename(p))[0]
                for p in sorted(glob.glob(os.path.join(LOGS, "*.csv")), key=os.path.getmtime)][-a.latest:]

    print("%-20s %6s %8s %8s %8s %8s %8s %8s %8s" %
          ("run", "iter", "surv", "fall", "v_tow", "v_hit", "duty", "rt_gait", "ep_s"))
    for run in runs:
        rows = tail(run, a.last)
        if not rows:
            print("%-20s %6s  (no csv yet)" % (run, "-"))
            continue
        print("%-20s %6d %8.3f %8.3f %8.3f %8.3f %8.3f %8.3f %8.2f" %
              (run, int(float(rows[-1]["iter"])), num(rows, "surv_ratio"), num(rows, "fall_rate"),
               num(rows, "v_toward"), num(rows, "v_hit_frac"), num(rows, "duty_factor"),
               num(rows, "rt_gait"), num(rows, "ep_len_s")))


if __name__ == "__main__":
    main()
