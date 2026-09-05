"""Get up off the deck.

The one thing every athlete in this project cannot do. A fall ends a runner's race outright — `DashEvent`
books it as a DNF and the broadcast cuts to the incident — because there has never been a policy that could
put the body back on its feet. This is that task.

It reuses `RunToTargetEnv` wholesale: the same MuJoCo Warp model, the same sync-free masked resets, the same
PD action path, and above all the **same observation contract**, so the exported ONNX drops into Unity's
existing `PolicyRunner` with no changes to `ObservationBuilder` at all. The only difference is the three
command values: run-to-target puts a direction and a distance there, and this task puts zeros, because
there is nowhere to go. Unity feeds the same zeros when it switches an athlete to `athlete_getup.onnx`.

What is genuinely different is the three things that define the task:

  * **Where an episode starts.** Not standing — and not, at first, flat on its back either. Starting poses
    are drawn from a tilt range that begins narrow (a stumble it can still catch) and widens toward "flat
    on the deck" only as the policy proves it can hold a stand.

    That curriculum is not decoration. Trained against the full range from the first step, this task has a
    local optimum that is very easy to find and very hard to leave: lie still in the most upright, highest
    posture the body can manage and collect the continuous shaping forever. A run left in it for 400
    iterations climbed its return from 124 to 584 while never once holding a stand, and its action noise
    collapsed from 0.80 to 0.45 — converging, confidently, on lying down well. The narrow start makes
    standing reachable by accident before the policy has committed to anything else.

  * **What is rewarded.** Height and uprightness, then stillness once it is up. A get-up policy that stands
    and immediately falls over again has not solved anything, so the standing bonus is a hold: it has to
    stay up for a second before the big payout lands.

  * **What ends an episode.** Only the clock. Run-to-target terminates on a fall and penalises it, which is
    exactly backwards here — falling over is the starting condition, and a policy that is terminated for
    being on the ground can never learn to leave it.
"""
from __future__ import annotations

import math

import mujoco_warp as mjw
import torch

from .run_to_target import RunToTargetEnv, yaw_quat


def quat_mul(a: torch.Tensor, b: torch.Tensor) -> torch.Tensor:
    """Hamilton product, wxyz, batched."""
    aw, ax, ay, az = a[:, 0], a[:, 1], a[:, 2], a[:, 3]
    bw, bx, by, bz = b[:, 0], b[:, 1], b[:, 2], b[:, 3]
    return torch.stack([
        aw * bw - ax * bx - ay * by - az * bz,
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
    ], dim=-1)


class GetUpEnv(RunToTargetEnv):
    # Margin, in degrees, between the shallowest starting pose and the pose that would already satisfy the
    # standing test. Kept as a margin rather than an absolute floor because the floor depends on
    # stand_upright: at 0.9 the boundary is acos(0.9) = 25.8 degrees, but raise or lower that threshold and
    # the boundary moves with it. A hard-coded floor would silently stop protecting anything.
    START_TILT_MARGIN_DEG = 5.0

    def __init__(self, xml_path: str, num_envs: int,
                 episode_len_s: float = 6.0,
                 tilt_range=(35.0, 75.0),
                 tilt_range_final=(40.0, 180.0),
                 curriculum_hold: float = 0.06,
                 curriculum_step: float = 8.0,
                 stand_upright: float = 0.9,
                 stand_height_frac: float = 0.82,
                 hold_seconds: float = 1.0,
                 nconmax: int = 96, njmax: int = 320,
                 **kw):
        # Set before super().__init__, because RunToTargetEnv's constructor calls reset(), which calls the
        # overrides below. Same pattern RunTrackEnv uses.
        self.stand_upright = stand_upright
        # Every starting pose has to be tilted past the standing test, or an episode begins already standing
        # and the task's headline metric counts bodies that were never on the ground. That boundary is
        # acos(stand_upright), so it is computed from it rather than assumed.
        floor_deg = math.degrees(math.acos(min(0.999, max(-0.999, stand_upright)))) + self.START_TILT_MARGIN_DEG
        self.tilt_lo = math.radians(max(tilt_range[0], floor_deg))
        self.tilt_hi = math.radians(max(tilt_range[1], floor_deg + 10.0))
        self.tilt_lo_final = math.radians(tilt_range_final[0])
        self.tilt_hi_final = math.radians(tilt_range_final[1])
        self.curriculum_hold = curriculum_hold
        self.curriculum_step = math.radians(curriculum_step)
        self.stand_height_frac = stand_height_frac
        self.hold_seconds = hold_seconds
        self.upright_hold = None
        self.ever_stood = None
        self.prev_height = None

        kw.pop("target_speed", None)   # the trainer passes it for every task; this one has no speed to track
        # A far bigger contact budget than the running tasks get. Those spend almost every step with two
        # feet on the deck; here every episode *starts* with a whole humanoid lying on it — torso, both
        # arms, both legs and the head all in contact at once. At the running tasks' nconmax the solver
        # silently drops contacts and the body sinks through the floor it is supposed to push off.
        kw.setdefault("nconmax", nconmax)
        kw.setdefault("njmax", njmax)
        super().__init__(xml_path, num_envs, episode_len_s=episode_len_s, **kw)

        self.hold_steps = max(1, int(round(hold_seconds / self.dt)))
        self.prev_height = self.xpos[:, self.pelvis, 2].clone()

        self._stagger()
        self._acc.update({k: torch.zeros((), device=self.device) for k in
                          ("stand_sum", "hold_sum", "height_sum", "stood_sum", "stand_time_sum")})
        self._last_episode = {}

    def _stagger(self) -> None:
        """
        Spreads the episode clock across the population.

        Without this every environment resets on the same step forever, because they all start together and
        the only thing that ends an episode here is a fixed six-second timer. That is bad twice over: every
        PPO minibatch sees the same slice of the task rather than a spread of it, and any metric averaged
        over a training iteration aliases against the episode period — a stand_frac sampled every ten
        iterations against a 12.5-iteration episode reports a number that is mostly about which part of the
        episode the population happens to be in, which is exactly how a run came to look like it was
        standing 22% of the time when it was not standing at all.
        """
        self.step_count = torch.randint(0, self.max_steps, (self.N,), device=self.device,
                                        generator=self.rng).float()

    def reset(self) -> torch.Tensor:
        obs = super().reset()
        self._stagger()   # super().reset() puts every clock back to zero; spread them out again
        return obs

    # ---- starting poses ------------------------------------------------------------------------

    def _reset_states(self):
        """
        A body on the ground, in a pose it did not choose.

        The rotation is built as a yaw times a tilt about a random horizontal axis, rather than as a
        uniformly random quaternion, because a uniform rotation spends most of its mass on poses that are
        merely odd; what is wanted is the specific family of poses a runner actually ends an attempt in —
        face down, on its back, on either side, and everything between those and still-recoverable.
        """
        N, dev = self.N, self.device
        q = self.default_qpos.unsqueeze(0).repeat(N, 1)

        yaw = (torch.rand(N, generator=self.rng, device=dev) * 2 - 1) * math.pi
        tilt = self.tilt_lo + torch.rand(N, generator=self.rng, device=dev) * (self.tilt_hi - self.tilt_lo)
        axis_ang = (torch.rand(N, generator=self.rng, device=dev) * 2 - 1) * math.pi

        half = tilt * 0.5
        s, c = torch.sin(half), torch.cos(half)
        q_tilt = torch.stack([c, torch.cos(axis_ang) * s, torch.sin(axis_ang) * s, torch.zeros_like(s)], dim=-1)
        q[:, 3:7] = quat_mul(yaw_quat(yaw), q_tilt)

        # Height for the pose: full standing height when it is still nearly upright, down to the thickness
        # of a body lying on the deck when it is flat. A few centimetres of clearance on top, so the reset
        # never starts inside the floor and PhysX settles it in the first step or two instead.
        # 0.14 m is where this body's pelvis actually rests when it is lying flat (measured, not guessed:
        # 200 settling steps put it at 0.099 m). The earlier 0.24 dropped every flat reset twenty
        # centimetres onto the deck and spent the first ten steps of every episode bouncing.
        lying = 0.14
        q[:, 2] = lying + (self.stand_height - lying) * torch.cos(tilt).clamp_min(0.0) + 0.04

        joint = self.default_joint.unsqueeze(0) + (torch.rand(N, self.A, generator=self.rng, device=dev) * 2 - 1) * 0.35
        q[:, 7:] = joint.clamp(self.joint_lo, self.joint_hi)

        v = torch.zeros(N, self.qvel.shape[1], device=dev)
        # Some starts are mid-tumble rather than already settled: a policy that has only ever woken up
        # motionless has not seen the moment it most needs to react to.
        v[:, :3] = (torch.rand(N, 3, generator=self.rng, device=dev) * 2 - 1) * 0.4
        v[:, 3:6] = (torch.rand(N, 3, generator=self.rng, device=dev) * 2 - 1) * 0.8
        v[:, 6:] = (torch.rand(N, self.A, generator=self.rng, device=dev) * 2 - 1) * 0.5
        return q, v

    def _apply_reset(self, mask: torch.Tensor) -> None:
        super()._apply_reset(mask)
        if self.upright_hold is None:
            self.upright_hold = torch.zeros(self.N, device=self.device)
            self.ever_stood = torch.zeros(self.N, device=self.device)
            return
        zero = torch.zeros_like(self.upright_hold)
        self.upright_hold = torch.where(mask, zero, self.upright_hold)
        self.ever_stood = torch.where(mask, zero, self.ever_stood)
        # Height carries into the rise term, so a reset must not read as a metre of instant progress.
        self.prev_height = torch.where(mask, self.xpos[:, self.pelvis, 2], self.prev_height)

    # ---- observation ---------------------------------------------------------------------------

    def compute_obs(self) -> torch.Tensor:
        """
        The run-to-target observation with a zero command.

        Keeping the layout identical is the whole reason this policy costs Unity nothing: `PolicyRunner`
        builds the same 12 + 3N vector it already builds, and switching an athlete from running to getting
        up is a change of ONNX file and three zeros, not a change of code.
        """
        q, pos, lin_w, lin_b, ang_b, grav_b = self._base_state()
        cmd = torch.zeros(self.N, 3, device=self.device)
        obs = torch.cat([lin_b, ang_b, grav_b, cmd,
                         self.qpos[:, 7:] - self.default_joint,
                         self.qvel[:, 6:],
                         self.last_action], dim=-1)
        return torch.nan_to_num(obs).clamp(-100.0, 100.0)

    # ---- step ----------------------------------------------------------------------------------

    def step(self, action: torch.Tensor):
        action = action.clamp(-5.0, 5.0)
        self.prev_action = self.last_action
        self.last_action = action
        target = (self.default_joint + action * self.action_scale).clamp(self.ctrl_lo, self.ctrl_hi)
        self.ctrl.copy_(target)
        for _ in range(self.decimation):
            mjw.step(self.mw, self.dw)
        self.step_count = self.step_count + 1.0

        q, pos, lin_w, lin_b, ang_b, grav_b = self._base_state()
        upright = -grav_b[:, 2]                                  # 1 standing, 0 on its side, -1 upside down
        h = pos[:, 2]
        h_frac = (h / max(1e-3, self.stand_height)).clamp(0.0, 1.5)

        standing = (upright > self.stand_upright) & (h_frac > self.stand_height_frac)
        stand_f = standing.float()
        self.upright_hold = torch.where(standing, self.upright_hold + 1.0, torch.zeros_like(self.upright_hold))
        held = self.upright_hold >= self.hold_steps

        # ---- reward ----
        #
        # The shape of this matters more than the weights. A body lying on the deck has a pelvis at about
        # 0.10 m against a standing height of 0.91, so anything linear in height is nearly flat across the
        # whole range the policy has to climb: the difference between "flat" and "up on one elbow" has to be
        # worth something, or there is no gradient to follow out of the starting pose.
        #
        # So height is squared (every centimetre gained pays more than the last), and it is paired with a
        # term on the *rate* of rise, which is the one signal that is immediate — it pays on the step the
        # body pushes, rather than several seconds later when it is finally upright.
        # Clamped tight and weighted modestly on purpose. Height under contact is a noisy signal — a body
        # scraping along the deck jitters by millimetres every step — and an aggressive rate term turns that
        # jitter into advantage variance, which shows up as a KL spike, which the adaptive learning rate
        # answers by slamming itself to the floor. The first tuning had this at 25x with a 5 cm clamp and
        # spent its updates oscillating between a 1e-5 and a 9e-4 learning rate instead of learning.
        rise = (h - self.prev_height).clamp(-0.03, 0.03)
        self.prev_height = h.detach().clone()

        r_upright = 2.0 * (((upright + 1.0) * 0.5) ** 2)
        r_height = 4.0 * (h_frac.clamp_max(1.0) ** 2)
        r_rise = 8.0 * rise
        r_stand = 2.0 * stand_f
        # The hold is the actual objective: standing up and falling straight back down solves nothing, so
        # the largest single term only arrives once the body has been up for a second.
        r_hold = 5.0 * held.float()

        # Everything below is a damping term and every one of them is gated on already being up. Penalising
        # motion while the body is still on the ground is telling it not to do the one thing being asked of
        # it — the first reward probe had an ungated angular-velocity penalty as the single largest term in
        # the whole function, quietly charging the policy for rolling over.
        joint_vel = self.qvel[:, 6:]
        r_still = -0.02 * stand_f * (joint_vel ** 2).sum(-1).clamp_max(200.0)
        r_drift = -0.3 * stand_f * (lin_b[:, :2] ** 2).sum(-1)
        r_spin = -0.05 * stand_f * (ang_b ** 2).sum(-1).clamp_max(50.0)

        r_act = -0.002 * (action ** 2).sum(-1)
        r_rate = -0.02 * ((action - self.prev_action) ** 2).sum(-1)
        jp = self.qpos[:, 7:]
        r_limit = -0.2 * ((self.joint_lo + 0.05 - jp).clamp_min(0.0) + (jp - self.joint_hi + 0.05).clamp_min(0.0)).sum(-1)

        reward = (r_upright + r_height + r_rise + r_stand + r_hold + r_still + r_drift + r_spin
                  + r_act + r_rate + r_limit)

        # ---- termination ----
        # The clock, and nothing else. There is no "fell" here: being down is the premise. The only other
        # way out is a body that has gone non-finite, which is a solver blow-up rather than an outcome.
        broken = ~torch.isfinite(pos).all(-1)
        timeout = self.step_count >= self.max_steps
        done = timeout | broken

        self.ever_stood = torch.maximum(self.ever_stood, stand_f)
        self.episode_return = self.episode_return + reward
        self.episode_len = self.episode_len + 1.0

        df = done.float()
        self._acc["ret_sum"] += (self.episode_return * df).sum()
        self._acc["len_sum"] += (self.episode_len * df).sum()
        self._acc["done_n"] += df.sum()
        self._acc["stood_sum"] += (self.ever_stood * df).sum()
        self._acc["stand_sum"] += stand_f.mean()
        self._acc["hold_sum"] += held.float().mean()
        self._acc["height_sum"] += h_frac.mean()
        self._acc["upright_sum"] += upright.mean()
        self._acc["stand_time_sum"] += (self.upright_hold * df).sum() * self.dt
        self._acc["steps"] += 1.0

        self._apply_reset(done)
        mjw.forward(self.mw, self.dw)
        self._obs = self.compute_obs()
        return self._obs, reward, done, timeout

    def advance_curriculum(self, hold_frac: float) -> None:
        """
        Widens the range of starting poses once the policy can hold a stand from the current one.

        Driven by hold_frac rather than stood_frac on purpose: touching upright for one frame on the way
        past is not evidence of anything, and it is exactly what a policy farming the shaping does anyway.
        Holding a stand for a second is the thing that has to be true before harder poses are worth setting.

        It only ever widens. A curriculum that narrows again when a harder pose knocks the metric down would
        oscillate, and the policy would spend the run relearning what it already knew.
        """
        if hold_frac < self.curriculum_hold:
            return
        self.tilt_hi = min(self.tilt_hi_final, self.tilt_hi + self.curriculum_step)
        self.tilt_lo = min(self.tilt_lo_final, self.tilt_lo + self.curriculum_step * 0.5)

    def get_stats(self):
        a = {k: v.item() for k, v in self._acc.items()}
        n = max(1.0, a["done_n"])
        s = max(1.0, a["steps"])
        # Every env in this task times out together on a fixed six-second clock, so an episode finishes only
        # once in every twelve or so training iterations. Reporting zero on the other eleven turns the
        # TensorBoard trace into a comb and makes the run unreadable; the last real figure is held instead.
        episodes_done = a["done_n"] > 0.5
        out = {
            "ep_return": a["ret_sum"] / n,
            "ep_len_s": a["len_sum"] / n * self.dt,
            # The headline number: what fraction of attempts got the body onto its feet at all.
            "stood_frac": a["stood_sum"] / n,
            # And what fraction of the whole run was spent standing, which is the one that keeps rising
            # after stood_frac saturates — it is the difference between standing up and staying up.
            "stand_frac": a["stand_sum"] / s,
            "hold_frac": a["hold_sum"] / s,
            "upright": a["upright_sum"] / s,
            "height_frac": a["height_sum"] / s,
            "stand_time_s": a["stand_time_sum"] / n,
            # Kept so the trainer's CSV columns keep meaning something: a "fall" here is an attempt that
            # never got up, and there is no target to run toward.
            "fall_rate": 1.0 - a["stood_sum"] / n,
            "reach_frac": a["stand_sum"] / s,
            "v_toward": 0.0,
            # Where the curriculum has got to, in degrees. Flat on the deck is 180.
            "tilt_lo_deg": math.degrees(self.tilt_lo),
            "tilt_hi_deg": math.degrees(self.tilt_hi),
        }
        self.advance_curriculum(out["hold_frac"])
        if not episodes_done:
            for k in ("ep_return", "ep_len_s", "stood_frac", "stand_time_s", "fall_rate"):
                out[k] = self._last_episode.get(k, 0.0)
        else:
            for k in ("ep_return", "ep_len_s", "stood_frac", "stand_time_s", "fall_rate"):
                self._last_episode[k] = out[k]
        for v in self._acc.values():
            v.zero_()
        return out
