"""Domain randomisation for the MuJoCo Warp tasks.

Why this exists
---------------
Every policy in this project is trained in MuJoCo and executed in Unity on PhysX. Until this module
existed the training side had *no* randomisation at all: one friction value, one mass set, one pair of
PD gains, noiseless observations and a zero-latency control loop. A policy trained that way is not
learning to stand up, it is learning to stand up *in MuJoCo* -- and the measured result was exactly
that. `athlete_getup.onnx` holds a stand in essentially every MuJoCo episode and, dropped into Unity,
drives uprightness from 0.03 to about 0.35 and falls back down.

Get-up is the task where this bites hardest, and not by accident. A running policy spends its life with
two feet on the deck; a get-up starts with a whole humanoid lying on it -- torso, both arms, both legs,
head -- and every newton it uses to leave the floor is routed through a contact. Contact is precisely
where MuJoCo's soft constraint solver and PhysX's iterative rigid solver disagree most, so the get-up
policy is the one most tightly overfitted to its trainer.

What is randomised, and why each one earns its place
----------------------------------------------------
* **PD gains (kp, kv)** -- the single most important term for this project. MuJoCo drives joints with a
  `<position>` actuator; Unity drives them with an `ArticulationDrive` in Force mode, whose stiffness is
  per-radian and therefore nominally the same number. "Nominally" is doing real work in that sentence:
  MuJoCo integrates actuator damping implicitly (`implicitfast`), PhysX does not, and at the gains this
  rig uses -- kp 200, kv 10 on the hips and knees -- that is a visible difference in how hard a joint
  actually pulls. A policy that has only ever felt one gain set treats its actuators as exact; one
  trained across a spread has to close the loop with feedback instead.
* **Sliding friction** -- a get-up is a sequence of pushes against the floor. MuJoCo's friction pyramid
  and PhysX's average-combine of two 1.0 materials are not the same contact.
* **Body mass** -- cheap insurance against inertia and centre-of-mass differences between the MJCF and
  the imported ArticulationBody chain.
* **Action latency** -- Unity reads joint state, runs inference, then writes drive targets, and the
  result lands on the next physics tick. The trainer applied actions instantly. A policy tuned on a
  zero-latency loop can rely on reactions the real loop cannot deliver.
* **Observation noise** -- ArticulationBody joint velocities are differentiated by the solver and are
  measurably noisier than MuJoCo's `qvel`. Noiseless training produces policies that trust velocity.
* **Pushes** -- an unmodelled shove is the cheapest proxy there is for "the dynamics are not what you
  think", and it is what stops a policy from riding one exact trajectory.

Everything is per-environment and resampled on reset, so a single training run covers the spread rather
than visiting one point of it per run. `mujoco_warp` stores model fields with a leading world axis
(size 1 when shared); expanding that axis to `num_envs` is what makes per-env physics possible at all.
"""
from __future__ import annotations

from typing import Optional

import torch
import warp as wp


class DomainRandomizer:
    """Per-environment physics, sensing and actuation randomisation.

    Ranges are multiplicative on the model's own values, so the MJCF stays the single source of truth
    for the nominal rig and this class only ever describes *spread*.
    """

    def __init__(self, mw, dw, num_envs: int, device: str, rng: torch.Generator,
                 friction=(0.7, 1.3), mass=(0.9, 1.1), kp=(0.8, 1.25), kv=(0.8, 1.25),
                 obs_noise: float = 1.0, action_delay_prob: float = 0.15,
                 push_interval_s: float = 2.0, push_vel: float = 0.6, dt: float = 0.02):
        self.mw, self.dw, self.N, self.device, self.rng = mw, dw, num_envs, device, rng
        # Full-strength targets. `strength` interpolates between "nominal model" and these; see
        # set_strength for why that dial exists.
        self._friction_full, self._mass_full = friction, mass
        self._kp_full, self._kv_full = kp, kv
        self._obs_noise_full = obs_noise
        self._delay_full = action_delay_prob
        self._push_full = push_vel
        self.push_every = max(1, int(round(push_interval_s / dt)))
        self.strength = 1.0
        self._apply_strength()

        # Expand the shared (world-axis == 1) model fields to one row per environment. Keep a reference
        # to every tensor handed to warp: `wp.from_torch` does not take ownership, so dropping these
        # would free the memory the model is reading from.
        self._own = {}
        self.geom_friction = self._expand("geom_friction")
        self.body_mass = self._expand("body_mass")
        self.gainprm = self._expand("actuator_gainprm")
        self.biasprm = self._expand("actuator_biasprm")

        # Nominal values, kept so each resample writes base * scale rather than compounding scale on
        # scale -- resampling in place would random-walk the whole population away from the MJCF.
        self.base_friction = self.geom_friction.clone()
        self.base_mass = self.body_mass.clone()
        self.base_gain = self.gainprm.clone()
        self.base_bias = self.biasprm.clone()

        self._step = 0
        self.resample(torch.ones(num_envs, dtype=torch.bool, device=device))

    # ---- strength ------------------------------------------------------------------------------
    def set_strength(self, s: float) -> None:
        """Scale every range between "nominal model" (0) and the configured spread (1).

        Randomisation is not free: it makes the task harder, and a task that is too hard at the start
        is not learned slowly, it is learned wrongly. Get-up has a well-documented local optimum --
        lie still in the most upright posture the body can hold and collect shaping reward forever --
        and full-strength randomisation from the first step walks straight into it. Measured on this
        rig: return climbed 23 -> 484 over 300 iterations while `stood` *fell* from 0.15 to 0.07 and
        the action noise collapsed from 0.80 to 0.67, which is that optimum exactly.

        So the policy learns to stand in near-nominal physics first and is hardened afterwards. The
        trainer moves this each iteration.
        """
        self.strength = max(0.0, float(s))
        self._apply_strength()

    def _apply_strength(self) -> None:
        k = self.strength
        lerp = lambda rng: (1.0 - (1.0 - rng[0]) * k, 1.0 + (rng[1] - 1.0) * k)
        self.friction_rng = lerp(self._friction_full)
        self.mass_rng = lerp(self._mass_full)
        self.kp_rng = lerp(self._kp_full)
        self.kv_rng = lerp(self._kv_full)
        self.obs_noise = self._obs_noise_full * k
        self.action_delay_prob = self._delay_full * k
        self.push_vel = self._push_full * k

    # ---- setup ---------------------------------------------------------------------------------
    def _expand(self, field: str) -> torch.Tensor:
        arr = getattr(self.mw, field)
        t = wp.to_torch(arr)
        if t.shape[0] == self.N:
            return t
        if t.shape[0] != 1:
            raise ValueError(f"{field} has world axis {t.shape[0]}, expected 1 or {self.N}")
        big = t.repeat(self.N, *([1] * (t.dim() - 1))).contiguous()
        self._own[field] = big
        setattr(self.mw, field, wp.from_torch(big, dtype=arr.dtype))
        return wp.to_torch(getattr(self.mw, field))

    def _u(self, n: int, lo: float, hi: float, *shape) -> torch.Tensor:
        r = torch.rand(n, *shape, generator=self.rng, device=self.device)
        return lo + (hi - lo) * r

    # ---- per-reset randomisation ---------------------------------------------------------------
    def resample(self, mask: torch.Tensor) -> None:
        """Draw fresh physics for every environment selected by `mask` (N,) bool."""
        m1 = mask[:, None]

        # Sliding friction only (index 0). Torsional and rolling friction stay at the MJCF values --
        # Unity's PhysicMaterial has no equivalent of either, so randomising them models nothing.
        f = self.base_friction.clone()
        f[:, :, 0] = f[:, :, 0] * self._u(self.N, *self.friction_rng, f.shape[1])
        self.geom_friction.copy_(torch.where(m1[:, :, None], f, self.geom_friction))

        m = self.base_mass * self._u(self.N, *self.mass_rng, self.base_mass.shape[1])
        self.body_mass.copy_(torch.where(m1, m, self.body_mass))

        # A MuJoCo <position> actuator is gainprm[0] = kp, biasprm[1] = -kp, biasprm[2] = -kv. kp has to
        # move in both arrays together or the actuator stops being a PD controller and becomes a spring
        # with a mismatched set point.
        s_kp = self._u(self.N, *self.kp_rng, self.base_gain.shape[1])
        s_kv = self._u(self.N, *self.kv_rng, self.base_gain.shape[1])
        g = self.base_gain.clone()
        b = self.base_bias.clone()
        g[:, :, 0] = g[:, :, 0] * s_kp
        b[:, :, 1] = b[:, :, 1] * s_kp
        b[:, :, 2] = b[:, :, 2] * s_kv
        self.gainprm.copy_(torch.where(m1[:, :, None], g, self.gainprm))
        self.biasprm.copy_(torch.where(m1[:, :, None], b, self.biasprm))

    # ---- per-step randomisation ----------------------------------------------------------------
    def delay(self, action: torch.Tensor, prev: torch.Tensor) -> torch.Tensor:
        """Hold the previous action on a random subset: a one-control-step actuation latency.

        Unity cannot do better than one step -- state is read, inference runs, and the drive target it
        produces is not felt until the next physics tick -- so a policy trained at zero latency is
        trained against a loop that does not exist.
        """
        if self.action_delay_prob <= 0.0:
            return action
        hold = torch.rand(self.N, 1, generator=self.rng, device=self.device) < self.action_delay_prob
        return torch.where(hold, prev, action)

    def noisy(self, obs: torch.Tensor, A: int) -> torch.Tensor:
        """Additive sensor noise, scaled per observation group.

        The scales follow the Isaac Lab defaults for a position-controlled humanoid, which is the same
        sensing model Unity presents: angular velocity and joint velocity are the noisy channels,
        joint position and the gravity projection are comparatively clean.
        """
        if self.obs_noise <= 0.0:
            return obs
        s = self.obs_noise
        n = torch.randn(obs.shape, generator=self.rng, device=self.device)
        scale = torch.zeros(obs.shape[1], device=self.device)
        scale[0:3] = 0.10 * s                  # base linear velocity
        scale[3:6] = 0.20 * s                  # base angular velocity
        scale[6:9] = 0.05 * s                  # projected gravity
        scale[9:12] = 0.0                      # command: Unity feeds this exactly
        scale[12:12 + A] = 0.01 * s            # joint position
        scale[12 + A:12 + 2 * A] = 1.5 * s     # joint velocity
        scale[12 + 2 * A:] = 0.0               # last action: the policy's own output, known exactly
        return obs + n * scale

    def maybe_push(self, qvel: torch.Tensor) -> None:
        """Every `push_every` control steps, kick each base with a random horizontal velocity."""
        self._step += 1
        if self.push_vel <= 0.0 or self._step % self.push_every:
            return
        kick = (torch.rand(self.N, 2, generator=self.rng, device=self.device) * 2 - 1) * self.push_vel
        qvel[:, :2] += kick
