using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// The hand-coded athlete, as the events see it.
    ///
    /// This used to be a pace profile: a transform swept along a line at a chosen speed, with a cosmetic
    /// bob, that could not be pushed off its course and never fell. It made a serviceable pacer and a
    /// meaningless opponent, because "the heuristic bot runs 9 m/s" was a fact about a constant in a
    /// script rather than about a body. Comparing a learned policy against it compared a physics problem
    /// with an animation.
    ///
    /// Now it is physical. The bot is built from the same MJCF as every other athlete, with the same
    /// colliders, the same ArticulationBody chain, the same per-joint PD gains and the same gravity, and
    /// it is driven by <see cref="HeuristicGait"/> -- arithmetic where the policies have a network. It
    /// can be shoved, it can trip over a hurdle, and it can fall over and stay there.
    ///
    /// This class is only the adapter: it keeps the small API the events already speak (reset onto a
    /// line or a track, go, stop, report speed and distance) and translates it into rig operations, so
    /// nothing upstream had to learn a new vocabulary.
    /// </summary>
    public class HeuristicRunner : MonoBehaviour
    {
        [Header("Wiring")]
        public AthleteRig rig;
        public HeuristicGait gait;

        [Header("Pace")]
        [Tooltip("Speed the gait is asked for. Unlike the old pace profile this is a request, not a "
               + "guarantee: what the body actually does is up to the controller and the physics.")]
        public float topSpeed = 3.0f;
        [Tooltip("Seconds spent easing the request up to topSpeed, so it does not lurch off the line.")]
        public float accelSeconds = 2.0f;

        public bool Running => gait != null && gait.Running;
        /// <summary>Forward speed measured on the rig, not integrated from a command.</summary>
        public float Speed { get; private set; }
        /// <summary>Distance travelled along the course, measured from the rig's own position.</summary>
        public float Distance { get; private set; }
        public Vector3 Start { get; private set; }
        public Vector3 Direction { get; private set; } = Vector3.right;

        float _t;
        Vector3 _startPos;

        // ---- straight-line courses -------------------------------------------------------------
        public void ResetTo(Vector3 start, Vector3 direction)
        {
            Start = start;
            Direction = direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.right;
            path = null;
            Place(start, Direction);
        }

        // ---- track courses ---------------------------------------------------------------------
        public TrackPath path;
        float _lateral;

        /// <summary>Arc length along <see cref="path"/>, read back from where the body actually is.</summary>
        public float S
        {
            get
            {
                if (path == null || rig == null) return Distance;
                return Mathf.Repeat(path.ProjectGlobal(rig.BasePosition), path.LapLength);
            }
        }

        public void ResetOnTrack(TrackPath p, float s, float lateral)
        {
            path = p;
            _lateral = lateral;
            Start = p.Position(s, lateral);
            Direction = p.Tangent(s);
            Place(Start, Direction);
        }

        void Place(Vector3 position, Vector3 direction)
        {
            Vector3 flat = new Vector3(direction.x, 0f, direction.z);
            Quaternion rot = flat.sqrMagnitude > 1e-6f
                ? Quaternion.FromToRotation(Vector3.right, flat.normalized)
                : Quaternion.identity;
            if (rig != null) rig.ResetPose(position, rot);
            else transform.SetPositionAndRotation(position, rot);
            _startPos = position;
            _t = 0f;
            Speed = 0f;
            Distance = 0f;
            Airborne = false;
            if (gait != null)
            {
                gait.desiredDirection = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.right;
                gait.desiredSpeed = 0f;
                gait.Halt();
            }
        }

        public void Go()
        {
            _t = 0f;
            if (gait != null) gait.Begin(Direction, 0f);
        }

        public void Stop()
        {
            Airborne = false;
            if (gait != null) gait.Halt();
        }

        // ---- the long jump take-off ------------------------------------------------------------
        /// <summary>True between the board and the sand.</summary>
        public bool Airborne { get; private set; }
        float _landY;

        /// <summary>
        /// Leaves the board. Where the old bot integrated a ballistic arc in script, this hands the
        /// same take-off velocity to the physics and lets PhysX fly the body, exactly as it does for
        /// the policy athletes.
        /// </summary>
        public void Launch(Vector3 velocity, float landY)
        {
            _landY = landY;
            Airborne = true;
            if (gait != null) gait.Halt();
            if (rig != null && rig.root != null)
                rig.root.AddForce(velocity * rig.root.mass, ForceMode.Impulse);
        }

        void FixedUpdate()
        {
            if (rig == null || !rig.IsBound) return;

            Vector3 dir = path != null ? path.Tangent(S) : Direction;
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude > 1e-6f) flat.Normalize(); else flat = Vector3.right;

            Speed = Vector3.Dot(rig.BaseLinearVelocityWorld, flat);
            Distance = path != null
                ? Distance   // the event reads S on a track; distance along a lap is its business
                : Vector3.Dot(rig.BasePosition - _startPos, flat);

            if (Airborne)
            {
                if (rig.BasePosition.y <= _landY) Airborne = false;
                return;
            }
            if (gait == null || !gait.Running) return;

            // Ease the ask up rather than demanding race pace from a standing start, which is the
            // quickest way to put a hand-tuned gait on its face.
            _t += Time.fixedDeltaTime;
            float k = accelSeconds > 0f ? Mathf.Clamp01(_t / accelSeconds) : 1f;
            gait.desiredSpeed = topSpeed * (1f - (1f - k) * (1f - k));
            gait.desiredDirection = flat;
        }
    }
}
