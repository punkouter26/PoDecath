"""PLACEHOLDER: balance / get-up task (Phase 2).

Intended design (reuse RunToTargetEnv machinery, same observation contract so Unity needs no changes):
  * Reset from randomised fallen poses: sample the stand keyframe, apply a random root orientation
    (lying on front/back/side), drop with gravity for ~0.5 s of simulation, then start the episode.
  * Reward: pelvis height toward stand height, uprightness (-projected_gravity_z), low joint velocity
    once upright, action-rate and torque penalties; bonus when upright for 1 s.
  * Termination: time-out only (no fall termination), so the policy learns to recover from anything.
  * Export as Assets/Policies/athlete_getup.onnx; DashEvent switches to it when a fall is detected and
    back to athlete_run.onnx once upright (policy blending is a follow-up).

Nothing here runs yet; train_run.py stays the entry point for the run-to-target task.
"""
from __future__ import annotations


class GetUpEnv:  # pragma: no cover - placeholder
    def __init__(self, *args, **kwargs):
        raise NotImplementedError("Get-up task is a Phase 2 placeholder. See docstring for the intended design.")
