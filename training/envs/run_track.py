"""Run laps around the rooftop stadium loop (same geometry as Unity's KartTrackBuilder).

Loop in the external frame (x along the straights, y lateral): straights at y = +-R from x = -halfLen to
+halfLen joined by semicircles. Travel is +x on the +y straight (the dash straight), then around the east end.
Arc length s in [0, lap). The policy keeps the run-to-target observation contract: the "target" is a carrot
placed `lookahead` metres ahead along the centre line, so the exported ONNX drops into Unity's TrackFollower.
Extra reward: progress along the track, lateral penalty; termination when leaving the deck.
"""
from __future__ import annotations

import math

import mujoco_warp as mjw
import torch

from .run_to_target import RunToTargetEnv, quat_rotate_inverse, quat_yaw, yaw_quat


class RunTrackEnv(RunToTargetEnv):
    def __init__(self, xml_path: str, num_envs: int, half_len: float = 11.2, radius: float = 8.8,
                 deck_width: float = 5.3, lookahead: float = 6.0, target_speed: float = 4.0, **kw):
        self.half_len = half_len
        self.R = radius
        self.deck_half = deck_width * 0.5
        self.lookahead = lookahead
        self.s_straight = 2.0 * half_len
        self.s_arc = math.pi * radius
        self.lap = 2.0 * self.s_straight + 2.0 * self.s_arc
        self._s = None
        super().__init__(xml_path, num_envs, target_speed=target_speed, **kw)
        # The carrot sits a fixed `lookahead` metres ahead, so the base task's "you arrived" test
        # (distance under 0.6 m) can never be true here and its 5.0 bonus -- nominally the largest
        # single term in the reward -- had never paid out once in this task's history. Dead weight,
        # and confusing to anyone reading the reward to work out what the policy is optimising.
        self.reach_bonus = 0.0

    # ---- observation ----------------------------------------------------------------------------
    def compute_obs(self) -> torch.Tensor:
        """Same layout as the base task, with the third command channel put to work.

        The base command is (direction x, direction y, distance / 10). Distance to a carrot pinned
        `lookahead` metres ahead is `lookahead` for the whole run, so that channel was a hard constant
        -- 0.6 on the shipped config -- carrying no information into the policy at all. It now carries
        signed lateral offset from the centre line, normalised by the half-deck, which is the one
        quantity the athlete is being penalised and terminated on and previously could not observe.
        Unity's TrackFollower already computes it for the HUD.
        """
        q, pos, lin_w, lin_b, ang_b, grav_b = self._base_state()
        yaw = quat_yaw(q)
        to_t = self.targets - pos[:, :2]
        dist = torch.norm(to_t, dim=-1, keepdim=True)
        c, s = torch.cos(yaw), torch.sin(yaw)
        dx = c * to_t[:, 0] + s * to_t[:, 1]
        dy = -s * to_t[:, 0] + c * to_t[:, 1]
        dirn = torch.stack([dx, dy], -1) / dist.clamp_min(1e-3)
        if self._s is None:
            lat = torch.zeros(self.N, 1, device=self.device)
        else:
            pos_c, tan = self.centerline(self._s)
            normal = torch.stack([-tan[:, 1], tan[:, 0]], -1)
            lat = ((pos[:, :2] - pos_c) * normal).sum(-1, keepdim=True) / self.deck_half
        cmd = torch.cat([dirn, lat.clamp(-2.0, 2.0)], -1)
        return self._assemble_obs(lin_b, ang_b, grav_b, cmd)

    # ---- centre line ---------------------------------------------------------------------------
    def centerline(self, s: torch.Tensor):
        """s: (N,) -> position (N,2), tangent (N,2).

        Branchless on purpose. This used to select each of the four track segments with a boolean mask
        -- `s[m1]` and friends -- which is `masked_select`: the output size is not known until the mask
        has been counted on the device, so every one of those lines stalled the pipeline waiting for the
        GPU. `centerline` runs three times per control step and once more over a 25x-expanded tensor in
        `project`, so the step path this file's own docstring calls sync-free was stopping about a dozen
        times per step. Every branch is now evaluated and `torch.where` picks the answer: strictly more
        arithmetic, and far less waiting.
        """
        s = torch.remainder(s, self.lap)
        L, R, A = self.s_straight, self.R, self.s_arc
        hl = self.half_len
        m0 = s < L
        m1 = (s >= L) & (s < L + A)
        m2 = (s >= L + A) & (s < 2 * L + A)

        # east arc, clockwise from top: theta from pi/2 down to -pi/2
        th1 = math.pi / 2 - (s - L) / R
        # west arc: theta from -pi/2 down to -3pi/2
        th3 = -math.pi / 2 - (s - 2 * L - A) / R

        pos_x = torch.where(m0, -hl + s,
                 torch.where(m1, hl + R * torch.cos(th1),
                 torch.where(m2, hl - (s - L - A), -hl + R * torch.cos(th3))))
        pos_y = torch.where(m0, torch.full_like(s, R),
                 torch.where(m1, R * torch.sin(th1),
                 torch.where(m2, torch.full_like(s, -R), R * torch.sin(th3))))
        tan_x = torch.where(m0, torch.ones_like(s),
                 torch.where(m1, torch.sin(th1),
                 torch.where(m2, -torch.ones_like(s), torch.sin(th3))))
        tan_y = torch.where(m0, torch.zeros_like(s),
                 torch.where(m1, -torch.cos(th1),
                 torch.where(m2, torch.zeros_like(s), -torch.cos(th3))))
        return torch.stack([pos_x, pos_y], -1), torch.stack([tan_x, tan_y], -1)

    def project(self, xy: torch.Tensor, s_prev: torch.Tensor) -> torch.Tensor:
        """Nearest arc length near the previous estimate (window -2..+4 m, 25 samples)."""
        offs = torch.linspace(-2.0, 4.0, 25, device=xy.device)
        cand = s_prev[:, None] + offs[None, :]                              # (N,25)
        p, _ = self.centerline(cand.reshape(-1))
        d = ((p.reshape(-1, 25, 2) - xy[:, None, :]) ** 2).sum(-1)
        best = d.argmin(-1)
        return torch.remainder(cand.gather(1, best[:, None]).squeeze(1), self.lap)

    # ---- overrides ------------------------------------------------------------------------------
    def _reset_states(self):
        q, v = super()._reset_states()
        N = self.N
        s0 = torch.rand(N, generator=self.rng, device=self.device) * self.lap
        pos, tan = self.centerline(s0)
        lateral = (torch.rand(N, generator=self.rng, device=self.device) * 2 - 1) * 1.0
        normal = torch.stack([-tan[:, 1], tan[:, 0]], -1)
        q[:, 0:2] = pos + normal * lateral[:, None]
        heading = torch.atan2(tan[:, 1], tan[:, 0]) + (torch.rand(N, generator=self.rng, device=self.device) * 2 - 1) * 0.3
        q[:, 3:7] = yaw_quat(heading)
        self._s_reset = s0
        return q, v

    def _apply_reset(self, mask: torch.Tensor) -> None:
        super()._apply_reset(mask)
        if self._s is None:
            self._s = torch.zeros(self.N, device=self.device)
            self._lap_progress = torch.zeros(self.N, device=self.device)
        self._s = torch.where(mask, self._s_reset, self._s)
        self._lap_progress = torch.where(mask, torch.zeros_like(self._lap_progress), self._lap_progress)
        self._update_carrot()

    def _update_carrot(self) -> None:
        carrot, _ = self.centerline(self._s + self.lookahead)
        self.targets = carrot

    def step(self, action: torch.Tensor):
        s_before = self._s.clone()
        obs, reward, done, timeout = super().step(action)      # uses self.targets = carrot; reward includes tracking toward carrot
        # progress along the track (after super() has applied resets for done envs, so recompute for all)
        xy = self.xpos[:, self.pelvis, :2]
        self._s = self.project(xy, self._s)
        ds = torch.remainder(self._s - s_before + self.lap * 0.5, self.lap) - self.lap * 0.5
        ds = torch.where(done, torch.zeros_like(ds), ds)
        self._lap_progress = self._lap_progress + ds
        pos_c, tan = self.centerline(self._s)
        lateral = torch.abs(((xy - pos_c) * torch.stack([-tan[:, 1], tan[:, 0]], -1)).sum(-1))
        off_deck = lateral > self.deck_half + 0.3
        r_lat = -0.5 * (lateral - (self.deck_half - 0.8)).clamp_min(0.0)
        r_prog = 0.5 * (ds / self.dt).clamp(-1.0, self.target_speed)
        reward = reward + r_lat + r_prog - 2.0 * off_deck.float()
        newly_done = off_deck & ~done

        # Book the deck exits, before _apply_reset clears the episode counters they are read from.
        #
        # RunToTargetEnv.step has already written its stats by the time control reaches here, and the
        # only ending it knows about is going down: height under 60% of standing, or uprightness under
        # 0.4. Running off the side of a 5.3 m deck is neither. So `fall_rate` was blind to the one
        # failure mode that gets *worse* the faster an athlete runs -- and the adaptive speed curriculum
        # gated on exactly that number. It raised the target happily while the field streamed off the
        # roof: 5% "falls" reported against a measured 0% of athletes completing a clean lap.
        ndf = newly_done.float()
        self._acc.setdefault("off_deck_sum", torch.zeros((), device=self.device))
        self._acc["off_deck_sum"] += ndf.sum()
        self._acc["fell_sum"] += ndf.sum()
        self._acc["done_n"] += ndf.sum()          # they ended an episode; the denominator must know
        self._acc["ret_sum"] += (self.episode_return * ndf).sum()
        self._acc["len_sum"] += (self.episode_len * ndf).sum()

        # Unconditional. `newly_done.any()` read a device boolean back to Python every single step just
        # to skip work that is already a no-op when the mask is empty -- the masked reset writes nothing
        # and `forward` costs far less than the stall did. The duplicate `compute_obs()` below went with
        # it: this used to build the observation three times per step and only keep the last.
        self._apply_reset(newly_done)
        done = done | off_deck
        self._update_carrot()
        self._physics_forward()
        self._obs = self.compute_obs()
        obs = self._obs
        self._acc.setdefault("lap_prog", torch.zeros((), device=self.device))
        self._acc["lap_prog"] += self._lap_progress.mean()
        return self._obs, reward, done, timeout

    def get_stats(self):
        lp = self._acc.get("lap_prog")
        steps = max(1.0, float(self._acc["steps"].item()))
        # Read every accumulator *before* super().get_stats(), which zeroes all of them. Reading
        # afterwards is why lap_progress_m has always reported 0.
        lp_v = float(lp.item()) if lp is not None else None
        od = self._acc.get("off_deck_sum")
        od_v = float(od.item()) if od is not None else None
        n = max(1.0, float(self._acc["done_n"].item()))

        out = super().get_stats()
        if lp_v is not None:
            out["lap_progress_m"] = lp_v / steps
        if od_v is not None:
            out["off_deck_rate"] = od_v / n
        return out
