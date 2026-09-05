using System.Collections.Generic;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Thin adapter over an ArticulationBody hierarchy. Exposes joint state in the
    /// external (Isaac Lab / MuJoCo) sign convention and applies PD position targets.
    /// The creature's forward axis is Unity +X (external +X), up is Unity +Y.
    /// </summary>
    public class CreatureRig : MonoBehaviour
    {
        public ArticulationBody root;
        [Tooltip("Filled by Bind(): joints in policy order.")]
        public ArticulationBody[] joints = new ArticulationBody[0];
        public FootContactSensor[] feet = new FootContactSensor[0];

        PolicyConfig _cfg;
        float[] _sign = new float[0];
        float[] _lowerExt = new float[0];
        float[] _upperExt = new float[0];
        float[] _defaultExt = new float[0];
        readonly Dictionary<string, ArticulationBody> _lookup = new Dictionary<string, ArticulationBody>();

        public PolicyConfig Config => _cfg;
        public bool IsBound => _cfg != null && joints.Length == _cfg.JointCount && joints.Length > 0;

        // ---- base kinematics (Unity frame) ----
        public Vector3 BasePosition => root != null ? root.transform.position : transform.position;
        public Quaternion BaseRotation => root != null ? root.transform.rotation : transform.rotation;
        public Vector3 BaseForward => BaseRotation * Vector3.right;   // external +X
        public Vector3 BaseUp => BaseRotation * Vector3.up;           // external +Z
        public float UprightDot => Vector3.Dot(BaseUp, Vector3.up);

        public Vector3 BaseLinearVelocityWorld => root != null ? root.linearVelocity : Vector3.zero;
        public Vector3 BaseAngularVelocityWorld => root != null ? root.angularVelocity : Vector3.zero;
        public Vector3 BaseLinearVelocityBody => Quaternion.Inverse(BaseRotation) * BaseLinearVelocityWorld;
        public Vector3 BaseAngularVelocityBody => Quaternion.Inverse(BaseRotation) * BaseAngularVelocityWorld;
        public Vector3 ProjectedGravityBody => Quaternion.Inverse(BaseRotation) * Vector3.down;

        /// <summary>Resolves joints by name in policy order and applies drive gains. Returns true when every joint was found.</summary>
        public bool Bind(PolicyConfig cfg)
        {
            _cfg = cfg;
            if (root == null) root = GetComponentInChildren<ArticulationBody>();

            _lookup.Clear();
            foreach (var ab in GetComponentsInChildren<ArticulationBody>(true))
                if (!_lookup.ContainsKey(ab.name)) _lookup.Add(ab.name, ab);

            int n = cfg.JointCount;
            joints = new ArticulationBody[n];
            _sign = new float[n];
            _lowerExt = new float[n];
            _upperExt = new float[n];
            _defaultExt = new float[n];

            bool ok = true;
            for (int i = 0; i < n; i++)
            {
                JointSpec spec = cfg.joints[i];
                if (!_lookup.TryGetValue(spec.name, out var ab))
                {
                    Debug.LogError($"[CreatureRig] Joint '{spec.name}' not found under '{name}'.", this);
                    ok = false;
                    continue;
                }
                joints[i] = ab;
                _sign[i] = spec.sign == 0f ? -1f : Mathf.Sign(spec.sign);
                _lowerExt[i] = spec.lower;
                _upperExt[i] = spec.upper;
                _defaultExt[i] = spec.defaultPos;
                ConfigureDrive(ab, i, cfg);
            }

            if (root != null)
            {
                root.solverIterations = cfg.solverIterations;
                root.solverVelocityIterations = cfg.solverVelocityIterations;
            }

            if (feet == null || feet.Length == 0) feet = GetComponentsInChildren<FootContactSensor>(true);
            return ok;
        }

        void ConfigureDrive(ArticulationBody ab, int i, PolicyConfig cfg)
        {
            float s = _sign[i];
            float a = s * _lowerExt[i] * Mathf.Rad2Deg;
            float b = s * _upperExt[i] * Mathf.Rad2Deg;
            float lo = Mathf.Min(a, b);
            float hi = Mathf.Max(a, b);

            ab.jointType = ArticulationJointType.RevoluteJoint;
            ab.twistLock = ArticulationDofLock.LimitedMotion;
            ab.jointFriction = cfg.jointFriction;

            JointSpec spec = cfg.joints[i];
            ArticulationDrive d = ab.xDrive;
            d.lowerLimit = lo;
            d.upperLimit = hi;
            d.stiffness = spec.stiffness > 0f ? spec.stiffness : cfg.stiffness;
            d.damping = spec.damping > 0f ? spec.damping : cfg.damping;
            d.forceLimit = spec.forceLimit > 0f ? spec.forceLimit : cfg.forceLimit;
            // Force = real PD spring in N m/rad and N m s/rad (matches MuJoCo kp/kv). Target would position-lock the joint.
            d.driveType = ArticulationDriveType.Force;
            d.target = s * _defaultExt[i] * Mathf.Rad2Deg;
            d.targetVelocity = 0f;
            ab.xDrive = d;
        }

        /// <summary>Joint angles (rad) and velocities (rad/s) in the external convention, policy order.</summary>
        public void ReadJointState(float[] pos, float[] vel)
        {
            for (int i = 0; i < joints.Length; i++)
            {
                ArticulationBody ab = joints[i];
                if (ab == null) { pos[i] = 0f; vel[i] = 0f; continue; }
                float s = _sign[i];
                pos[i] = s * ab.jointPosition[0];
                vel[i] = s * ab.jointVelocity[0];
            }
        }

        /// <summary>Applies PD position targets given in radians, external convention, policy order.</summary>
        public void ApplyTargets(float[] targetsRad)
        {
            bool clamp = _cfg == null || _cfg.clampTargetsToJointLimits;
            for (int i = 0; i < joints.Length; i++)
            {
                ArticulationBody ab = joints[i];
                if (ab == null) continue;
                float t = targetsRad[i];
                if (clamp) t = Mathf.Clamp(t, _lowerExt[i], _upperExt[i]);
                ab.SetDriveTarget(ArticulationDriveAxis.X, _sign[i] * t * Mathf.Rad2Deg);
            }
        }

        public float DefaultPosition(int i) => _defaultExt[i];
        public float LowerLimit(int i) => _lowerExt[i];
        public float UpperLimit(int i) => _upperExt[i];

        /// <summary>Teleports the base and snaps every joint to its default angle with zero velocity.</summary>
        public void ResetPose(Vector3 position, Quaternion rotation)
        {
            if (root == null) return;
            root.TeleportRoot(position, rotation);
            root.linearVelocity = Vector3.zero;
            root.angularVelocity = Vector3.zero;

            for (int i = 0; i < joints.Length; i++)
            {
                ArticulationBody ab = joints[i];
                if (ab == null) continue;
                float unityRad = _sign[i] * _defaultExt[i];
                ab.jointPosition = new ArticulationReducedSpace(unityRad);
                ab.jointVelocity = new ArticulationReducedSpace(0f);
                ab.jointForce = new ArticulationReducedSpace(0f);
                ab.linearVelocity = Vector3.zero;
                ab.angularVelocity = Vector3.zero;
                ab.SetDriveTarget(ArticulationDriveAxis.X, unityRad * Mathf.Rad2Deg);
            }
        }

        public void ApplyImpulseToBase(Vector3 worldImpulse)
        {
            if (root != null) root.AddForce(worldImpulse, ForceMode.Impulse);
        }
    }
}
