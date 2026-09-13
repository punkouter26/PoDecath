"""GPU-vectorised "run to target" environment on MuJoCo Warp (sync-free step path).

Observation (matches Unity ObservationBuilder / PolicyConfig, external Z-up body frame):
    base_lin_vel(3) base_ang_vel(3) projected_gravity(3) target_command(3)
    joint_pos - default(N) joint_vel(N) last_action(N) foot_contact(2) base_height(1)
target_command = (unit direction to target in the base-yaw frame (x, y), min(dist, 10) / 10).
Action: PD joint targets, target = default + action * action_scale (radians).

`foot_contact` and `base_height` are new. A legged policy that cannot see its own feet has to infer
stance from joint angles alone, and this one previously had no idea which foot was on the ground --
which is half of why the gait never looked like walking. Both are cheap and both have an exact Unity
equivalent (a sole raycast and the pelvis height the HUD already reads).
"""
from __future__ import annotations

import json
import math
import os
from typing import Dict, Optional, Tuple

import mujoco
import mujoco_warp as mjw
import numpy as np
import torch
import warp as wp

from .domain_rand import DomainRandomizer


def quat_rotate_inverse(q: torch.Tensor, v: torch.Tensor) -> torch.Tensor:
    """q: (N,4) wxyz, v: (N,3) world -> body."""
    w, xyz = q[:, :1], q[:, 1:]
    t = 2.0 * torch.cross(xyz, v, dim=-1)
    return v - w * t + torch.cross(xyz, t, dim=-1)


def quat_yaw(q: torch.Tensor) -> torch.Tensor:
    w, x, y, z = q[:, 0], q[:, 1], q[:, 2], q[:, 3]
    return torch.atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z))


def yaw_quat(yaw: torch.Tensor) -> torch.Tensor:
    z = torch.zeros_like(yaw)
    return torch.stack([torch.cos(yaw * 0.5), z, z, torch.sin(yaw * 0.5)], dim=-1)


class RunToTargetEnv:
    def __init__(self, xml_path: str, num_envs: int, device: str = "cuda", seed: int = 0,
                 control_decimation: int = 4, episode_len_s: float = 20.0, action_scale: float = 0.5,
                 target_speed: float = 3.5, target_dist: Tuple[float, float] = (6.0, 16.0),
                 nconmax: int = 48, njmax: int = 192, verbose: bool = False,
                 domain_rand: bool = True, dr_kwargs: Optional[dict] = None,
                 cuda_graph: bool = True, action_clip: float = 3.0):
        wp.init()
        wp.config.verbose_warnings = verbose
        self.device = device
        self.N = num_envs
        self.decimation = control_decimation
        self.action_scale = action_scale
        self.target_speed = target_speed
        self.target_dist = target_dist
        # Actions used to be clamped at +-5, which with action_scale 0.5 asks for +-2.5 rad of travel
        # on joints whose entire range is under 2.6 rad and mostly under 1.5. The majority of the action
        # space therefore landed on the ctrlrange clamp, where the gradient is zero, and the policy was
        # rewarded for saturating: bang-bang control that neither looks nor is human. +-3 keeps headroom
        # past every joint limit without handing the policy a dead zone to hide in.
        self.action_clip = action_clip
        self.use_cuda_graph = cuda_graph and device.startswith("cuda")
        self.rng = torch.Generator(device=device); self.rng.manual_seed(seed)

        self.m = mujoco.MjModel.from_xml_path(xml_path)
        self.dt = float(self.m.opt.timestep) * control_decimation
        self.max_steps = int(episode_len_s / self.dt)
        d0 = mujoco.MjData(self.m)
        mujoco.mj_resetDataKeyframe(self.m, d0, 0)
        mujoco.mj_forward(self.m, d0)
        self.mw = mjw.put_model(self.m)
        self.mw.opt.warn_overflow = verbose   # device printf warnings cost ~10x throughput on fallen bodies
        self.dw = mjw.put_data(self.m, d0, nworld=num_envs, nconmax=nconmax, njmax=njmax)

        cfg_path = os.path.splitext(xml_path)[0] + "_policy_config.json"
        with open(cfg_path, "r", encoding="utf-8") as f:
            self.cfg = json.load(f)
        self.A = self.m.nu
        self.obs_dim = 12 + 3 * self.A + 3      # + foot_contact(2) + base_height(1)

        # torch views into warp data (no copies)
        self.qpos = wp.to_torch(self.dw.qpos)
        self.qvel = wp.to_torch(self.dw.qvel)
        self.ctrl = wp.to_torch(self.dw.ctrl)
        self.xquat = wp.to_torch(self.dw.xquat)     # (N, nbody, 4) wxyz
        self.xpos = wp.to_torch(self.dw.xpos)       # (N, nbody, 3)
        self.site_xpos = wp.to_torch(self.dw.site_xpos)
        self.act_force = wp.to_torch(self.dw.actuator_force)   # exact joint torques, for the energy term
        self.pelvis = mujoco.mj_name2id(self.m, mujoco.mjtObj.mjOBJ_BODY, "pelvis")

        # Feet. The sole sites sit at the centre of each foot box, so the sole plane is half the box
        # thickness below them; contact is "sole within a centimetre of the deck".
        self.foot_sites = [mujoco.mj_name2id(self.m, mujoco.mjtObj.mjOBJ_SITE, f"foot_{s}_site") for s in "lr"]
        foot_geom = mujoco.mj_name2id(self.m, mujoco.mjtObj.mjOBJ_GEOM, "foot_l_geom")
        self.sole_half = float(self.m.geom_size[foot_geom, 2])
        self.sole_contact_h = float(self.cfg.get("sole_contact_height", 0.03))
        self.swing_clear_h = 0.10          # metres of clearance asked of a swing foot
        self.stance_width = 0.20           # metres between the feet in normal running
        # Paid when the athlete arrives at its target. Tasks whose target is a moving carrot the athlete
        # can never actually reach set this to 0 rather than carrying a term that never fires.
        self.reach_bonus = 5.0

        # Human peak joint speeds, from the policy config (house rule 15). Absent on an older config,
        # in which case the penalty is simply never active.
        vl = self.cfg.get("velocity_limit")
        self.qvel_limit = (torch.tensor(vl, device=device, dtype=torch.float32)
                           if vl else torch.full((self.A,), 1e9, device=device))

        # Joint indices the gait terms need, looked up by name so a rig change cannot silently
        # renumber them into the wrong limb.
        order = self.cfg["joint_order"]
        self.i_shoulder_l = order.index("shoulder_x_l")
        self.i_shoulder_r = order.index("shoulder_x_r")
        self.i_hip_l = order.index("hip_y_l")
        self.i_hip_r = order.index("hip_y_r")

        key = torch.tensor(self.m.key_qpos[0], device=device, dtype=torch.float32)
        self.default_qpos = key
        self.default_joint = key[7:].clone()
        self.stand_height = float(key[2])
        rng = torch.tensor(self.m.jnt_range[1:], device=device, dtype=torch.float32)
        self.joint_lo, self.joint_hi = rng[:, 0], rng[:, 1]
        self.ctrl_lo = torch.tensor(self.m.actuator_ctrlrange[:, 0], device=device, dtype=torch.float32)
        self.ctrl_hi = torch.tensor(self.m.actuator_ctrlrange[:, 1], device=device, dtype=torch.float32)

        N = num_envs
        self.targets = torch.zeros(N, 2, device=device)
        self.last_action = torch.zeros(N, self.A, device=device)
        self.prev_action = torch.zeros(N, self.A, device=device)
        self.step_count = torch.zeros(N, device=device)
        self.episode_return = torch.zeros(N, device=device)
        self.episode_len = torch.zeros(N, device=device)
        self.gravity_world = torch.tensor([0.0, 0.0, -1.0], device=device).expand(N, 3)
        self.air_time = torch.zeros(N, 2, device=device)
        self.prev_air = torch.zeros(N, 2, device=device)
        self.prev_foot_xy = torch.zeros(N, 2, 2, device=device)
        self._acc = {k: torch.zeros((), device=device) for k in
                     ("ret_sum", "len_sum", "fell_sum", "done_n", "v_sum", "reach_sum", "upright_sum",
                      "steps", "air_sum", "slip_sum", "duty_sum")}

        # Built before the first reset: `_apply_reset` resamples through it, so it has to exist by then.
        self.dr = DomainRandomizer(self.mw, self.dw, num_envs, device, self.rng,
                                   dt=self.dt, **(dr_kwargs or {})) if domain_rand else None
        self._step_graph = None
        self._fwd_graph = None
        # Capture before the first reset, not after. Capturing warms the sim up by stepping it, so it
        # has to happen while the state is still throwaway -- and it must not itself call reset(), or a
        # subclass whose constructor has not finished yet (GetUpEnv sets `prev_height` after
        # super().__init__ returns) gets a second reset it is not ready for.
        self._capture_graphs()
        self.reset()

    # ---- CUDA graphs ---------------------------------------------------------------------------
    def _capture_graphs(self) -> None:
        """Capture the physics blocks once, then replay them instead of relaunching every kernel.

        `mjw.step` dispatches dozens of small kernels, and at a few thousand worlds the launch overhead
        is a real fraction of the step. The two blocks that never change shape -- the decimation loop and
        the post-reset `forward` -- are captured here and replayed with `wp.capture_launch`.

        Capture records device addresses, so every tensor the graph touches has to be written in place
        afterwards. That holds: the reset path and the domain randomiser both go through `copy_`.
        """
        if not self.use_cuda_graph:
            return
        try:
            # Warm up first: capture cannot compile kernels, so anything not yet loaded must run once.
            for _ in range(self.decimation):
                mjw.step(self.mw, self.dw)
            mjw.forward(self.mw, self.dw)
            wp.synchronize()
            with wp.ScopedCapture() as cap:
                for _ in range(self.decimation):
                    mjw.step(self.mw, self.dw)
            self._step_graph = cap.graph
            with wp.ScopedCapture() as cap:
                mjw.forward(self.mw, self.dw)
            self._fwd_graph = cap.graph
        except Exception as e:   # pragma: no cover - capture is an optimisation, never a requirement
            print(f"[cuda-graph] capture unavailable ({e}); falling back to direct launches")
            self._step_graph = self._fwd_graph = None

    def _physics_step(self) -> None:
        if self._step_graph is not None:
            wp.capture_launch(self._step_graph)
        else:
            for _ in range(self.decimation):
                mjw.step(self.mw, self.dw)

    def _physics_forward(self) -> None:
        if self._fwd_graph is not None:
            wp.capture_launch(self._fwd_graph)
        else:
            mjw.forward(self.mw, self.dw)

    # ---- helpers -------------------------------------------------------------------------------
    def _random_targets(self, pos_xy: torch.Tensor) -> torch.Tensor:
        n = pos_xy.shape[0]
        ang = torch.rand(n, generator=self.rng, device=self.device) * 2 * math.pi
        dist = self.target_dist[0] + torch.rand(n, generator=self.rng, device=self.device) * (self.target_dist[1] - self.target_dist[0])
        return pos_xy + torch.stack([torch.cos(ang), torch.sin(ang)], -1) * dist[:, None]

    def _reset_states(self) -> Tuple[torch.Tensor, torch.Tensor]:
        """Fresh (qpos, qvel) candidates for every env; applied with torch.where by the caller."""
        N = self.N
        q = self.default_qpos.unsqueeze(0).repeat(N, 1)
        yaw = (torch.rand(N, generator=self.rng, device=self.device) * 2 - 1) * math.pi
        q[:, 3:7] = yaw_quat(yaw)
        # The stand keyframe now rests the soles 2 mm off the deck, so this only needs to clear the
        # joint jitter below. It used to add 20 mm on top of a keyframe that already floated 10 mm,
        # which opened every single episode with a 3 cm free fall and an impact the policy did not cause.
        q[:, 2] += 0.002
        q[:, 7:] += (torch.rand(N, self.A, generator=self.rng, device=self.device) * 2 - 1) * 0.05
        v = torch.zeros(N, self.qvel.shape[1], device=self.device)
        v[:, :2] = (torch.rand(N, 2, generator=self.rng, device=self.device) * 2 - 1) * 0.2
        return q, v

    def _apply_reset(self, mask: torch.Tensor) -> None:
        """mask: (N,) bool. Sync-free masked reset."""
        q, v = self._reset_states()
        m1 = mask[:, None]
        self.qpos.copy_(torch.where(m1, q, self.qpos))
        self.qvel.copy_(torch.where(m1, v, self.qvel))
        self.ctrl.copy_(torch.where(m1, q[:, 7:], self.ctrl))
        zero_a = torch.zeros_like(self.last_action)
        self.last_action = torch.where(m1, zero_a, self.last_action)
        self.prev_action = torch.where(m1, zero_a, self.prev_action)
        self.step_count = torch.where(mask, torch.zeros_like(self.step_count), self.step_count)
        self.episode_return = torch.where(mask, torch.zeros_like(self.episode_return), self.episode_return)
        self.episode_len = torch.where(mask, torch.zeros_like(self.episode_len), self.episode_len)
        self.targets = torch.where(m1, self._random_targets(q[:, :2]), self.targets)
        m2 = mask[:, None].expand(-1, 2)
        self.air_time = torch.where(m2, torch.zeros_like(self.air_time), self.air_time)
        self.prev_air = torch.where(m2, torch.zeros_like(self.prev_air), self.prev_air)
        self.prev_foot_xy = torch.where(m2[:, :, None], torch.zeros_like(self.prev_foot_xy), self.prev_foot_xy)
        if self.dr is not None:
            self.dr.resample(mask)

    def _stagger(self) -> None:
        """Spread the episode clock across the population.

        Every environment otherwise starts at step 0 and, for any environment that never falls, times
        out in lockstep forever. `train_run.py` already documents the damage: most iterations finish no
        episode at all and report a fall rate of 0.0 that means "no data" rather than "nobody fell", and
        the adaptive speed curriculum is gated on exactly that number. `GetUpEnv` worked around it with
        a local copy of this method; it belongs in the base env, where every task gets it.
        """
        self.step_count = torch.randint(0, max(1, self.max_steps), (self.N,), device=self.device,
                                        generator=self.rng).float()

    def reset(self) -> torch.Tensor:
        self._apply_reset(torch.ones(self.N, dtype=torch.bool, device=self.device))
        self._physics_forward()
        self._stagger()
        self.prev_foot_xy = self.site_xpos[:, self.foot_sites, :2].clone()
        self._obs = self.compute_obs()
        return self._obs

    # ---- feet ----------------------------------------------------------------------------------
    def _foot_state(self):
        """(sole height (N,2), foot xy (N,2,2), in-contact (N,2)) -- all sync-free."""
        site = self.site_xpos[:, self.foot_sites]          # (N,2,3)
        sole_z = site[:, :, 2] - self.sole_half
        contact = sole_z < self.sole_contact_h
        return sole_z, site[:, :, :2], contact

    def _base_state(self):
        q = self.xquat[:, self.pelvis]                    # (N,4) wxyz
        pos = self.xpos[:, self.pelvis]
        lin_w = self.qvel[:, :3]
        ang_b = self.qvel[:, 3:6]                          # free joint: angular velocity in body frame
        lin_b = quat_rotate_inverse(q, lin_w)
        grav_b = quat_rotate_inverse(q, self.gravity_world)
        return q, pos, lin_w, lin_b, ang_b, grav_b

    def compute_obs(self) -> torch.Tensor:
        q, pos, lin_w, lin_b, ang_b, grav_b = self._base_state()
        yaw = quat_yaw(q)
        to_t = self.targets - pos[:, :2]
        dist = torch.norm(to_t, dim=-1, keepdim=True)
        c, s = torch.cos(yaw), torch.sin(yaw)
        dx = c * to_t[:, 0] + s * to_t[:, 1]
        dy = -s * to_t[:, 0] + c * to_t[:, 1]
        dirn = torch.stack([dx, dy], -1) / dist.clamp_min(1e-3)
        cmd = torch.cat([dirn, (dist.clamp_max(10.0) / 10.0)], -1)
        return self._assemble_obs(lin_b, ang_b, grav_b, cmd)

    def _assemble_obs(self, lin_b, ang_b, grav_b, cmd) -> torch.Tensor:
        """The shared observation layout. Every task builds the same vector with its own command."""
        _, _, contact = self._foot_state()
        base_h = self.xpos[:, self.pelvis, 2:3]
        obs = torch.cat([lin_b, ang_b, grav_b, cmd,
                         self.qpos[:, 7:] - self.default_joint,
                         self.qvel[:, 6:],
                         self.last_action,
                         contact.float(),
                         base_h], dim=-1)
        if self.dr is not None:
            obs = self.dr.noisy(obs, self.A)
        return torch.nan_to_num(obs).clamp(-100.0, 100.0)

    # ---- step ----------------------------------------------------------------------------------
    def step(self, action: torch.Tensor):
        action = action.clamp(-self.action_clip, self.action_clip)
        # The observation reports what the policy asked for; the actuators may be handed last step's
        # command instead. Unity's read -> infer -> write loop cannot land a target sooner than the
        # next physics tick, so a zero-latency trainer is training against a loop that does not exist.
        applied = self.dr.delay(action, self.last_action) if self.dr is not None else action
        self.prev_action = self.last_action
        self.last_action = action
        target = (self.default_joint + applied * self.action_scale).clamp(self.ctrl_lo, self.ctrl_hi)
        self.ctrl.copy_(target)
        if self.dr is not None:
            self.dr.maybe_push(self.qvel)
        self._physics_step()
        self.step_count = self.step_count + 1.0

        q, pos, lin_w, lin_b, ang_b, grav_b = self._base_state()
        to_t = self.targets - pos[:, :2]
        dist = torch.norm(to_t, dim=-1)
        dir_w = to_t / dist.clamp_min(1e-3)[:, None]
        v_toward = (lin_w[:, :2] * dir_w).sum(-1)
        upright = -grav_b[:, 2]                               # 1 when standing
        yaw = quat_yaw(q)
        fwd_w = torch.stack([torch.cos(yaw), torch.sin(yaw)], -1)
        heading = (fwd_w * dir_w).sum(-1)

        # ---- reward ----
        r_track = 1.5 * torch.exp(-((v_toward - self.target_speed) ** 2) / 2.0)
        r_prog = 0.25 * v_toward.clamp(-1.0, self.target_speed)
        r_alive = 0.3
        r_upright = 0.3 * upright.clamp_min(0.0)
        r_heading = 0.2 * heading
        r_height = -0.5 * (self.stand_height - 0.05 - pos[:, 2]).clamp_min(0.0)
        r_lin_z = -0.5 * lin_b[:, 2] ** 2
        r_ang = -0.03 * (ang_b[:, :2] ** 2).sum(-1)
        r_act = -0.002 * (action ** 2).sum(-1)
        r_rate = -0.02 * ((action - self.prev_action) ** 2).sum(-1)
        jp = self.qpos[:, 7:]
        r_limit = -1.0 * ((self.joint_lo + 0.05 - jp).clamp_min(0.0) + (jp - self.joint_hi + 0.05).clamp_min(0.0)).sum(-1)
        reached = dist < 0.6
        r_reach = self.reach_bonus * reached.float()

        # ---- gait ----
        # None of this existed. The reward had twelve terms and not one of them mentioned the feet, so
        # nothing ever asked for a flight phase, a foot that stays put under load, a swing foot that
        # clears the deck, or a left side that matches the right. A policy given that reward converges
        # on the cheapest thing that moves a pelvis forward, which is a shuffle. These terms are what
        # the gait is actually graded on.
        sole_z, foot_xy, contact = self._foot_state()
        foot_v = (foot_xy - self.prev_foot_xy) / self.dt
        self.prev_foot_xy = foot_xy.clone()
        moving = (v_toward.abs() > 0.5).float()

        # Air time, paid once per footfall. Rewarding contact-free time directly buys hopping; paying
        # it out only when the foot lands buys a stride.
        self.prev_air = self.air_time.clone()
        self.air_time = torch.where(contact, torch.zeros_like(self.air_time), self.air_time + self.dt)
        first_contact = contact & (self.prev_air > 0.0)
        r_air = 1.0 * ((self.prev_air - 0.25) * first_contact.float()).sum(-1) * moving

        # A foot in contact that is still moving is skating. Clamped: a body tumbling at 20 m/s would
        # otherwise score -200 here on its way down, which says nothing about its gait and swamps every
        # term that does. The same clamp reasoning applies to the energy and joint-speed terms below --
        # each is bounded so that no single penalty can dominate the reward during a fall, which the
        # termination penalty already covers.
        r_slip = -0.5 * (((foot_v ** 2).sum(-1).clamp_max(25.0)) * contact.float()).sum(-1)

        # A swing foot that never rises walks through the ground it is supposed to be clearing.
        r_clear = -0.5 * (((self.swing_clear_h - sole_z).clamp_min(0.0) ** 2) * (~contact).float()).sum(-1)

        # Keep the feet under the hips instead of straddling.
        foot_sep = (foot_xy[:, 0] - foot_xy[:, 1]).norm(dim=-1)
        r_width = -0.3 * (foot_sep - self.stance_width).abs()

        # Running is alternating. Two feet down at speed is a bunny hop.
        r_alt = -0.5 * (contact[:, 0] & contact[:, 1]).float() * moving

        # Contralateral arm swing: the shoulder opposite the driving hip. Cheap, and it is most of
        # what reads as "human" to someone watching rather than measuring. Measured as deviation from
        # the stand pose -- the shoulder's default is -78 deg, so the raw angles would be dominated by
        # that offset and the product would say nothing about swing.
        jpr = jp - self.default_joint
        sh_l, sh_r = jpr[:, self.i_shoulder_l], jpr[:, self.i_shoulder_r]
        hip_l, hip_r = jpr[:, self.i_hip_l], jpr[:, self.i_hip_r]
        r_arm = 0.3 * (-(sh_l * hip_l) - (sh_r * hip_r)).clamp(-1.0, 1.0) * moving

        # Cost of transport. The single most effective term for making a gait look economical, because
        # a real runner is solving the same problem.
        r_energy = -2e-4 * (self.act_force * self.qvel[:, 6:]).abs().clamp_max(2000.0).sum(-1)

        # House rule 15: joints move no faster than a human's.
        r_qvel = -0.02 * (self.qvel[:, 6:].abs() - self.qvel_limit).clamp(0.0, 5.0).pow(2).sum(-1)

        reward = (r_track + r_prog + r_alive + r_upright + r_heading + r_height + r_lin_z + r_ang
                  + r_act + r_rate + r_limit + r_reach
                  + r_air + r_slip + r_clear + r_width + r_alt + r_arm + r_energy + r_qvel)

        # ---- termination ----
        fell = (pos[:, 2] < self.stand_height * 0.6) | (upright < 0.4) | ~torch.isfinite(pos).all(-1)
        timeout = self.step_count >= self.max_steps
        reward = reward - 2.0 * fell.float()
        done = fell | timeout

        self.episode_return = self.episode_return + reward
        self.episode_len = self.episode_len + 1.0

        # stats accumulators (GPU only; read once per iteration)
        df = done.float()
        self._acc["ret_sum"] += (self.episode_return * df).sum()
        self._acc["len_sum"] += (self.episode_len * df).sum()
        self._acc["fell_sum"] += (fell.float() * df).sum()
        self._acc["done_n"] += df.sum()
        self._acc["v_sum"] += v_toward.mean()
        self._acc["reach_sum"] += reached.float().mean()
        self._acc["upright_sum"] += upright.mean()
        # Masked *selection* would force a device sync here; weight-and-divide stays on the GPU.
        fc = first_contact.float()
        self._acc["air_sum"] += (self.prev_air * fc).sum() / fc.sum().clamp_min(1.0)
        self._acc["slip_sum"] += (foot_v.norm(dim=-1) * contact.float()).sum(-1).mean()
        self._acc["duty_sum"] += contact.float().mean()
        self._acc["steps"] += 1.0

        # new target for envs that reached theirs (no reset), then masked resets for done envs
        self.targets = torch.where(reached[:, None], self._random_targets(pos[:, :2]), self.targets)
        self._apply_reset(done)
        self._physics_forward()
        self._obs = self.compute_obs()
        return self._obs, reward, done, timeout

    def get_stats(self) -> Dict[str, float]:
        a = {k: v.item() for k, v in self._acc.items()}
        n = max(1.0, a["done_n"]); s = max(1.0, a["steps"])
        out = {"ep_return": a["ret_sum"] / n, "ep_len_s": a["len_sum"] / n * self.dt, "fall_rate": a["fell_sum"] / n,
               "v_toward": a["v_sum"] / s, "reach_frac": a["reach_sum"] / s, "upright": a["upright_sum"] / s,
               # Gait readouts. air_time is seconds of flight per footfall (a walk is ~0, a run 0.1-0.2),
               # foot_slip is metres/second of sliding under a loaded foot (want ~0), duty_factor is the
               # fraction of time a foot is down (human walking ~0.6, running ~0.35).
               "air_time": a["air_sum"] / s, "foot_slip": a["slip_sum"] / s, "duty_factor": a["duty_sum"] / s}
        for v in self._acc.values(): v.zero_()
        return out

    # ---- for evaluation with a fixed far target (100 m dash) --------------------------------------
    def set_targets(self, xy: torch.Tensor) -> None:
        self.targets = xy.clone()
