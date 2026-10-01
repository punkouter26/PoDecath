"""
The lap, with other runners on it.

    train_run.py --task pack --resume checkpoints/run_track/<run>/latest.pt ...

`crowd.py` taught contact on the open run-to-target task with one figure walking at the athlete
head-on. A race is not that: everybody is going the same way round the same loop, and what knocks a
runner down is running up the back of somebody slower, being clipped from behind by somebody faster,
or being leaned on from the next lane. Measured on the 8-runner race on the phone, 2026-09-30:
3 of 8 down between 42 m and 46 m, every one of them from another athlete.

This is `RunTrackEnv` -- same carrot, same deck, same 80 observations, so a lap checkpoint resumes
straight into it and the export drops into the lap policy's slot -- plus `pacemakers` athlete-shaped
figures per environment that share the loop:

    ahead   (45 %)  2-5 m in front, slower than the athlete, so it runs up their back
    behind  (30 %)  2-4 m back, faster by up to a shove's worth, so it is clipped from behind
    beside  (25 %)  alongside, drifting in until it is leaning on the athlete's line

Speeds are set off what the athlete is actually doing rather than off the commanded pace, so the
encounters stay encounters at any stage of a speed curriculum. Each figure lives 3-6 s and then
re-forms somewhere else around the athlete, which is what turns one bump per episode into a pack.

The figures are MuJoCo *mocap* bodies, as in `crowd.py`: no joints, no state of their own, a pose
written every control step. That keeps the model's state byte-for-byte the ordinary athlete's, and
it means contact arrives through the physics and never through the observation -- the condition the
game presents. They are immovable, which a real runner is not, so `set_threat` holds the closing
speeds to what one athlete can do to another (a 0.8 m/s clip at most) rather than what a wall can.
"""
import os
import shutil

import torch
import warp as wp

from .run_track import RunTrackEnv

# The same standing figure crowd.py uses. contype=2 keeps the figures from colliding with each other
# while still hitting the athlete, whose geoms are contype=1, conaffinity=1.
_FIGURE = """  <body name="pacemaker{k}" mocap="true" pos="{x} 0 0.95">
    <geom name="pm{k}_head" type="sphere" size="0.11" pos="0 0 0.60" contype="2" conaffinity="1" rgba="0.85 0.25 0.2 0.35"/>
    <geom name="pm{k}_torso" type="capsule" size="0.13" fromto="0 0 -0.10 0 0 0.35" contype="2" conaffinity="1" rgba="0.85 0.25 0.2 0.35"/>
    <geom name="pm{k}_legs" type="capsule" size="0.09" fromto="0 0 -0.85 0 0 -0.10" contype="2" conaffinity="1" rgba="0.85 0.25 0.2 0.35"/>
  </body>
"""


def _inject(xml_text: str, count: int) -> str:
    assert "</worldbody>" in xml_text
    # Parked well apart and well away from the stand keyframe until the first pose is written.
    figures = "".join(_FIGURE.format(k=k, x=30 + 2 * k) for k in range(count))
    return xml_text.replace("</worldbody>", figures + "</worldbody>", 1)


class PackTrackEnv(RunTrackEnv):
    """Laps of the rooftop loop with kinematic runners to be bumped by."""

    def __init__(self, xml_path: str, num_envs: int, device="cuda", seed: int = 0,
                 pacemakers: int = 2, **kw):
        base = os.path.splitext(os.path.abspath(xml_path))[0]
        injected_path = base + "_pack.xml"
        with open(xml_path, "r", encoding="utf-8") as fh, open(injected_path, "w", encoding="utf-8") as out:
            out.write(_inject(fh.read(), pacemakers))
        # The base env reads its policy contract from "<xml>_policy_config.json".
        shutil.copyfile(base + "_policy_config.json", base + "_pack_policy_config.json")

        # The plan exists before the base constructor because its first reset routes into
        # _apply_reset below. The mocap views only bind after super().__init__.
        K = self.K = pacemakers
        z = lambda: torch.zeros(num_envs, K, device=device)
        self.pm_s, self.pm_lat, self.pm_goal = z(), z(), z()      # arc length, lateral offset, lateral it drifts to
        self.pm_v, self.pm_latv, self.pm_life = z(), z(), z()     # pace, drift rate, seconds left
        self.pm_threat = 0.3                                      # ramps to 1.0 with the run; see set_threat
        self.mocap_pos = None
        self.mocap_quat = None
        super().__init__(injected_path, num_envs, device=device, seed=seed, **kw)
        self.mocap_pos = wp.to_torch(self.dw.mocap_pos)      # (N, K, 3)
        self.mocap_quat = wp.to_torch(self.dw.mocap_quat)    # (N, K, 4) wxyz
        self._acc["near_sum"] = torch.zeros((), device=self.device)
        self._write_pose()

    # ---- the plan ------------------------------------------------------------------------------

    def set_threat(self, frac: float) -> None:
        """0..1, called from the training loop alongside the domain-randomisation ramp. Scales how
        much slower the figure ahead is, how hard the one behind arrives and how fast the one
        beside leans in."""
        self.pm_threat = float(min(1.0, max(0.25, frac)))

    def _wrap(self, ds: torch.Tensor) -> torch.Tensor:
        return torch.remainder(ds + self.lap * 0.5, self.lap) - self.lap * 0.5

    def _athlete_on_track(self):
        """(lateral offset from the centre line (N,), speed along it (N,)), from the free joint."""
        pos_c, tan = self.centerline(self._s)
        normal = torch.stack([-tan[:, 1], tan[:, 0]], -1)
        lat = ((self.qpos[:, :2] - pos_c) * normal).sum(-1)
        along = (self.qvel[:, :2] * tan).sum(-1)
        return lat, along

    def _respawn(self, mask: torch.Tensor) -> None:
        """mask: (N, K) bool. Re-forms those figures around the athlete."""
        N, K = mask.shape
        r = lambda: torch.rand(N, K, generator=self.rng, device=self.device)
        th = self.pm_threat
        lat0, along = self._athlete_on_track()
        lat0 = lat0[:, None]
        pace = along.clamp_min(1.0)[:, None]

        kind = r()
        ahead = kind < 0.45
        behind = (kind >= 0.45) & (kind < 0.75)
        side = torch.where(r() < 0.5, -torch.ones(N, K, device=self.device), torch.ones(N, K, device=self.device))

        ds = torch.where(ahead, 2.0 + 3.0 * r(), torch.where(behind, -(2.0 + 2.0 * r()), (r() * 2 - 1) * 0.4))
        v = torch.where(ahead, pace * (1.0 - (0.1 + 0.4 * r()) * th),
                        torch.where(behind, pace + (0.2 + 0.6 * r()) * th, pace * (0.97 + 0.06 * r())))
        # In line: within half a metre of the athlete's own line, so about half are square hits and
        # half are glancing. A soft threat spreads them wider, so early episodes mostly brush past.
        inline = lat0 + (r() * 2 - 1) * (0.5 + 0.5 * (1.0 - th))
        beside = lat0 + side * (0.75 + 0.25 * r())
        lat = torch.where(ahead | behind, inline, beside)
        # Beside: drift in to 0.3 m off the athlete's line -- a shoulder in their space -- and stay.
        goal = torch.where(ahead | behind, lat, lat0 + side * 0.3)
        edge = self.deck_half - 0.2
        lat, goal = lat.clamp(-edge, edge), goal.clamp(-edge, edge)
        latv = (0.15 + 0.35 * r()) * th
        life = 3.0 + 3.0 * r()

        self.pm_s = torch.where(mask, torch.remainder(self._s[:, None] + ds, self.lap), self.pm_s)
        self.pm_v = torch.where(mask, v, self.pm_v)
        self.pm_lat = torch.where(mask, lat, self.pm_lat)
        self.pm_goal = torch.where(mask, goal, self.pm_goal)
        self.pm_latv = torch.where(mask, latv, self.pm_latv)
        self.pm_life = torch.where(mask, life, self.pm_life)

    def _figure_xy(self):
        N, K = self.pm_s.shape
        pos, tan = self.centerline(self.pm_s.reshape(-1))
        normal = torch.stack([-tan[:, 1], tan[:, 0]], -1)
        xy = (pos + normal * self.pm_lat.reshape(-1, 1)).reshape(N, K, 2)
        return xy, torch.atan2(tan[:, 1], tan[:, 0]).reshape(N, K)

    def _write_pose(self) -> None:
        """Writes the plan into the mocap poses. The figures are on rails: their pose is set, never
        simulated, so between control steps they cannot be pushed off their line."""
        if self.mocap_pos is None:
            return   # called from the constructor's first reset, before the mocap views exist
        xy, yaw = self._figure_xy()
        half = yaw * 0.5
        self.mocap_pos[:, :, :2] = xy
        self.mocap_pos[:, :, 2] = 0.95
        self.mocap_quat[:, :, 0] = half.cos()
        self.mocap_quat[:, :, 1] = 0.0
        self.mocap_quat[:, :, 2] = 0.0
        self.mocap_quat[:, :, 3] = half.sin()

    # ---- overrides -----------------------------------------------------------------------------

    def _apply_reset(self, mask: torch.Tensor) -> None:
        super()._apply_reset(mask)
        self._respawn(mask[:, None].expand(-1, self.K))
        self._write_pose()

    def step(self, action: torch.Tensor):
        self.pm_s = torch.remainder(self.pm_s + self.pm_v * self.dt, self.lap)
        reach = self.pm_latv * self.dt
        self.pm_lat = self.pm_lat + torch.maximum(torch.minimum(self.pm_goal - self.pm_lat, reach), -reach)
        self.pm_life = self.pm_life - self.dt
        # Out of time, or the athlete and the figure have parted company: re-form it nearby.
        self._respawn((self.pm_life <= 0.0) | (self._wrap(self.pm_s - self._s[:, None]).abs() > 8.0))
        self._write_pose()
        out = super().step(action)
        # How much of the time somebody is actually on top of the athlete: a figure's centre within
        # 0.45 m of the pelvis is shoulder to shoulder. Without this a quiet run and a run that has
        # learned to cope are the same curve.
        xy, _ = self._figure_xy()
        d = (xy - self.xpos[:, self.pelvis, None, :2]).norm(dim=-1)
        self._acc["near_sum"] += (d < 0.45).any(-1).float().mean()
        return out

    def get_stats(self):
        steps = max(1.0, float(self._acc["steps"].item()))
        near = float(self._acc["near_sum"].item())
        out = super().get_stats()
        out["pack_contact"] = near / steps
        out["pack_threat"] = self.pm_threat
        return out
