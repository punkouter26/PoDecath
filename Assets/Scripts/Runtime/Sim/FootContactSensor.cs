using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Attach to the ArticulationBody that owns a foot collider. Reports whether the
    /// foot touched anything during the last physics step and the normal force magnitude.
    /// Allocation-free: uses Collision.GetContact instead of Collision.contacts.
    /// </summary>
    public class FootContactSensor : MonoBehaviour
    {
        public Collider footCollider;

        public bool InContact { get; private set; }
        public float NormalForce { get; private set; }

        bool _touchedThisStep;
        float _impulseThisStep;

        void FixedUpdate()
        {
            InContact = _touchedThisStep;
            NormalForce = _impulseThisStep / Mathf.Max(Time.fixedDeltaTime, 1e-5f);
            _touchedThisStep = false;
            _impulseThisStep = 0f;
        }

        void OnCollisionStay(Collision c) => Accumulate(c);
        void OnCollisionEnter(Collision c) => Accumulate(c);

        void Accumulate(Collision c)
        {
            int n = c.contactCount;
            for (int i = 0; i < n; i++)
            {
                ContactPoint cp = c.GetContact(i);
                if (footCollider == null || cp.thisCollider == footCollider)
                {
                    _touchedThisStep = true;
                    _impulseThisStep += c.impulse.magnitude;
                    return;
                }
            }
        }
    }
}
