"""Print a scalar series from a TensorBoard run directory (first/last/changes)."""
import sys, os, glob
from tensorboard.backend.event_processing.event_accumulator import EventAccumulator

run, tags = sys.argv[1], sys.argv[2].split(",")
d = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "logs", "tb", run)
ea = EventAccumulator(d, size_guidance={"scalars": 0}); ea.Reload()
avail = ea.Tags()["scalars"]
for tag in tags:
    if tag not in avail:
        print(f"{tag}: MISSING (have: {len(avail)} tags)"); continue
    ev = ea.Scalars(tag)
    vals = [(e.step, e.value) for e in ev]
    changes = [vals[0]]
    for s, v in vals[1:]:
        if abs(v - changes[-1][1]) > 1e-9:
            changes.append((s, v))
    print(f"{tag}: n={len(vals)} first={vals[0][1]:.4g}@{vals[0][0]} last={vals[-1][1]:.4g}@{vals[-1][0]} "
          f"distinct={len(changes)}")
    if len(changes) <= 12:
        print("   changes:", ", ".join(f"{v:.3g}@{s}" for s, v in changes))
    else:
        print("   sample:", ", ".join(f"{v:.3g}@{s}" for s, v in changes[::max(1, len(changes)//10)]))
