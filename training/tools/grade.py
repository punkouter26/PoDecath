"""Grade one or more run CSVs against the rl_optimization_log thresholds.

Usage: python tools/grade.py <run-name> [<run-name> ...] [--last N] [--at ITER] [--terms]
Reads training/logs/<run>.csv and prints the mean of the last N iterations for the KPI
columns, side by side, with a pass/fail against the exit thresholds.
"""
import argparse, csv, math, os

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOGS = os.path.join(HERE, "logs")

# name -> (want, lo, hi); want in {"up", "down", "band"}
THRESH = {
    "surv_ratio":  ("up",   0.90, None),
    "fall_rate":   ("down", None, 0.10),
    "v_err":       ("down", None, 0.35),
    "v_hit_frac":  ("up",   0.50, None),
    "air_time":    ("band", 0.05, 0.25),
    "duty_factor": ("band", 0.35, 0.65),
    "foot_slip":   ("down", None, 0.15),
    "pitch_dev":   ("down", None, 15.0),
    "roll_dev":    ("down", None, 10.0),
}
SHOW = ["ep_return", "surv_ratio", "fall_rate", "ep_len_s", "v_toward", "v_err", "v_hit_frac",
        "air_time", "duty_factor", "foot_slip", "pitch_dev", "roll_dev", "torque", "power",
        "jerk", "act_sat", "std", "fps"]


def load(run):
    path = run if run.endswith(".csv") else os.path.join(LOGS, run + ".csv")
    with open(path, newline="") as fh:
        return list(csv.DictReader(fh))


def window(rows, last, at):
    if at is not None:
        rows = [r for r in rows if int(float(r["iter"])) <= at]
    return rows[-last:]


def mean(rows, key):
    vals = []
    for r in rows:
        try:
            v = float(r.get(key, ""))
        except (TypeError, ValueError):
            continue
        if not math.isnan(v):
            vals.append(v)
    return sum(vals) / len(vals) if vals else float("nan")


def verdict(key, v):
    if key not in THRESH or math.isnan(v):
        return ""
    want, lo, hi = THRESH[key]
    if want == "up":
        return "PASS" if v > lo else "fail"
    if want == "down":
        return "PASS" if v < hi else "fail"
    return "PASS" if lo <= v <= hi else "fail"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--last", type=int, default=25)
    ap.add_argument("--at", type=int, default=None)
    ap.add_argument("--terms", action="store_true")
    a = ap.parse_args()

    data = {}
    for run in a.runs:
        w = window(load(run), a.last, a.at)
        data[run] = (w, int(float(w[-1]["iter"])))

    names = list(data)
    cw = max(max(len(n) for n in names) + 2, 12)
    hdr = "metric".ljust(14) + "".join(n.rjust(cw) for n in names) + "   verdict"
    print(hdr)
    print("-" * len(hdr))
    for key in SHOW:
        cells = "".join(f"{mean(data[n][0], key):{cw}.4g}" for n in names)
        print(key.ljust(14) + cells + "   " + verdict(key, mean(data[names[-1]][0], key)))
    print("-" * len(hdr))
    print("graded at iter" + "".join(str(data[n][1]).rjust(cw) for n in names))

    if a.terms:
        keys = sorted(k for k in data[names[0]][0][0] if k.startswith("rt_"))
        print("\nper-term reward (mean per step)")
        for k in keys:
            cells = "".join(f"{mean(data[n][0], k):{cw}.4f}" for n in names)
            print(k.ljust(14) + cells)
        for n in names:
            print(f"  {n}: sum of terms = {sum(mean(data[n][0], k) for k in keys):.4f}")


if __name__ == "__main__":
    main()
