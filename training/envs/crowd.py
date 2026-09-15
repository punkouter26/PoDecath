"""
Crowded running: the athlete shares the deck with a pacemaker — a rigid, athlete-shaped obstacle that
runs the same line, so a policy learns what contact does before the game turns collisions on.

    train_run.py --task crowd --resume checkpoints/run_track/lap_v80/latest.pt --iters 6800 ...

The observation is UNCHANGED — the same 80 floats as run-to-target — so an existing checkpoint resumes
and the exported ONNX drops straight into the game. The pacemaker is a MuJoCo *mocap* body: no joints,
no qpos of its own, its pose written every control step — a moving wall with the athlete's shape, at
infinite mass, that the athlete must give way to. It spawns on the athlete's line of travel, head-on
(60%) or overtaking-slow (40%), so contact is a certainty the policy has to absorb, not an accident.

Because the pacemaker is a mocap body the model's state is byte-for-byte the ordinary athlete's
(nq 28, nv 27, nu 21), which is why nothing in the base environment needs overriding: contact arrives
through the physics, never through the observation — exactly the condition the game presents.
"""
import os
from typing import Tuple  # noqa: F401

import torch
import warp as wp

from .run_to_target import RunToTargetEnv

# A standing figure: head, torso, legs. contype=2 keeps the pacemaker's own geoms from colliding with
# each other while still hitting the athlete (whose geoms are contype=1, conaffinity=1).
_MANNEQUIN = """  <body name="pacemaker" mocap="true" pos="0 0 0.95">
    <geom name="pm_head" type="sphere" size="0.11" pos="0 0 0.60" contype="2" conaffinity="1" rgba="0.85 0.25 0.2 0.35"/>
    <geom name="pm_torso" type="capsule" size="0.13" fromto="0 0 -0.10 0 0 0.35" contype="2" conaffinity="1" rgba="0.85 0.25 0.2 0.35"/>
    <geom name="pm_legs" type="capsule" size="0.09" fromto="0 0 -0.85 0 0 -0.10" contype="2" conaffinity="1" rgba="0.85 0.25 0.2 0.35"/>
  </body>
"""


def _inject(xml_text: str) -> str:
    assert "</worldbody>" in xml_text
    return xml_text.replace("</worldbody>", _MANNEQUIN + "</worldbody>", 1)


class CrowdEnv(RunToTargetEnv):
    """Run-to-target with a kinematic pacemaker crossing the athlete's path."""

    def __init__(self, xml_path: str, num_envs: int, device="cuda", seed: int = 0, **kw):
        with open(xml_path, "r", encoding="utf-8") as fh:
            injected_path = os.path.join(os.path.dirname(os.path.abspath(xml_path)), "athlete_crowd.xml")
            with open(injected_path, "w", encoding="utf-8") as out:
                out.write(_inject(fh.read()))
        # The plan tensors exist before the base constructor because its very first reset routes into
        # _apply_reset below. The mocap views only bind after super().__init__.
        self.pm_xy = torch.zeros(num_envs, 2, device=device)
        self.pm_vel = torch.zeros(num_envs, 2, device=device)
        self.pm_theta = torch.zeros(num_envs, device=device)
        self.pm_threat = 0.3            # ramps to 1.0 with the run; see set_threat
        self.mocap_pos = None
        self.mocap_quat = None
        super().__init__(injected_path, num_envs, device=device, seed=seed, **kw)
        # Mocap pose, per environment: warp arrays of vec3 shaped (N, nmocap).
        self.mocap_pos = wp.to_torch(self.dw.mocap_pos)      # (N, 1, 3)
        self.mocap_quat = wp.to_torch(self.dw.mocap_quat)    # (N, 1, 4) wxyz
        self._write_pose()
        # Per-env pacemaker plan, in the world frame.
        self.pm_xy = torch.zeros(num_envs, 2, device=self.device)
        self.pm_vel = torch.zeros(num_envs, 2, device=self.device)
        self.pm_theta = torch.zeros(num_envs, device=self.device)

    # ---- state spawn ---------------------------------------------------------------------------

    def _apply_reset(self, mask: torch.Tensor) -> None:
        super()._apply_reset(mask)
        # The pacemaker spawns on the athlete's line of travel — fresh spawn position to fresh target —
        # 4-8 m out, at most half a metre off the line. Head-on most of the time; otherwise it trundles
        # the same way at three-fifths pace so the athlete overtakes straight into it.
        N = self.N
        pos = self.qpos[:, :2]
        dirn = self.targets[:, :2] - pos
        dirn = dirn / dirn.norm(dim=-1, keepdim=True).clamp_min(1e-3)
        dist = 4.0 + torch.rand(N, generator=self.rng, device=self.device) * 4.0
        dist = dist / (0.5 + 0.5 * self.pm_threat)
        side = (torch.rand(N, generator=self.rng, device=self.device) * 2 - 1) * 0.5
        perp = torch.stack([-dirn[:, 1], dirn[:, 0]], -1)
        pm_xy = pos + dirn * dist[:, None] + perp * side[:, None]
        head_on = torch.rand(N, generator=self.rng, device=self.device) < 0.6
        ts = torch.full_like(dist, float(self.target_speed)) * self.pm_threat
        speed = torch.where(head_on, ts, ts * 0.6)
        # Softer threat also means farther away: early episodes are near-clean runs that end with a
        # distant bump, and the pacemaker closes in as the threat ramps.
        theta = torch.where(head_on,
                            torch.atan2(-dirn[:, 1], -dirn[:, 0]),
                            torch.atan2(dirn[:, 1], dirn[:, 0]))
        vel = torch.where(head_on[:, None], -dirn, dirn * 0.6) * speed[:, None]
        m = mask[:, None]
        self.pm_xy = torch.where(m, pm_xy, self.pm_xy)
        self.pm_vel = torch.where(m, vel, self.pm_vel)
        self.pm_theta = torch.where(mask, theta, self.pm_theta)
        self._write_pose()

    # ---- stepping ------------------------------------------------------------------------------

    def set_threat(self, frac: float) -> None:
        """0..1, called from the training loop alongside the domain-randomisation ramp. Scales how hard
        the pacemaker hits (its speed) and how soon it arrives (its spawn distance)."""
        self.pm_threat = float(min(1.0, max(0.25, frac)))

    def _write_pose(self) -> None:
        """Writes the plan into the mocap pose. The pacemaker is a wall on rails: its pose is set,
        never simulated, so between control steps it cannot be pushed off its line."""
        if self.mocap_pos is None:
            return   # called from the constructor's first reset, before the mocap views exist
        half = self.pm_theta * 0.5
        zero = torch.zeros_like(half)
        quat = torch.stack([half.cos(), zero, zero, half.sin()], -1)
        z = torch.full_like(self.pm_theta, 0.95)
        self.mocap_pos[:, 0, :2] = self.pm_xy
        self.mocap_pos[:, 0, 2] = z
        self.mocap_quat[:, 0, :] = quat

    def step(self, action: torch.Tensor):
        self.pm_xy += self.pm_vel * self.dt
        self._write_pose()
        return super().step(action)
