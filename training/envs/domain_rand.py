"""Domain randomisation for the MuJoCo Warp tasks.

Why this exists -- and what it is *not* for
-------------------------------------------
Every policy here is trained in MuJoCo and executed in Unity on PhysX, and until this module existed
the training side had no randomisation at all: one friction value, one mass set, one pair of PD gains,
noiseless observations and a control loop with zero latency. That is worth fixing on its own terms. A
policy that has only ever felt one set of dynamics treats its actuators as exact rather than closing
the loop on feedback, and the Isaac Lab twin of this same body has always randomised friction, mass,
pushes and observation noise (see AGENTS.md) while the MuJoCo run had none.

This module was originally written to fix something else, and that reason was wrong. The project's
headline open item was that `athlete_getup.onnx` "does not transfer" to PhysX, and this looked like
the textbook cause. It was not. Measured afterwards with the same policy, on a supine athlete in
RooftopRace: peak uprightness 0.947, stand held for 7.44 s out of 8. The policy had always transferred.
What failed was RecoveryController, whose floor raycast hit the athlete's own chest collider and so
could never satisfy its standing test -- it booked a DNF for every athlete that stood up. One layer
mask.

So: randomisation here buys robustness margin. It is not load bearing for sim-to-sim transfer on this
rig, and a run that omits it is not thereby broken. Reach for it when a policy needs to survive
dynamics it has not seen, not as a reflex when something downstream looks wrong -- measure first.

What is randomised, and why each one earns its place
----------------------------------------------------
* **PD gains (kp, kv)** -- MuJoCo drives joints with a `<position>` actuator and Unity with an
  `ArticulationDrive` in Force mode, whose stiffness is per-radian and therefore nominally the same
  number. MuJoCo integrates actuator damping implicitly (`implicitfast`) and PhysX does not, so at the
  gains this rig uses -- kp 200, kv 10 on the hips and knees -- the two differ in how hard a joint
  actually pulls.
* **Sliding friction** -- a get-up is a sequence of pushes against the floor, and MuJoCo's friction
  pyramid is not PhysX's average-combine of two 1.0 materials.
* **Body mass** -- inertia and centre-of-mass differences between the MJCF and the imported
  ArticulationBody chain.
* **Action latency** -- Unity reads joint state, runs inference, then writes drive targets, and the
  result lands on the next physics tick. The trainer applied actions instantly.
* **Observation noise** -- ArticulationBody joint velocities are differentiated by the solver and are
  measurably noisier than MuJoCo's `qvel`.
* **Pushes** -- the cheapest proxy for "the dynamics are not what you think", and what stops a policy
  riding one exact trajectory.

Everything is per-environment and resampled on reset, so one run covers the spread rather than visiting
one point of it. `mujoco_warp` stores model fields with a leading world axis (size 1 when shared);
expanding that axis to `num_envs` is what makes per-env physics possible at all.
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

        Randomisation is not free: every bit of it widens the distribution the policy has to solve,
        and get-up is already a hard exploration problem. The shape of that problem is on record in
        `training/logs/train_getup.log`, the run that produced the working MuJoCo policy:

            iter  230   return  514   stood 0.03   action std 0.61     <- looks like failure
            iter  590   return  928   stood 0.04   action std 0.87     <- entropy re-widens
            iter  710   return 1141   stood 0.35
            iter 1520   return 1774   stood 1.00   hold 0.13           <- breakthrough
            iter 1880   return 2616   stood 1.00   hold 0.59

        The first five hundred iterations *look* exactly like the lying-down local optimum -- return
        climbing while the policy stands less and its action noise collapses -- and are not. That is
        the task finding the basin before it finds the way out, and the escape depends on the entropy
        bonus re-widening the policy around iteration 500-700.

        That window is the reason for this dial. Full-strength randomisation applied across it is
        noise added to the one phase that has to stay explorable. Ramping keeps the physics near
        nominal while the policy learns to stand at all, then hardens it once standing is reliable.

        (Read the log before concluding a run has failed. A run of this task was killed at iteration
        330 on the belief that a falling `stood` meant the lying-down trap; the successful run's own
        curve, quoted above, was lower at the same point.)
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
