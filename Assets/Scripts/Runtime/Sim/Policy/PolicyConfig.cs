using System;
using UnityEngine;

namespace PoDecath.Sim
{
    [Serializable]
    public struct JointSpec
    {
        [Tooltip("Name of the ArticulationBody GameObject in the rig hierarchy (must match exactly).")]
        public string name;
        [Tooltip("Default joint angle in radians, external convention (Isaac Lab default_joint_pos).")]
        public float defaultPos;
        [Tooltip("Lower limit, radians, external convention.")]
        public float lower;
        [Tooltip("Upper limit, radians, external convention.")]
        public float upper;
        [Tooltip("Sign between the external joint angle and the Unity ArticulationBody angle. " +
                 "-1 when the Unity joint axis is the reflected external axis.")]
        public float sign;
        [Tooltip("Per-joint PD stiffness (N m / rad). 0 = use the config-wide value.")]
        public float stiffness;
        [Tooltip("Per-joint PD damping (N m s / rad). 0 = use the config-wide value.")]
        public float damping;
        [Tooltip("Per-joint torque limit (N m). 0 = use the config-wide value.")]
        public float forceLimit;
    }

    /// <summary>
    /// Describes the tensor contract and the physics parameters of one policy family.
    /// Defaults follow an Isaac Lab velocity-tracking task exported through rsl_rl.
    /// </summary>
    [CreateAssetMenu(menuName = "PoDecath/Policy Config", fileName = "PolicyConfig")]
    public class PolicyConfig : ScriptableObject
    {
        [Header("Joints (policy order)")]
        public JointSpec[] joints = Array.Empty<JointSpec>();

        [Header("Observation layout (Isaac Lab velocity task)")]
        public bool includeBaseLinearVelocity = true;
        public bool includeBaseAngularVelocity = true;
        public bool includeProjectedGravity = true;
        public bool includeVelocityCommand = true;
        public bool includeJointPositions = true;
        public bool includeJointVelocities = true;
        public bool includeLastActions = true;

        [Header("Height scan (0 = disabled)")]
        [Tooltip("Grid extent along the external X axis in metres.")]
        public float heightScanSizeX = 0f;
        [Tooltip("Grid extent along the external Y axis in metres.")]
        public float heightScanSizeY = 0f;
        public float heightScanResolution = 0.1f;
        [Tooltip("Isaac Lab: base_z - hit_z - heightScanOffset, clipped to +-1.")]
        public float heightScanOffset = 0.5f;
        public float heightScanMaxDistance = 2f;

        [Header("Observation scaling (applied before clipping, 1 = raw)")]
        public float linearVelocityScale = 1f;
        public float angularVelocityScale = 1f;
        public float jointPositionScale = 1f;
        public float jointVelocityScale = 1f;
        public float observationClip = 100f;

        [Header("Action mapping: target = default + action * actionScale")]
        public float actionScale = 0.25f;
        public float actionClip = 100f;
        public bool clampTargetsToJointLimits = true;

        [Header("Stepping")]
        [Tooltip("Physics steps per second. Sets Time.fixedDeltaTime = 1 / physicsHz.")]
        public int physicsHz = 200;
        [Tooltip("Physics steps per policy step. 200 Hz / 4 = 50 Hz control.")]
        public int controlDecimation = 4;
        public int solverIterations = 8;
        public int solverVelocityIterations = 2;

        [Header("PD drive (Isaac Lab ImplicitActuator)")]
        public float stiffness = 25f;
        public float damping = 0.5f;
        public float forceLimit = 23.7f;
        public float jointFriction = 0f;
        public float footStaticFriction = 1f;
        public float footDynamicFriction = 1f;

        [Header("Reset / termination defaults")]
        public float spawnHeight = 0.4f;
        public float minBaseHeight = 0.12f;
        [Tooltip("Terminate when dot(base up, world up) drops below this value.")]
        public float minUprightDot = 0.3f;
        public float episodeSeconds = 20f;

        public int JointCount => joints?.Length ?? 0;

        public int HeightScanCountX => (heightScanSizeX <= 0f || heightScanResolution <= 0f) ? 0
            : Mathf.FloorToInt(heightScanSizeX / heightScanResolution + 1e-4f) + 1;

        public int HeightScanCountY => (heightScanSizeY <= 0f || heightScanResolution <= 0f) ? 0
            : Mathf.FloorToInt(heightScanSizeY / heightScanResolution + 1e-4f) + 1;

        public int HeightScanCount => HeightScanCountX * HeightScanCountY;

        public int ObservationSize
        {
            get
            {
                int n = 0;
                if (includeBaseLinearVelocity) n += 3;
                if (includeBaseAngularVelocity) n += 3;
                if (includeProjectedGravity) n += 3;
                if (includeVelocityCommand) n += 3;
                if (includeJointPositions) n += JointCount;
                if (includeJointVelocities) n += JointCount;
                if (includeLastActions) n += JointCount;
                n += HeightScanCount;
                return n;
            }
        }

        public int ActionSize => JointCount;
        public float FixedDeltaTime => 1f / Mathf.Max(1, physicsHz);
        public float ControlHz => physicsHz / (float)Mathf.Max(1, controlDecimation);

        /// <summary>Fills the asset with the Unitree Go2 / Isaac Lab flat-terrain defaults (12 DoF).</summary>
        public void ApplyGo2Defaults()
        {
            // Isaac Lab joint order for Go2 (USD breadth-first): all hips, all thighs, all calves.
            joints = new[]
            {
                J("FL_hip_joint",    0.1f, -1.0472f,  1.0472f),
                J("FR_hip_joint",   -0.1f, -1.0472f,  1.0472f),
                J("RL_hip_joint",    0.1f, -1.0472f,  1.0472f),
                J("RR_hip_joint",   -0.1f, -1.0472f,  1.0472f),
                J("FL_thigh_joint",  0.8f, -1.5708f,  3.4907f),
                J("FR_thigh_joint",  0.8f, -1.5708f,  3.4907f),
                J("RL_thigh_joint",  1.0f, -0.5236f,  4.5379f),
                J("RR_thigh_joint",  1.0f, -0.5236f,  4.5379f),
                J("FL_calf_joint",  -1.5f, -2.7227f, -0.8378f),
                J("FR_calf_joint",  -1.5f, -2.7227f, -0.8378f),
                J("RL_calf_joint",  -1.5f, -2.7227f, -0.8378f),
                J("RR_calf_joint",  -1.5f, -2.7227f, -0.8378f),
            };
            actionScale = 0.25f;
            stiffness = 25f;
            damping = 0.5f;
            forceLimit = 23.7f;
            physicsHz = 200;
            controlDecimation = 4;
            spawnHeight = 0.4f;
            minBaseHeight = 0.12f;
        }

        static JointSpec J(string n, float d, float lo, float hi) =>
            new JointSpec { name = n, defaultPos = d, lower = lo, upper = hi, sign = -1f };
    }
}
