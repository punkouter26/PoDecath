using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Heuristic-coded sprinter (house rule: RED). Kinematic pace profile along a straight lane:
    /// accelerates to top speed over accelSeconds, then holds it. No physics, never falls.
    /// </summary>
    public class HeuristicRunner : MonoBehaviour
    {
        public float topSpeed = 9.0f;
        public float accelSeconds = 3.0f;
        public float bobAmplitude = 0.03f;
        public float bobHz = 3.0f;

        public bool Running { get; private set; }
        public float Speed { get; private set; }
        public float Distance { get; private set; }
        public Vector3 Start { get; private set; }
        public Vector3 Direction { get; private set; } = Vector3.right;

        float _t;
        float _baseY;

        public void ResetTo(Vector3 start, Vector3 direction)
        {
            Start = start;
            Direction = direction.normalized;
            transform.position = start;
            // The root's +X is the athlete's forward axis (same convention as the physics rig).
            Vector3 flat = new Vector3(Direction.x, 0f, Direction.z);
            transform.rotation = flat.sqrMagnitude > 1e-6f ? Quaternion.FromToRotation(Vector3.right, flat.normalized) : Quaternion.identity;
            _baseY = start.y;
            _t = 0f;
            Speed = 0f;
            Distance = 0f;
            Running = false;
        }

        public void Go() => Running = true;
        public void Stop() { Running = false; Airborne = false; }

        // ---- flight mode: the long jump take-off ----
        /// <summary>True between the take-off board and the sand.</summary>
        public bool Airborne { get; private set; }
        Vector3 _vel;
        float _landY;

        /// <summary>
        /// Leaves the ground on a ballistic arc and lands when the root drops to <paramref name="landY"/>.
        /// The RL athletes get their arc from PhysX; this bot has no physics at all, so its jump is
        /// integrated here from the same take-off velocity the event hands the physics athletes.
        /// </summary>
        public void Launch(Vector3 velocity, float landY)
        {
            Running = false;
            Airborne = true;
            _vel = velocity;
            _landY = landY;
        }

        // ---- track mode: follow a TrackPath instead of a straight line ----
        public TrackPath path;
        float _s;
        float _lateral;

        public void ResetOnTrack(TrackPath p, float s, float lateral)
        {
            path = p;
            _s = s;
            _lateral = lateral;
            Start = p.Position(s, lateral);
            Direction = p.Tangent(s);
            transform.position = Start;
            Vector3 flat = new Vector3(Direction.x, 0f, Direction.z);
            transform.rotation = flat.sqrMagnitude > 1e-6f ? Quaternion.FromToRotation(Vector3.right, flat.normalized) : Quaternion.identity;
            _baseY = Start.y;
            _t = 0f;
            Speed = 0f;
            Distance = 0f;
            Running = false;
        }

        void Update()
        {
            if (Airborne)
            {
                float fdt = Time.deltaTime;
                _vel += Physics.gravity * fdt;
                Vector3 next = transform.position + _vel * fdt;
                Distance += new Vector2(_vel.x, _vel.z).magnitude * fdt;
                Speed = _vel.magnitude;
                if (next.y <= _landY) { next.y = _landY; Airborne = false; Speed = 0f; }
                transform.position = next;
                return;
            }
            if (!Running) return;
            float dt = Time.deltaTime;
            _t += dt;
            float k = accelSeconds > 0f ? Mathf.Clamp01(_t / accelSeconds) : 1f;
            Speed = topSpeed * (1f - (1f - k) * (1f - k));   // ease-out acceleration
            Distance += Speed * dt;
            float bob = Mathf.Abs(Mathf.Sin(_t * bobHz * Mathf.PI)) * bobAmplitude * Mathf.Clamp01(Speed / topSpeed);
            if (path != null)
            {
                _s += Speed * dt;
                Vector3 p = path.Position(_s, _lateral);
                p.y += bob;
                transform.position = p;
                Vector3 t = path.Tangent(_s);
                transform.rotation = Quaternion.FromToRotation(Vector3.right, new Vector3(t.x, 0f, t.z).normalized);
                return;
            }
            Vector3 q = Start + Direction * Distance;
            q.y = _baseY + bob;
            transform.position = q;
        }
    }
}
