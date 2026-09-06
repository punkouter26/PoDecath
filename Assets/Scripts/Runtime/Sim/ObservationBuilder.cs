using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Fills a pre-allocated observation buffer in the Isaac Lab velocity-task layout:
    ///   [base_lin_vel(3), base_ang_vel(3), projected_gravity(3), velocity_command(3),
    ///    joint_pos - default(N), joint_vel(N), last_action(N), height_scan(H)]
    /// All vectors are expressed in the external right-handed Z-up body frame.
    /// Angular velocity is a pseudo-vector, so the reflection between frames negates it
    /// after the axis swap: w_ext = -(w_u.x, w_u.z, w_u.y).
    /// No allocations after construction.
    /// </summary>
    public sealed class ObservationBuilder
    {
        readonly PolicyConfig _cfg;
        readonly Vector2[] _scanOffsetsExt;   // (x, y) in the external base-yaw frame
        readonly int _layerMask;

        public int Size => _cfg.ObservationSize;

        public ObservationBuilder(PolicyConfig cfg, int creatureLayer)
        {
            _cfg = cfg;
            _layerMask = creatureLayer >= 0 ? ~(1 << creatureLayer) : ~0;

            int nx = cfg.HeightScanCountX;
            int ny = cfg.HeightScanCountY;
            _scanOffsetsExt = new Vector2[nx * ny];
            if (nx > 0 && ny > 0)
            {
                // Isaac Lab grid_pattern: meshgrid(x, y, indexing="xy") flattened -> x varies fastest.
                int k = 0;
                for (int j = 0; j < ny; j++)
                {
                    float y = -cfg.heightScanSizeY * 0.5f + j * cfg.heightScanResolution;
                    for (int i = 0; i < nx; i++)
                    {
                        float x = -cfg.heightScanSizeX * 0.5f + i * cfg.heightScanResolution;
                        _scanOffsetsExt[k++] = new Vector2(x, y);
                    }
                }
            }
        }

        public void Fill(float[] obs, AthleteRig rig, Vector3 commandExt, float[] lastAction, float[] jointPosExt, float[] jointVelExt)
        {
            int o = 0;
            float clip = _cfg.observationClip;

            if (_cfg.includeBaseLinearVelocity)
            {
                Vector3 v = rig.BaseLinearVelocityBody * _cfg.linearVelocityScale;
                CoordinateTransform.WriteExternal(obs, o, v);
                o += 3;
            }
            if (_cfg.includeBaseAngularVelocity)
            {
                Vector3 w = rig.BaseAngularVelocityBody * _cfg.angularVelocityScale;
                obs[o + 0] = -w.x;
                obs[o + 1] = -w.z;
                obs[o + 2] = -w.y;
                o += 3;
            }
            if (_cfg.includeProjectedGravity)
            {
                CoordinateTransform.WriteExternal(obs, o, rig.ProjectedGravityBody);
                o += 3;
            }
            if (_cfg.includeVelocityCommand)
            {
                obs[o + 0] = commandExt.x;
                obs[o + 1] = commandExt.y;
                obs[o + 2] = commandExt.z;
                o += 3;
            }

            int n = _cfg.JointCount;
            if (_cfg.includeJointPositions)
            {
                for (int i = 0; i < n; i++)
                    obs[o + i] = (jointPosExt[i] - rig.DefaultPosition(i)) * _cfg.jointPositionScale;
                o += n;
            }
            if (_cfg.includeJointVelocities)
            {
                for (int i = 0; i < n; i++)
                    obs[o + i] = jointVelExt[i] * _cfg.jointVelocityScale;
                o += n;
            }
            if (_cfg.includeLastActions)
            {
                for (int i = 0; i < n; i++) obs[o + i] = lastAction[i];
                o += n;
            }

            int h = _scanOffsetsExt.Length;
            if (h > 0)
            {
                Vector3 basePos = rig.BasePosition;
                Vector3 fwd = rig.BaseForward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.right;
                fwd.Normalize();
                // external +Y (left) in Unity is +Z; with yaw applied: left = up x fwd? Use the mapped axis.
                Vector3 left = Vector3.Cross(Vector3.up, fwd) * -1f; // Unity is left-handed: this yields the +Z-side for fwd=+X
                float maxDist = _cfg.heightScanMaxDistance;
                for (int i = 0; i < h; i++)
                {
                    Vector2 off = _scanOffsetsExt[i];
                    Vector3 origin = basePos + fwd * off.x + left * off.y + Vector3.up * 0.5f;
                    float hit = basePos.y - maxDist;
                    if (Physics.Raycast(origin, Vector3.down, out RaycastHit rh, maxDist + 0.5f, _layerMask, QueryTriggerInteraction.Ignore))
                        hit = rh.point.y;
                    obs[o + i] = Mathf.Clamp(basePos.y - hit - _cfg.heightScanOffset, -1f, 1f);
                }
                o += h;
            }

            for (int i = 0; i < o; i++)
            {
                float v = obs[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
                obs[i] = Mathf.Clamp(v, -clip, clip);
            }
        }
    }
}
