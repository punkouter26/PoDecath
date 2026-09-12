using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Attach to the ArticulationBody that owns a foot collider. Reports whether the
    /// foot touched anything during the last physics step, how hard, and whether it slid.
    /// Allocation-free: uses Collision.GetContact instead of Collision.contacts.
    ///
    /// The slip reading is the one worth explaining. A foot that is planted and gripping has, by
    /// definition, almost no velocity along the ground at the contact point; a foot that is sliding has a
    /// lot. The difference between those two is what a skid is, it costs one <c>GetPointVelocity</c> per
    /// contact to measure, and it is also an honest signal about the policy: an athlete scrubbing speed
    /// through a bend is losing the corner whether or not it ends up on the deck. The deck does not move,
    /// so the foot's own point velocity is the relative velocity; a moving surface would need the other
    /// body's velocity subtracted here.
    /// </summary>
    public class FootContactSensor : MonoBehaviour
    {
        public Collider footCollider;

        public bool InContact { get; private set; }
        public float NormalForce { get; private set; }

        /// <summary>Contact impulse magnitude over the last physics step, in N s. Zero when not in contact.</summary>
        public float Impulse { get; private set; }

        /// <summary>
        /// How fast the foot is sliding along whatever it is touching, in m/s. Near zero for a planted
        /// foot; a scrubbing or skidding foot runs to several metres a second.
        /// </summary>
        public float SlipSpeed { get; private set; }

        /// <summary>Direction of the slide in world space, unit length, or zero when the foot is planted.</summary>
        public Vector3 SlipDirection { get; private set; }

        /// <summary>Where the foot met the ground last step, for anything drawing at the contact.</summary>
        public Vector3 Point { get; private set; }

        /// <summary>Surface normal at that contact.</summary>
        public Vector3 Normal { get; private set; } = Vector3.up;

        ArticulationBody _body;

        bool _touchedThisStep;
        float _impulseThisStep;
        float _slipThisStep;
        Vector3 _slipDirThisStep;
        Vector3 _pointThisStep;
        Vector3 _normalThisStep = Vector3.up;

        void Awake() => _body = GetComponent<ArticulationBody>();

        void FixedUpdate()
        {
            InContact = _touchedThisStep;
            NormalForce = _impulseThisStep / Mathf.Max(Time.fixedDeltaTime, 1e-5f);
            Impulse = _impulseThisStep;
            SlipSpeed = _touchedThisStep ? _slipThisStep : 0f;
            SlipDirection = _touchedThisStep ? _slipDirThisStep : Vector3.zero;
            if (_touchedThisStep) { Point = _pointThisStep; Normal = _normalThisStep; }

            _touchedThisStep = false;
            _impulseThisStep = 0f;
            _slipThisStep = 0f;
            _slipDirThisStep = Vector3.zero;
        }

        void OnCollisionStay(Collision c) => Accumulate(c);
        void OnCollisionEnter(Collision c) => Accumulate(c);

        void Accumulate(Collision c)
        {
            int n = c.contactCount;
            for (int i = 0; i < n; i++)
            {
                ContactPoint cp = c.GetContact(i);
                if (footCollider != null && cp.thisCollider != footCollider) continue;

                _touchedThisStep = true;
                _impulseThisStep += c.impulse.magnitude;
                _pointThisStep = cp.point;
                _normalThisStep = cp.normal;
                MeasureSlip(cp);
                return;
            }
        }

        /// <summary>
        /// The part of the foot's velocity at the contact point that lies along the surface. The normal
        /// component is the foot arriving or leaving, which is a footfall rather than a skid, so it is
        /// projected out.
        /// </summary>
        void MeasureSlip(ContactPoint cp)
        {
            if (_body == null) { _slipThisStep = 0f; return; }
            Vector3 v = _body.GetPointVelocity(cp.point);
            Vector3 tangential = v - Vector3.Dot(v, cp.normal) * cp.normal;
            float speed = tangential.magnitude;
            if (speed <= _slipThisStep) return;
            _slipThisStep = speed;
            _slipDirThisStep = speed > 1e-4f ? tangential / speed : Vector3.zero;
        }
    }
}
