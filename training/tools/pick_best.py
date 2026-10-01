"""Pick the best checkpoint of a run by measured behaviour, not by being the newest.

    .venv/Scripts/python.exe tools/pick_best.py --run-dir checkpoints/run_track/r1_speed --pack

`train_run.py` leaves `latest.pt` on whatever the last iteration happened to be, and a run on the
adaptive speed curriculum spends its whole life hunting around the fastest pace it can hold -- so
consecutive checkpoints alternate between clean ones and fall-heavy ones (export_checkpoint.py says
the same). This gates the last few saved checkpoints with `eval_lap.py`, deterministically, and
copies the winner to `<run-dir>/best.pt`.

Score: lap speed, for a checkpoint that keeps at least --min-clean of its athletes on their feet
and on the deck; a checkpoint that does not is scored far below any that does. With --pack the lap
speed is multiplied by the fraction that also get through 20 s among the pack task's figures, so a
fast runner that goes down at the first bump loses to a slightly slower one that does not.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def gate(ckpt: str, pack: bool, args, scratch: str) -> dict:
    """One eval_lap.py run in its own process; returns its mean block, or {} if it failed."""
    if os.path.exists(scratch):
        os.remove(scratch)
    cmd = [sys.executable, os.path.join(HERE, "eval_lap.py"), "--ckpt", ckpt,
           "--episodes", str(args.episodes), "--seconds", str(args.seconds),
           "--num-envs", str(args.num_envs), "--target-speed", str(args.target_speed),
           "--json", scratch, "--label", os.path.basename(ckpt) + ("@pack" if pack else "")]
    if pack:
        cmd.append("--pack")
    subprocess.run(cmd, cwd=HERE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if not os.path.exists(scratch):
        return {}
    with open(scratch, encoding="utf-8") as fh:
        row = json.loads(fh.readlines()[-1])
    return row.get("mean", {})


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run-dir", default="", help="a run's checkpoint directory (model_*.pt)")
    ap.add_argument("--ckpt", action="append", default=[],
                    help="score this checkpoint as well (repeatable); with no --run-dir, only these")
    ap.add_argument("--last", type=int, default=5, help="how many checkpoints to gate")
    ap.add_argument("--every", type=int, default=400,
                    help="iterations between the ones gated. 0 spreads them evenly over the second "
                         "half of the run, for a long run whose best moment may be well before its end")
    ap.add_argument("--pack", action="store_true", help="also gate among the pack task's figures")
    ap.add_argument("--min-clean", type=float, default=0.97)
    ap.add_argument("--episodes", type=int, default=3)
    ap.add_argument("--seconds", type=float, default=20.0)
    ap.add_argument("--num-envs", type=int, default=256)
    ap.add_argument("--target-speed", type=float, default=3.6)
    ap.add_argument("--out", default="", help="where the winner is copied (default <run-dir>/best.pt)")
    ap.add_argument("--json", default="", help="append every candidate's scores to this file")
    args = ap.parse_args()

    picks = []
    if args.run_dir:
        saved = sorted(glob.glob(os.path.join(args.run_dir, "model_*.pt")))
        if not saved:
            raise SystemExit(f"no model_*.pt in {args.run_dir}")
        newest = int(os.path.basename(saved[-1])[6:-3])
        by_iter = {int(os.path.basename(p)[6:-3]): p for p in saved}
        every = args.every or max(1, (newest - min(by_iter)) // (2 * max(1, args.last - 1)))
        for k in range(args.last):
            want = newest - k * every
            near = min(by_iter, key=lambda i: abs(i - want))
            if by_iter[near] not in picks:
                picks.append(by_iter[near])
    picks += [c for c in args.ckpt if c not in picks]
    if not picks:
        raise SystemExit("nothing to score: give --run-dir or --ckpt")

    scratch = os.path.join(HERE, "logs", f"pick_best_{os.getpid()}.jsonl")
    rows = []
    for ck in picks:
        lap = gate(ck, False, args, scratch)
        if not lap:
            print(f"{os.path.basename(ck)}: the lap gate did not run", flush=True)
            continue
        speed, clean = lap["speed"], lap["clean"]
        score = speed if clean >= args.min_clean else 0.25 * speed * clean
        row = {"ckpt": ck, "speed": speed, "clean": clean, "pitch": lap["pitch_dev"], "roll": lap["roll_dev"],
               "torque": lap["torque"], "jerk": lap["jerk"]}
        if args.pack:
            pk = gate(ck, True, args, scratch)
            row["pack_clean"] = pk.get("clean", 0.0)
            row["pack_speed"] = pk.get("speed", 0.0)
            score *= row["pack_clean"]
        row["score"] = score
        rows.append(row)
        print(f"{os.path.basename(ck)}: lap {speed:.2f} m/s, {clean * 100:.0f}% clean"
              + (f", in a pack {row['pack_clean'] * 100:.0f}% clean at {row['pack_speed']:.2f} m/s" if args.pack else "")
              + f" -> score {score:.3f}", flush=True)
    if os.path.exists(scratch):
        os.remove(scratch)
    if not rows:
        raise SystemExit("no candidate could be gated")

    best = max(rows, key=lambda r: r["score"])
    out = args.out or (os.path.join(args.run_dir, "best.pt") if args.run_dir else "")
    if out:
        shutil.copyfile(best["ckpt"], out)
        best = dict(best, copied_to=out)
    if args.json:
        with open(args.json, "a", encoding="utf-8") as fh:
            fh.write(json.dumps({"run_dir": args.run_dir, "best": best, "candidates": rows}) + "\n")
    print(f"BEST {os.path.basename(best['ckpt'])}: lap {best['speed']:.2f} m/s, {best['clean'] * 100:.0f}% clean"
          + (f", pack {best['pack_clean'] * 100:.0f}% clean" if args.pack else "")
          + (f" -> {out}" if out else ""), flush=True)


if __name__ == "__main__":
    main()
