using UnityEngine;

namespace PoDecath.Sim
{
    public enum CommandMode
    {
        /// <summary>Steer toward a world target: forward speed plus proportional yaw rate.</summary>
        TowardTarget,
        /// <summary>Fixed (vx, vy, yaw_rate) command.</summary>
        Constant,
        /// <summary>Uniformly resampled command every N seconds (Isaac Lab style).</summary>
        RandomResample,
        /// <summary>Run-to-target policies: (unit dir x, unit dir y) in the base-yaw frame plus min(dist, 10) / 10.</summary>
        TargetVector,
    }

    /// <summary>
    /// Produces the 3-float velocity command (vx, vy, yaw_rate) in the external
    /// right-handed base frame: +vx forward, +vy left, +yaw counter-clockwise seen from above.
    /// </summary>
    public class VelocityCommandSource : MonoBehaviour
    {
        public CommandMode mode = CommandMode.TowardTarget;

        [Header("Constant")]
        public Vector3 constantCommand = new Vector3(0.5f, 0f, 0f);

        [Header("Toward target")]
        public float targetSpeed = 0.6f;
        public float yawGain = 1.5f;
        public float maxYawRate = 1.0f;
        public float slowRadius = 0.8f;
        public float stopRadius = 0.25f;

        [Header("Random resample")]
        public Vector2 linXRange = new Vector2(-1f, 1f);
        public Vector2 linYRange = new Vector2(-1f, 1f);
        public Vector2 yawRange = new Vector2(-1f, 1f);
        public float resampleSeconds = 5f;

        public Vector3 Command { get; private set; }
        public Vector3 TargetPosition { get; private set; }
        public bool HasTarget { get; private set; }
        public float HeadingErrorRad { get; private set; }

        float _timer;
        System.Random _rng = new System.Random(1234);

        public void SetTarget(Vector3 unityWorldPosition)
        {
            TargetPosition = unityWorldPosition;
            HasTarget = true;
        }

        public void ClearTarget() => HasTarget = false;

        public void ResetCommand()
        {
            _timer = 0f;
            if (mode == CommandMode.RandomResample) Resample();
            else if (mode == CommandMode.Constant) Command = constantCommand;
            else Command = Vector3.zero;
        }

        /// <summary>Called once per policy step by PolicyRunner.</summary>
        public void Tick(AthleteRig rig)
        {
            switch (mode)
            {
                case CommandMode.Constant:
                    Command = constantCommand;
                    break;
                case CommandMode.RandomResample:
                    _timer += Time.fixedDeltaTime * Mathf.Max(1, rig.Config != null ? rig.Config.controlDecimation : 1);
                    if (_timer >= resampleSeconds) { _timer = 0f; Resample(); }
                    break;
                case CommandMode.TargetVector:
                    Command = HasTarget ? TargetVectorCommand(rig) : new Vector3(1f, 0f, 0f);
                    break;
                default:
                    Command = HasTarget ? Steer(rig) : Vector3.zero;
                    break;
            }
        }

        /// <summary>Matches training/envs/run_to_target.py: direction in the base-yaw frame and saturated distance.</summary>
        Vector3 TargetVectorCommand(AthleteRig rig)
        {
            Vector3 fwdExt = CoordinateTransform.UnityToExternal(rig.BaseForward);
            Vector3 toExt = CoordinateTransform.UnityToExternal(TargetPosition - rig.BasePosition);
            float yaw = Mathf.Atan2(fwdExt.y, fwdExt.x);
            float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            float dx = c * toExt.x + s * toExt.y;
            float dy = -s * toExt.x + c * toExt.y;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            HeadingErrorRad = Mathf.Atan2(dy, dx);
            if (dist < 1e-3f) return new Vector3(1f, 0f, 0f);
            return new Vector3(dx / dist, dy / dist, Mathf.Min(dist, 10f) / 10f);
        }

        void Resample()
        {
            float R(Vector2 r) => (float)(r.x + _rng.NextDouble() * (r.y - r.x));
            Command = new Vector3(R(linXRange), R(linYRange), R(yawRange));
        }

        Vector3 Steer(AthleteRig rig)
        {
            // Work entirely in the external frame so yaw sign is the right-handed convention.
            Vector3 fwdExt = CoordinateTransform.UnityToExternal(rig.BaseForward);
            Vector3 toTargetExt = CoordinateTransform.UnityToExternal(TargetPosition - rig.BasePosition);
            toTargetExt.z = 0f;
            float dist = toTargetExt.magnitude;
            if (dist < stopRadius) { HeadingErrorRad = 0f; return Vector3.zero; }

            float heading = Mathf.Atan2(fwdExt.y, fwdExt.x);
            float desired = Mathf.Atan2(toTargetExt.y, toTargetExt.x);
            float err = Mathf.DeltaAngle(heading * Mathf.Rad2Deg, desired * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            HeadingErrorRad = err;

            float yaw = Mathf.Clamp(yawGain * err, -maxYawRate, maxYawRate);
            float align = Mathf.Clamp01(1f - Mathf.Abs(err) / (Mathf.PI * 0.5f));
            float speed = targetSpeed * align * Mathf.Clamp01(dist / slowRadius);
            return new Vector3(speed, 0f, yaw);
        }
    }
}
