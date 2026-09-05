"""Isaac Lab (direct workflow) twin of training/envs/run_to_target.py.

Same body (athlete.usd converted from athlete.xml), same PD gains, same observation contract
(75 floats: base_lin_vel, base_ang_vel, projected_gravity, target_command, joint_pos_rel, joint_vel,
last_action, all in the policy joint order by NAME) and the same reward terms, so the exported ONNX drops
into Unity's PolicyRunner unchanged and the two trainers can be compared on equal footing.
"""
from __future__ import annotations

import json
import math
import os

import torch

import isaaclab.sim as sim_utils
from isaaclab.actuators import ImplicitActuatorCfg
from isaaclab.assets import Articulation, ArticulationCfg
from isaaclab.envs import DirectRLEnv, DirectRLEnvCfg
from isaaclab.scene import InteractiveSceneCfg
from isaaclab.sim import SimulationCfg
from isaaclab.terrains import TerrainImporterCfg
from isaaclab.utils import configclass

HERE = os.path.dirname(os.path.abspath(__file__))
USD_PATH = os.path.abspath(os.path.join(HERE, "..", "usd", "athlete.usd"))
POLICY_JSON = os.path.abspath(os.path.join(HERE, "..", "..", "models", "athlete_policy_config.json"))

with open(POLICY_JSON, "r", encoding="utf-8") as f:
    PJ = json.load(f)
JOINT_ORDER: list[str] = PJ["joint_order"]
DEFAULTS = dict(zip(JOINT_ORDER, PJ["default_joint_pos"]))
KP = dict(zip(JOINT_ORDER, PJ["kp"]))
KV = dict(zip(JOINT_ORDER, PJ["kv"]))
FL = dict(zip(JOINT_ORDER, PJ["force_limit"]))


def _group(prefix: str) -> list[str]:
    return [j for j in JOINT_ORDER if j.startswith(prefix)]


def _actuators() -> dict:
    acts = {}
    for name, prefix in (("abdomen", "abdomen"), ("hip", "hip"), ("knee", "knee"), ("ankle", "ankle"),
                         ("shoulder", "shoulder"), ("elbow", "elbow")):
        names = _group(prefix)
        if not names:
            continue
        acts[name] = ImplicitActuatorCfg(
            joint_names_expr=names,
            stiffness={n: KP[n] for n in names},
            damping={n: KV[n] + 0.5 for n in names},   # + MJCF joint damping
            effort_limit_sim={n: FL[n] for n in names},
        )
    return acts


ATHLETE_CFG = ArticulationCfg(
    prim_path="/World/envs/env_.*/Robot",
    spawn=sim_utils.UsdFileCfg(
        usd_path=USD_PATH,
        activate_contact_sensors=False,
        rigid_props=sim_utils.RigidBodyPropertiesCfg(disable_gravity=False, max_depenetration_velocity=10.0),
        articulation_props=sim_utils.ArticulationRootPropertiesCfg(
            enabled_self_collisions=False, solver_position_iteration_count=8, solver_velocity_iteration_count=2),
    ),
    init_state=ArticulationCfg.InitialStateCfg(pos=(0.0, 0.0, 0.93), joint_pos=DEFAULTS, joint_vel={".*": 0.0}),
    actuators=_actuators(),
    soft_joint_pos_limit_factor=1.0,
)


from isaaclab.managers import EventTermCfg as EventTerm
from isaaclab.managers import SceneEntityCfg
import isaaclab.envs.mdp as mdp


@configclass
class EventCfg:
    """Domain randomisation (the same recipe Isaac Lab's stock locomotion tasks use)."""
    physics_material = EventTerm(
        func=mdp.randomize_rigid_body_material, mode="startup",
        params={"asset_cfg": SceneEntityCfg("robot", body_names=".*"), "static_friction_range": (0.6, 1.2),
                "dynamic_friction_range": (0.6, 1.2), "restitution_range": (0.0, 0.0), "num_buckets": 64})
    add_base_mass = EventTerm(
        func=mdp.randomize_rigid_body_mass, mode="startup",
        params={"asset_cfg": SceneEntityCfg("robot", body_names="pelvis"), "mass_distribution_params": (-2.0, 4.0), "operation": "add"})
    push_robot = EventTerm(
        func=mdp.push_by_setting_velocity, mode="interval", interval_range_s=(4.0, 8.0),
        params={"velocity_range": {"x": (-0.6, 0.6), "y": (-0.6, 0.6)}})


@configclass
class RunToTargetIsaacEnvCfg(DirectRLEnvCfg):
    events: EventCfg = EventCfg()
    obs_noise = True
    decimation = 4
    episode_length_s = 20.0
    action_scale = float(PJ.get("action_scale", 0.5))
    action_space = len(JOINT_ORDER)
    observation_space = 12 + 3 * len(JOINT_ORDER)
    state_space = 0
    target_speed = 3.5
    target_dist = (6.0, 16.0)

    sim: SimulationCfg = SimulationCfg(dt=1.0 / 200.0, render_interval=4,
                                       physics_material=sim_utils.RigidBodyMaterialCfg(static_friction=1.0, dynamic_friction=1.0))
    terrain = TerrainImporterCfg(prim_path="/World/ground", terrain_type="plane", collision_group=-1,
                                 physics_material=sim_utils.RigidBodyMaterialCfg(static_friction=1.0, dynamic_friction=1.0, restitution=0.0))
    scene: InteractiveSceneCfg = InteractiveSceneCfg(num_envs=4096, env_spacing=4.0, replicate_physics=True)
    robot: ArticulationCfg = ATHLETE_CFG


class RunToTargetIsaacEnv(DirectRLEnv):
    cfg: RunToTargetIsaacEnvCfg

    def __init__(self, cfg: RunToTargetIsaacEnvCfg, render_mode: str | None = None, **kwargs):
        super().__init__(cfg, render_mode, **kwargs)
        # policy joint order (by name) -> simulation joint indices
        idx, names = self.robot.find_joints(JOINT_ORDER, preserve_order=True)
        assert list(names) == JOINT_ORDER, f"joint order mismatch: {names}"
        self.jidx = torch.tensor(idx, device=self.device, dtype=torch.long)
        self.default_joint = torch.tensor([DEFAULTS[n] for n in JOINT_ORDER], device=self.device)
        lim = self.robot.data.joint_pos_limits[0, self.jidx]        # (A, 2)
        self.joint_lo, self.joint_hi = lim[:, 0], lim[:, 1]
        A = len(JOINT_ORDER)
        self.actions_buf = torch.zeros(self.num_envs, A, device=self.device)
        self.prev_actions = torch.zeros_like(self.actions_buf)
        self.targets = torch.zeros(self.num_envs, 2, device=self.device)
        self.stand_height = 0.93
        self.stats = {}

    # ---- scene ----
    def _setup_scene(self):
        self.robot = Articulation(self.cfg.robot)
        self.cfg.terrain.num_envs = self.scene.cfg.num_envs
        self.cfg.terrain.env_spacing = self.scene.cfg.env_spacing
        self._terrain = self.cfg.terrain.class_type(self.cfg.terrain)
        self.scene.clone_environments(copy_from_source=False)
        self.scene.articulations["robot"] = self.robot
        light = sim_utils.DomeLightCfg(intensity=2000.0, color=(0.75, 0.75, 0.75))
        light.func("/World/Light", light)

    # ---- helpers ----
    def _sample_targets(self, env_ids: torch.Tensor):
        n = env_ids.numel()
        pos = self.robot.data.root_pos_w[env_ids, :2]
        ang = torch.rand(n, device=self.device) * 2 * math.pi
        d = self.cfg.target_dist[0] + torch.rand(n, device=self.device) * (self.cfg.target_dist[1] - self.cfg.target_dist[0])
        self.targets[env_ids] = pos + torch.stack([torch.cos(ang), torch.sin(ang)], -1) * d[:, None]

    def _yaw(self, q: torch.Tensor) -> torch.Tensor:
        w, x, y, z = q[:, 0], q[:, 1], q[:, 2], q[:, 3]
        return torch.atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z))

    # ---- RL hooks ----
    def _pre_physics_step(self, actions: torch.Tensor):
        self.prev_actions = self.actions_buf.clone()
        self.actions_buf = actions.clamp(-5.0, 5.0)
        target = (self.default_joint + self.actions_buf * self.cfg.action_scale).clamp(self.joint_lo, self.joint_hi)
        self._joint_targets = target

    def _apply_action(self):
        self.robot.set_joint_position_target(self._joint_targets, joint_ids=self.jidx)

    def _get_observations(self) -> dict:
        d = self.robot.data
        q = d.root_quat_w
        pos = d.root_pos_w
        yaw = self._yaw(q)
        to_t = self.targets - pos[:, :2]
        dist = torch.norm(to_t, dim=-1, keepdim=True)
        c, s = torch.cos(yaw), torch.sin(yaw)
        dx = c * to_t[:, 0] + s * to_t[:, 1]
        dy = -s * to_t[:, 0] + c * to_t[:, 1]
        dirn = torch.stack([dx, dy], -1) / dist.clamp_min(1e-3)
        cmd = torch.cat([dirn, dist.clamp_max(10.0) / 10.0], -1)
        lin, ang, grav = d.root_lin_vel_b, d.root_ang_vel_b, d.projected_gravity_b
        jpos = d.joint_pos[:, self.jidx] - self.default_joint
        jvel = d.joint_vel[:, self.jidx]
        if getattr(self.cfg, "obs_noise", False):
            # Isaac Lab velocity-task noise levels: teaches robustness that sim-to-sim transfer needs
            lin = lin + torch.randn_like(lin) * 0.05
            ang = ang + torch.randn_like(ang) * 0.1
            grav = grav + torch.randn_like(grav) * 0.03
            jpos = jpos + torch.randn_like(jpos) * 0.01
            jvel = jvel + torch.randn_like(jvel) * 0.5
        obs = torch.cat([lin, ang, grav, cmd, jpos, jvel, self.actions_buf], dim=-1)
        obs = torch.nan_to_num(obs).clamp(-100.0, 100.0)
        return {"policy": obs}

    def _get_rewards(self) -> torch.Tensor:
        d = self.robot.data
        pos = d.root_pos_w
        to_t = self.targets - pos[:, :2]
        dist = torch.norm(to_t, dim=-1)
        dir_w = to_t / dist.clamp_min(1e-3)[:, None]
        v_toward = (d.root_lin_vel_w[:, :2] * dir_w).sum(-1)
        upright = -d.projected_gravity_b[:, 2]
        yaw = self._yaw(d.root_quat_w)
        heading = (torch.stack([torch.cos(yaw), torch.sin(yaw)], -1) * dir_w).sum(-1)
        a = self.actions_buf
        jp = d.joint_pos[:, self.jidx]
        r = (1.5 * torch.exp(-((v_toward - self.cfg.target_speed) ** 2) / 2.0)
             + 0.25 * v_toward.clamp(-1.0, self.cfg.target_speed)
             + 0.3 + 0.3 * upright.clamp_min(0.0) + 0.2 * heading
             - 0.5 * (self.stand_height - 0.05 - pos[:, 2]).clamp_min(0.0)
             - 0.5 * d.root_lin_vel_b[:, 2] ** 2
             - 0.03 * (d.root_ang_vel_b[:, :2] ** 2).sum(-1)
             - 0.002 * (a ** 2).sum(-1)
             - 0.02 * ((a - self.prev_actions) ** 2).sum(-1)
             - 1.0 * ((self.joint_lo + 0.05 - jp).clamp_min(0.0) + (jp - self.joint_hi + 0.05).clamp_min(0.0)).sum(-1))
        reached = dist < 0.6
        r = r + 5.0 * reached.float()
        r = r - 2.0 * self._fallen().float()
        if reached.any():
            self._sample_targets(torch.nonzero(reached).squeeze(-1))
        self.stats = {"v_toward": v_toward.mean(), "upright": upright.mean(), "reach_frac": reached.float().mean()}
        return r

    def _fallen(self) -> torch.Tensor:
        d = self.robot.data
        upright = -d.projected_gravity_b[:, 2]
        return (d.root_pos_w[:, 2] < self.stand_height * 0.6) | (upright < 0.4)

    def _get_dones(self):
        # DirectRLEnv evaluates dones before rewards, so the fall test lives here and is recomputed in rewards.
        time_out = self.episode_length_buf >= self.max_episode_length - 1
        return self._fallen(), time_out

    def _reset_idx(self, env_ids: torch.Tensor | None):
        if env_ids is None or len(env_ids) == self.num_envs:
            env_ids = self.robot._ALL_INDICES
        super()._reset_idx(env_ids)
        n = len(env_ids)
        root = self.robot.data.default_root_state[env_ids].clone()
        root[:, :3] += self.scene.env_origins[env_ids]
        root[:, 2] += 0.02
        yaw = (torch.rand(n, device=self.device) * 2 - 1) * math.pi
        root[:, 3] = torch.cos(yaw * 0.5); root[:, 4] = 0.0; root[:, 5] = 0.0; root[:, 6] = torch.sin(yaw * 0.5)
        root[:, 7:9] = (torch.rand(n, 2, device=self.device) * 2 - 1) * 0.2
        root[:, 9:] = 0.0
        jp = self.robot.data.default_joint_pos[env_ids].clone()
        jp += (torch.rand_like(jp) * 2 - 1) * 0.05
        jv = torch.zeros_like(jp)
        self.robot.write_root_pose_to_sim(root[:, :7], env_ids)
        self.robot.write_root_velocity_to_sim(root[:, 7:], env_ids)
        self.robot.write_joint_state_to_sim(jp, jv, None, env_ids)
        self.actions_buf[env_ids] = 0.0
        self.prev_actions[env_ids] = 0.0
        self._sample_targets(env_ids)
