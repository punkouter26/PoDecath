"""Film a checkpoint in MuJoCo and put the clip in TensorBoard, next to the reward curves.

    .venv/Scripts/python.exe tools/rollout_video.py --ckpt checkpoints/run_track/<run>/model_00400.pt \
        --tb-dir logs/tb/<run> --step 400 --task track

A reward curve says a number went up. It cannot say whether the gait that earned it looks like running,
and on this project that question has decided more runs than the curves have: a policy scoring well by
shuffling, or by leaning on the edge of a joint limit, looks fine in a chart and wrong in a second of
video. So every checkpoint `train_run.py` saves can now be filmed (`--video-every-iters`), and the clip
lands in TensorBoard's IMAGES tab under `rollout/<task>`, one per saved iteration -- drag the step slider
and the athlete learns to run in front of you. A copy is written to `logs/videos/<run>/` as a GIF.

Runs on the CPU in its own process, so filming never slows training down. Rendering uses MuJoCo's own
offscreen renderer: on Windows and a desktop Linux that is GLFW and needs nothing; on a headless Linux
box set MUJOCO_GL=egl first.

The observation is `athlete_rollout.Rollout`'s, which is the trainer's exactly, so what is filmed is what
was trained. `--task getup` starts the athlete flat on its back with the command zeroed, the way
`envs/get_up.py` resets it.
"""
from __future__ import annotations

import argparse
import io
import math
import os
import sys

import mujoco
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)
from athlete_rollout import Rollout  # noqa: E402


class GetUpRollout(Rollout):
    """The same athlete, laid flat on its back, with the run-to-target command zeroed (envs/get_up.py)."""

    def reset(self) -> None:
        super().reset()
        # 90 degrees about the body's own y axis, front up: supine. 0.14 m is where this pelvis rests
        # lying flat (measured in get_up.py) plus the 4 cm the trainer drops it from.
        self.d.qpos[3:7] = [math.cos(-math.pi / 4), 0.0, math.sin(-math.pi / 4), 0.0]
        self.d.qpos[2] = 0.18
        self.d.qvel[:] = 0.0
        mujoco.mj_forward(self.m, self.d)

    def observe(self) -> np.ndarray:
        obs = super().observe()
        obs[9:12] = 0.0          # the command slice: get-up is trained with zeros here
        return obs


def film(args) -> list[np.ndarray]:
    xml = args.xml or os.path.join(ROOT, "models", "athlete.xml")
    cls = GetUpRollout if args.task == "getup" else Rollout
    r = cls(xml, args.ckpt, args.target_dist, args.seed)

    renderer = mujoco.Renderer(r.m, height=args.height, width=args.width)
    cam = mujoco.MjvCamera()
    cam.type = mujoco.mjtCamera.mjCAMERA_FREE
    cam.distance = 4.2
    cam.elevation = -14.0
    cam.azimuth = 120.0

    frames: list[np.ndarray] = []
    next_frame = 0.0
    fell_at = None
    look = np.array(r.d.xpos[r.pelvis])
    while r.d.time < args.seconds:
        s = r.step()
        if r.d.time >= next_frame:
            next_frame += 1.0 / args.fps
            # Glide the camera after the pelvis rather than locking to it: a camera bolted to the pelvis
            # turns every stride into a shake and hides the one thing the clip is for, which is the gait.
            look += (np.array(r.d.xpos[r.pelvis]) - look) * 0.25
            cam.lookat[:] = look
            cam.azimuth += 0.4
            renderer.update_scene(r.d, camera=cam)
            frames.append(renderer.render().copy())
        # A fall stays in the clip -- it is often the most informative second of it -- and after a
        # beat the athlete is reset so the rest of the clip is not a body lying still.
        if args.task != "getup" and s["fell"]:
            if fell_at is None:
                fell_at = r.d.time
            elif r.d.time - fell_at > 1.0:
                t = r.d.time
                r.reset()
                r.d.time = t
                fell_at = None
    renderer.close()
    return frames


def gif_bytes(frames: list[np.ndarray], fps: int) -> bytes:
    from PIL import Image  # Pillow: TensorBoard's own image summaries need it too
    imgs = [Image.fromarray(f) for f in frames]
    buf = io.BytesIO()
    imgs[0].save(buf, format="GIF", save_all=True, append_images=imgs[1:],
                 duration=int(round(1000 / fps)), loop=0, optimize=False)
    return buf.getvalue()


def log(tb_dir: str, tag: str, step: int, frames: list[np.ndarray], fps: int, gif: bytes) -> str:
    """Into TensorBoard as an animated image. `add_video` would need moviepy; this needs only Pillow."""
    from torch.utils.tensorboard import SummaryWriter
    from tensorboard.compat.proto.summary_pb2 import Summary
    h, w = frames[0].shape[:2]
    writer = SummaryWriter(tb_dir)
    img = Summary.Image(height=h, width=w, colorspace=3, encoded_image_string=gif)
    writer._get_file_writer().add_summary(Summary(value=[Summary.Value(tag=tag, image=img)]), step)
    writer.flush()
    writer.close()
    return tag


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", required=True)
    ap.add_argument("--tb-dir", required=True, help="the run's TensorBoard directory (logs/tb/<run>)")
    ap.add_argument("--step", type=int, required=True, help="training iteration the clip belongs to")
    ap.add_argument("--task", default="target", choices=["target", "track", "getup", "crowd"])
    ap.add_argument("--xml", default="")
    ap.add_argument("--seconds", type=float, default=6.0)
    ap.add_argument("--fps", type=int, default=20)
    ap.add_argument("--width", type=int, default=320)
    ap.add_argument("--height", type=int, default=240)
    ap.add_argument("--target-dist", type=float, default=10.0)
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    frames = film(args)
    if len(frames) < 2:
        raise SystemExit("nothing filmed")
    gif = gif_bytes(frames, args.fps)

    run = os.path.basename(os.path.normpath(args.tb_dir))
    out_dir = os.path.join(ROOT, "logs", "videos", run)
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, f"iter_{args.step:05d}.gif")
    with open(path, "wb") as fh:
        fh.write(gif)

    tag = log(args.tb_dir, f"rollout/{args.task}", args.step, frames, args.fps, gif)
    print(f"[video] {len(frames)} frames at {args.fps} fps -> TensorBoard '{tag}' step {args.step} and {path}",
          flush=True)


if __name__ == "__main__":
    main()
