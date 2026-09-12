using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// How hard one athlete is actually working, read off the joints rather than guessed from its speed.
    ///
    /// Everything a viewer wants to know about effort is already in the articulation chain and nothing was
    /// looking at it. Each joint has a torque this step and a drive force limit it was configured with, and
    /// the ratio of the two is the single most honest answer to "is it straining or is it cruising" that
    /// this simulation can give. Multiply the torque by the joint's own angular velocity and it is
    /// mechanical power in watts; integrate that and it is work done in joules.
    ///
    /// **Nothing here changes the physics, and that is deliberate.** It would be easy, and wrong, to let
    /// <see cref="Fatigue"/> pull the drive force limits down as a race goes on. Every policy in this
    /// project was trained against constant limits (23.7 N m on the shipped config), so quietly lowering
    /// them mid-race would put a trained athlete outside the envelope it learned in and drop it on the deck
    /// for a reason no training curve could ever explain. This component only ever reads. What consumes it
    /// -- the stress skeleton, the strain bar, the breathing -- is presentation, and presentation cannot
    /// make an athlete fall over.
    ///
    /// Sampled on the control step (50 Hz by default) rather than every physics step: the numbers are
    /// smoothed over roughly a second anyway, so four times the sampling would buy four times the cost and
    /// no extra information.
    /// </summary>
    [DefaultExecutionOrder(-40)]   // after PolicyRunner (-50): reads the torques this step's drives produced
    public class EffortMeter : MonoBehaviour
    {
        [Tooltip("The body to read. Found on this object or below it if not set.")]
        public AthleteRig rig;

        [Tooltip("Physics steps between samples. 4 matches the policy's own control decimation.")]
        public int decimation = 4;

        [Header("Fatigue (cosmetic)")]
        [Tooltip("Mechanical work, in joules, at which Fatigue reads 1. Calibrated by watching a 400 m, "
               + "not derived from anything: it decides when the breathing gets ragged and nothing else. "
               + "Lower makes athletes look tired sooner.")]
        public float fatigueJoules = 16000f;

        [Tooltip("How fast fatigue bleeds off while an athlete is doing nothing, as a fraction per second. "
               + "A stopped finisher gets its breath back over about half a minute.")]
        public float recoveryPerSecond = 0.03f;

        [Tooltip("Power below which an athlete counts as resting and starts recovering.")]
        public float restingWatts = 40f;

        // Smoothing over roughly the last second of control steps at 50 Hz. Fast enough to catch the surge
        // into a bend, slow enough that the strain bar does not strobe.
        const float Ema = 0.04f;

        float[] _saturation = new float[0];
        float[] _limit = new float[0];
        int _step;
        float _work;

        /// <summary>Per-joint drive torque as a fraction of that joint's own force limit, 0..1, smoothed.</summary>
        public float[] Saturation => _saturation;

        /// <summary>How many joints this is reporting on. Zero until the rig is bound.</summary>
        public int JointCount => _saturation.Length;

        /// <summary>Mean saturation across the whole body, 0..1. The number the strain bar draws.</summary>
        public float Effort { get; private set; }

        /// <summary>The hardest-worked joint's saturation this sample, 0..1.</summary>
        public float PeakSaturation { get; private set; }

        /// <summary>Index of that joint, for anything that wants to point at it. -1 before the first sample.</summary>
        public int PeakJoint { get; private set; } = -1;

        /// <summary>Mechanical power the drives are putting into the body right now, in watts, smoothed.</summary>
        public float Watts { get; private set; }

        /// <summary>Total mechanical work done this attempt, in joules.</summary>
        public float Joules => _work;

        /// <summary>
        /// 0..1 tiredness. Cosmetic only — see the class note. Drives the breathing, the strain bar's
        /// colour, and nothing that touches a drive.
        /// </summary>
        public float Fatigue { get; private set; }

        /// <summary>Where a joint is in the world, for anything drawing on top of the body.</summary>
        public Vector3 JointPosition(int i)
        {
            if (rig == null || rig.joints == null || i < 0 || i >= rig.joints.Length) return transform.position;
            ArticulationBody ab = rig.joints[i];
            return ab != null ? ab.transform.position : transform.position;
        }

        void Awake()
        {
            if (rig == null) rig = GetComponentInChildren<AthleteRig>();
        }

        /// <summary>Clears the accumulated work. Called when an attempt restarts, so fatigue is not inherited.</summary>
        public void ResetForAttempt()
        {
            _work = 0f;
            Fatigue = 0f;
            Effort = 0f;
            Watts = 0f;
            PeakSaturation = 0f;
            PeakJoint = -1;
            for (int i = 0; i < _saturation.Length; i++) _saturation[i] = 0f;
        }

        void FixedUpdate()
        {
            if (rig == null || !rig.IsBound) return;
            if (++_step % Mathf.Max(1, decimation) != 0) return;
            EnsureBuffers();
            Sample(Time.fixedDeltaTime * Mathf.Max(1, decimation));
        }

        /// <summary>
        /// Caches each joint's configured force limit once. It is read off the drive rather than off the
        /// <see cref="PolicyConfig"/> so that whatever the rig was actually given is what the ratio is
        /// measured against — a per-joint limit from the MJCF actuators overrides the config's fallback,
        /// and reading the config would silently compare against a number the joint never saw.
        /// </summary>
        void EnsureBuffers()
        {
            int n = rig.joints.Length;
            if (_saturation.Length == n) return;
            _saturation = new float[n];
            _limit = new float[n];
            for (int i = 0; i < n; i++)
            {
                ArticulationBody ab = rig.joints[i];
                float limit = ab != null ? ab.xDrive.forceLimit : 0f;
                // A limit of zero or infinity means "unconstrained", and dividing by it gives a saturation
                // that is either meaningless or always zero. Fall back to the shipped torque limit so the
                // reading stays comparable across athletes instead of going blank.
                _limit[i] = limit > 0f && !float.IsInfinity(limit) ? limit : 23.7f;
            }
        }

        void Sample(float dt)
        {
            int n = _saturation.Length;
            if (n == 0) return;

            float sumSat = 0f, power = 0f, peak = 0f;
            int peakAt = -1;

            for (int i = 0; i < n; i++)
            {
                ArticulationBody ab = rig.joints[i];
                if (ab == null) continue;

                // driveForce, not jointForce. Both are torques in N m in reduced coordinates and element 0
                // is the one this rig's revolute joints use, but they answer different questions:
                // jointForce is the total generalised force at the joint, constraints and contacts
                // included, while driveForce is what the PD drive itself produced — and driveForce is
                // exactly the quantity that forceLimit caps. Measuring the total against the drive's limit
                // would report saturation on a joint that is merely being leant on by the deck.
                //
                // Sign is the Unity convention and effort has no direction, so it is taken absolute here.
                float torque = Mathf.Abs(ab.driveForce[0]);
                float omega = Mathf.Abs(ab.jointVelocity[0]);

                float s = Mathf.Clamp01(torque / _limit[i]);
                _saturation[i] += (s - _saturation[i]) * Ema;
                sumSat += _saturation[i];
                if (_saturation[i] > peak) { peak = _saturation[i]; peakAt = i; }

                power += torque * omega;
            }

            Effort += (sumSat / n - Effort) * Ema;
            Watts += (power - Watts) * Ema;
            PeakSaturation = peak;
            PeakJoint = peakAt;

            // Work is the un-smoothed power over the sample window: smoothing it first would lag the
            // integral, and this is the one number here that should be an honest accumulation.
            if (power > restingWatts)
            {
                _work += power * dt;
                Fatigue = Mathf.Clamp01(_work / Mathf.Max(1f, fatigueJoules));
            }
            else
            {
                Fatigue = Mathf.Max(0f, Fatigue - recoveryPerSecond * dt);
                _work = Fatigue * fatigueJoules;
            }
        }
    }
}
