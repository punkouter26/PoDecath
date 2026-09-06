using System;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// A humanoid gait written by hand, on the same terms the learned policies get.
    ///
    /// The old heuristic bot was a pace profile: it moved a transform along a line at a chosen speed and
    /// could not fall, because nothing about it was physical. That made it a useful pacer and a
    /// meaningless opponent -- "the RED bot runs 9 m/s" was a statement about a number in a script, not
    /// about a body. This drives the same <see cref="AthleteRig"/> the policies drive, through the same
    /// <see cref="AthleteRig.ApplyTargets"/> call, into the same ArticulationDrive PD controllers with
    /// the same per-joint gains from the MJCF, against the same colliders and the same gravity. The only
    /// difference from a policy is where the joint targets come from: arithmetic here, a network there.
    ///
    /// It will not beat the trained policy and is not meant to. It is meant to make the comparison real.
    ///
    /// Where it actually stands, measured
    /// ----------------------------------
    /// Honestly: it balances, and it does not yet walk. Scored by `PoDecath/Probe Heuristic Gait`, which
    /// silences the race and gives every controller the same start on the rooftop deck:
    ///
    ///     drives holding the default pose, no controller   falls at 2.1 s
    ///     this controller, standing                        falls at 4.5 s
    ///     the trained policy (athlete_track.onnx)          falls at 6.1 s, +2.0 m
    ///     this controller, walking                         falls at 2.1 s, going backwards
    ///
    /// So the balance half is real -- it more than doubles how long a passive rig stays up, on the same
    /// gains and the same body -- and the stepping half is not there yet. The walk falls faster than the
    /// stand, which means the leg trajectory is currently costing more stability than the feedback buys.
    /// That is the honest state, and the probe is how the next attempt gets measured rather than admired.
    ///
    /// One thing the tuning did settle, and it is a fact about this rig rather than about controllers.
    /// The foot box sits 0.12 m in front of the ankle with a 0.16 m half-length, so there is roughly
    /// 0.28 m of lever ahead of the joint and 0.04 m behind it. Standing bolt upright, the body can
    /// resist pitching forward with about 200 N m of centre-of-pressure shift and pitching backward with
    /// about 30 -- so it falls over backwards, which is exactly what it did. Carrying the mass a little
    /// forward (pitchSetpoint) is what took the stand from 2.2 s to 4.5 s; leaning too far, past about
    /// 0.3, tips it the other way and is worse than not leaning at all.
    ///
    /// How it works
    /// ------------
    /// A phase clock runs the legs in antiphase. Each leg follows a scripted trajectory in hip pitch,
    /// knee and ankle pitch -- swing forward with the knee tucked for ground clearance, then stance
    /// driving the body over the foot and pushing off. That alone produces a machine that walks for
    /// about a step and a half and then falls over, because an open-loop gait has no answer to the
    /// body's own momentum. So four feedback terms sit on top, and they are what actually keeps it up:
    ///
    ///   * **Pitch.** The trunk's lean and lean rate bias both hips. Falling forward lengthens the
    ///     reach of the swing leg, which is the only thing that can catch it.
    ///   * **Foot placement.** Raibert's rule: put the foot where the velocity error says it belongs,
    ///     roughly half a stance period ahead plus a correction on the speed error. This is the term
    ///     that regulates speed, far more than the step frequency does.
    ///   * **Roll.** A biped that does not shift its weight sideways falls sideways. The pelvis is
    ///     driven over the stance foot through hip abduction, in phase with the clock, with the base's
    ///     own roll and roll rate correcting it.
    ///   * **Ankle.** Small, fast corrections against pitch rate while a foot is down -- the ankle
    ///     strategy, worth little on its own and cheap to add.
    ///
    /// Arms swing in antiphase to the legs. That is not decoration: it cancels the angular momentum the
    /// legs inject about the vertical axis, and without it the torso yaws itself off course.
    ///
    /// Everything is expressed in the external (MuJoCo) joint convention in radians, exactly as
    /// <see cref="PolicyRunner"/> does, and <see cref="AthleteRig.ApplyTargets"/> handles the sign flip
    /// and the clamp to joint limits.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class HeuristicGait : MonoBehaviour
    {
        [Header("Wiring")]
        public AthleteRig rig;

        [Header("Command")]
        [Tooltip("Speed asked for, m/s. The gait regulates toward this; it does not simply play faster.")]
        public float desiredSpeed = 2.0f;
        [Tooltip("Where to run. Overwritten by the event or the track follower each step.")]
        public Vector3 desiredDirection = Vector3.right;

        [Header("Gait clock")]
        [Tooltip("Steps per second at desiredSpeed. Humans hold roughly this and vary step length instead.")]
        public float stepHz = 1.6f;
        [Range(0.5f, 0.8f)]
        [Tooltip("Fraction of a leg's cycle spent on the ground. Above 0.5 both feet are down together "
               + "for part of the cycle, which is what makes it a walk rather than a run -- and what "
               + "makes it survivable without a flight phase to get wrong.")]
        public float stanceFraction = 0.62f;

        [Header("Leg trajectory (radians)")]
        public float hipSwing = 0.45f;      // how far the hip reaches forward in swing
        public float hipExtend = 0.15f;     // how far it drives back through stance
        public float kneeLift = 0.95f;      // knee tuck at mid-swing, for ground clearance
        public float kneeStance = 0.12f;    // slight bend on contact, to absorb
        public float ankleLift = 0.20f;     // toes up in swing, so they do not catch
        public float anklePush = 0.28f;     // push-off at the end of stance

        [Header("Balance")]
        [Tooltip("Lean held while balancing, as dot(base up, forward). Positive is nose-down. "
               + "Not cosmetic. This rig's foot box sits 0.12 m in front of the ankle with a 0.16 m "
               + "half-length, so there is about 0.28 m of lever ahead of the joint and 0.04 m behind "
               + "it: it can resist pitching forward with roughly 200 N m of centre-of-pressure shift "
               + "and pitching backward with about 30. Standing bolt upright therefore falls over "
               + "backwards, which is exactly what it did. Carrying the mass slightly forward puts the "
               + "centre of pressure in the middle of the foot where there is authority in both "
               + "directions -- the same reason people stand with their weight over the balls of "
               + "their feet.")]
        public float pitchSetpoint = 0.20f;
        public float pitchGain = -3.0f;
        public float pitchRateGain = -0.7f;
        public float rollGain = -1.1f;
        public float rollRateGain = -0.22f;
        [Tooltip("Raibert foot placement: how hard the speed error moves the footfall.")]
        public float placementGain = 0.16f;
        [Tooltip("Baseline hip abduction, radians. A wider stance is easier to balance and looks it.")]
        public float stanceWidth = 0.08f;
        [Tooltip("How far the pelvis rides over the stance foot each step.")]
        public float weightShift = 0.10f;
        public float ankleRateGain = -0.35f;

        [Header("Upper body")]
        public float armSwing = 0.55f;
        public float elbowBend = 0.7f;
        public float torsoPitch = 0.10f;

        /// <summary>Metres travelled since <see cref="Begin"/>, measured on the rig, not integrated from a command.</summary>
        public float Distance { get; private set; }
        /// <summary>Forward speed along the commanded direction, m/s.</summary>
        public float Speed { get; private set; }
        public bool Running { get; private set; }

        float[] _targets;
        float[] _pos, _vel;
        float _phase;
        Vector3 _startPos;
        int _n;

        // Joint indices, resolved by name once. -1 for anything the rig does not have, so a different
        // skeleton degrades to whatever it does have rather than throwing.
        int _abdY = -1, _abdX = -1;
        int _hipYL = -1, _hipXL = -1, _kneeL = -1, _ankYL = -1, _ankXL = -1;
        int _hipYR = -1, _hipXR = -1, _kneeR = -1, _ankYR = -1, _ankXR = -1;
        int _shXL = -1, _elbL = -1, _shXR = -1, _elbR = -1;

        void Awake()
        {
            if (rig == null) rig = GetComponentInChildren<AthleteRig>();
            Resolve();
        }

        void Resolve()
        {
            if (rig == null || rig.Config == null) return;
            _n = rig.Config.JointCount;
            _targets = new float[_n];
            _pos = new float[_n];
            _vel = new float[_n];
            for (int i = 0; i < _n; i++)
            {
                switch (rig.Config.joints[i].name)
                {
                    case "abdomen_y": _abdY = i; break;
                    case "abdomen_x": _abdX = i; break;
                    case "hip_y_l": _hipYL = i; break;
                    case "hip_x_l": _hipXL = i; break;
                    case "knee_l": _kneeL = i; break;
                    case "ankle_y_l": _ankYL = i; break;
                    case "ankle_x_l": _ankXL = i; break;
                    case "hip_y_r": _hipYR = i; break;
                    case "hip_x_r": _hipXR = i; break;
                    case "knee_r": _kneeR = i; break;
                    case "ankle_y_r": _ankYR = i; break;
                    case "ankle_x_r": _ankXR = i; break;
                    case "shoulder_x_l": _shXL = i; break;
                    case "elbow_l": _elbL = i; break;
                    case "shoulder_x_r": _shXR = i; break;
                    case "elbow_r": _elbR = i; break;
                }
            }
        }

        /// <summary>Start walking. The clock starts mid-stance so the first move is a weight shift, not a step.</summary>
        public void Begin(Vector3 direction, float speed)
        {
            if (_targets == null) Resolve();
            desiredDirection = direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.right;
            desiredSpeed = speed;
            _phase = 0f;
            Distance = 0f;
            Speed = 0f;
            _startPos = rig != null ? rig.BasePosition : transform.position;
            Running = true;
        }

        public void Halt() => Running = false;

        void FixedUpdate()
        {
            if (rig == null || !rig.IsBound) return;
            if (_targets == null || _targets.Length != rig.Config.JointCount) Resolve();
            if (_targets == null) return;

            float dt = Time.fixedDeltaTime;
            rig.ReadJointState(_pos, _vel);

            // ---- body state, in the frame we are trying to run along ----
            Vector3 fwd = new Vector3(desiredDirection.x, 0f, desiredDirection.z);
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.right;
            Vector3 left = Vector3.Cross(Vector3.up, fwd);

            Vector3 v = rig.BaseLinearVelocityWorld;
            Speed = Vector3.Dot(v, fwd);
            float vLat = Vector3.Dot(v, left);

            Vector3 up = rig.BaseUp;
            // Lean, signed: positive when the chest has pitched forward past vertical.
            float pitch = Vector3.Dot(up, fwd);
            float roll = Vector3.Dot(up, left);
            Vector3 w = rig.BaseAngularVelocityWorld;
            float pitchRate = Vector3.Dot(w, left);
            float rollRate = Vector3.Dot(w, fwd);

            if (!Running)
            {
                Stand(pitch, pitchRate, roll, rollRate);
                rig.ApplyTargets(_targets);
                return;
            }

            Distance = Vector3.Dot(rig.BasePosition - _startPos, fwd);

            // ---- clock ----
            // Step frequency rises a little with the ask, but most of the speed comes from step length
            // via the placement term below. Driving speed with frequency alone is what makes scripted
            // gaits scuttle and then trip.
            float hz = stepHz * Mathf.Clamp(0.75f + 0.25f * (desiredSpeed / Mathf.Max(0.5f, 2.0f)), 0.75f, 1.6f);
            _phase = Mathf.Repeat(_phase + hz * dt, 1f);

            float phaseL = _phase;
            float phaseR = Mathf.Repeat(_phase + 0.5f, 1f);

            // ---- feedback shared by both legs ----
            float vErr = desiredSpeed - Speed;
            // Raibert: reach further ahead when we are going too fast (to brake) or leaning (to catch).
            float pitchErr = pitch - pitchSetpoint;
            float reach = placementGain * (-vErr) + pitchGain * pitchErr + pitchRateGain * pitchRate;
            reach = Mathf.Clamp(reach, -0.5f, 0.7f);

            float lateral = rollGain * roll + rollRateGain * rollRate + 0.05f * vLat;
            lateral = Mathf.Clamp(lateral, -0.35f, 0.35f);

            for (int i = 0; i < _n; i++) _targets[i] = rig.DefaultPosition(i);

            Leg(phaseL, +1f, reach, lateral, pitchRate, _hipYL, _hipXL, _kneeL, _ankYL, _ankXL);
            Leg(phaseR, -1f, reach, lateral, pitchRate, _hipYR, _hipXR, _kneeR, _ankYR, _ankXR);

            // ---- trunk: hold it a touch forward of vertical and resist the lean ----
            Set(_abdY, Mathf.Clamp(-torsoPitch - 0.6f * pitchErr - 0.1f * pitchRate, -0.5f, 0.4f));
            Set(_abdX, Mathf.Clamp(-0.4f * roll, -0.3f, 0.3f));

            // ---- arms, antiphase to the leg on the same side ----
            float swingL = Mathf.Sin(phaseL * 2f * Mathf.PI);
            Set(_shXL, -armSwing * swingL);
            Set(_shXR, armSwing * swingL);          // mirrored range; opposite sign is the same motion
            Set(_elbL, -elbowBend);
            Set(_elbR, elbowBend);

            rig.ApplyTargets(_targets);
        }

        /// <summary>
        /// One leg's scripted trajectory plus the feedback that makes it survivable.
        /// <paramref name="side"/> is +1 for the left leg and -1 for the right.
        /// </summary>
        void Leg(float phase, float side, float reach, float lateral, float pitchRate,
                 int hipY, int hipX, int knee, int ankY, int ankX)
        {
            bool stance = phase < stanceFraction;
            float hipTarget, kneeTarget, ankTarget;

            if (stance)
            {
                float u = phase / stanceFraction;                 // 0 at touchdown, 1 at toe-off
                // Hip travels from reaching-forward to driving-back, which is what moves the body.
                hipTarget = Mathf.Lerp(-hipSwing - reach, hipExtend, u);
                // Absorb on contact, straighten through mid-stance, and stay straight for push-off.
                kneeTarget = kneeStance * Mathf.Sin(Mathf.PI * Mathf.Clamp01(u * 1.6f));
                // Plantarflex late: the push.
                ankTarget = -anklePush * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((u - 0.55f) / 0.45f));
                // Ankle strategy, only while the foot is actually down.
                ankTarget += Mathf.Clamp(ankleRateGain * pitchRate, -0.15f, 0.15f);
            }
            else
            {
                float u = (phase - stanceFraction) / (1f - stanceFraction);   // 0 at toe-off, 1 at touchdown
                // Reach forward, easing in and out so the foot is not thrown.
                hipTarget = Mathf.Lerp(hipExtend, -hipSwing - reach, Mathf.SmoothStep(0f, 1f, u));
                // Tuck the knee mid-swing for clearance, extend before touchdown.
                kneeTarget = kneeLift * Mathf.Sin(Mathf.PI * u);
                // Toes up so the foot does not catch, relaxing just before contact.
                ankTarget = ankleLift * Mathf.Sin(Mathf.PI * Mathf.Clamp01(u * 0.9f));
            }

            // Lateral: ride the pelvis over whichever foot is down. The shift leads the stance phase,
            // because the weight has to arrive before the other foot leaves.
            float shift = weightShift * Mathf.Cos(phase * 2f * Mathf.PI);
            float abduct = side * (stanceWidth + shift) + lateral;

            Set(hipY, hipTarget);
            Set(knee, Mathf.Max(0f, kneeTarget));      // knees do not hyperextend
            Set(ankY, ankTarget);
            Set(hipX, Mathf.Clamp(abduct, -0.5f, 0.5f));
            Set(ankX, Mathf.Clamp(-0.5f * lateral, -0.3f, 0.3f));
        }

        /// <summary>Standing still: feet under the hips, and the same balance terms holding it there.</summary>
        void Stand(float pitch, float pitchRate, float roll, float rollRate)
        {
            for (int i = 0; i < _n; i++) _targets[i] = rig.DefaultPosition(i);
            float pitchErr = pitch - pitchSetpoint;
            float hip = Mathf.Clamp(pitchGain * pitchErr + pitchRateGain * pitchRate, -0.4f, 0.4f);
            float ank = Mathf.Clamp(-0.5f * (pitchGain * pitchErr + pitchRateGain * pitchRate), -0.3f, 0.3f);
            float lat = Mathf.Clamp(rollGain * roll + rollRateGain * rollRate, -0.3f, 0.3f);
            Set(_hipYL, -hip); Set(_hipYR, -hip);
            Set(_kneeL, 0.10f); Set(_kneeR, 0.10f);
            Set(_ankYL, ank); Set(_ankYR, ank);
            Set(_hipXL, stanceWidth + lat); Set(_hipXR, -stanceWidth + lat);
            Set(_abdY, Mathf.Clamp(-0.6f * pitchErr, -0.3f, 0.3f));
        }

        void Set(int i, float radians)
        {
            if (i >= 0 && i < _n) _targets[i] = radians;
        }
    }
}
