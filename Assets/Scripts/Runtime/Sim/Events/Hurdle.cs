using System;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// One hurdle: a free rigid body standing on the deck, with nothing scripted about how it behaves.
    /// An athlete that hits it knocks it over, and whether the athlete stays on its feet afterwards is
    /// PhysX's business, not this component's.
    ///
    /// The mass sits low on purpose (<see cref="lowCentre"/>). A box whose centre of mass is at its
    /// geometric centre launches when it is struck near the top; a real hurdle is weighted at the feet so
    /// that it tips forward instead, which is both the correct behaviour and the one that gives a runner
    /// something to trip over rather than something to be hit by.
    ///
    /// Attribution is by walking up from whatever collider made contact to the <see cref="AthleteRig"/>
    /// that owns it, because an athlete's colliders are several levels below
    /// its athlete object and the hierarchy root is the spawner, not the runner.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Hurdle : MonoBehaviour
    {
        [Tooltip("Tilt past this and the hurdle counts as knocked over.")]
        public float knockedDegrees = 22f;
        [Tooltip("Shifted this far from its mark and it counts as knocked over, however upright it landed.")]
        public float knockedMetres = 0.3f;
        [Tooltip("Height of the centre of mass above the deck. Well below the bar, so a hit tips it.")]
        public float lowCentre = 0.12f;

        [Header("Sound")]
        [Tooltip("The hurdle going over: bar, frame and deck.")]
        public AudioClip clatter;
        [Tooltip("The hurdle clipped and left standing. This is the common case in a real race and it "
               + "sounds nothing like a topple, so it gets its own clip rather than a quieter clatter.")]
        public AudioClip clip;
        [Range(0f, 1f)] public float volume = 0.75f;
        public float maxDistance = 60f;
        [Tooltip("Impulse below which a contact is a brush and makes no sound at all.")]
        public float minClipImpulse = 0.6f;
        [Tooltip("Impulse that plays the clip at full level.")]
        public float referenceClipImpulse = 12f;

        /// <summary>True once this hurdle has been put down, until the next attempt stands it up again.</summary>
        public bool Knocked { get; private set; }

        /// <summary>The physics athlete last in contact before it went over, if it was one.</summary>
        public AthleteRig ByRig { get; private set; }

        /// <summary>
        /// Magnitude of the last contact impulse an athlete put into this hurdle, in N s, and where it
        /// landed. Kept because the hurdle is the only thing that ever sees the number: by the time
        /// <see cref="KnockedOver"/> fires a frame or two later, PhysX has moved on and the difference
        /// between a clip that happened to topple it and a runner demolishing it is gone.
        /// </summary>
        public float LastImpulse { get; private set; }

        /// <summary>Where that contact was, for anything drawing or shaking at the point of impact.</summary>
        public Vector3 LastContact { get; private set; }

        public event Action<Hurdle> KnockedOver;

        /// <summary>
        /// Raised on every athlete contact above <see cref="minClipImpulse"/>, whether or not the hurdle
        /// goes down — which is most of them, because clipping a hurdle and leaving it standing is the
        /// common case in a real race. Carries the impulse so the effects can be scaled by it.
        /// </summary>
        public event Action<Hurdle, float> Struck;

        Rigidbody _body;
        Vector3 _mark;
        Quaternion _markRot;
        AudioSource _src;

        void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _body.centerOfMass = new Vector3(0f, lowCentre, 0f);
            // The bar is a few centimetres deep and athletes arrive at up to 9 m/s; discrete contacts would
            // let a shin pass straight through it between two physics steps.
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _body.interpolation = RigidbodyInterpolation.Interpolate;

            _mark = transform.position;
            _markRot = transform.rotation;

            if (clatter == null && clip == null) return;
            _src = gameObject.AddComponent<AudioSource>();
            _src.clip = clatter;
            _src.playOnAwake = false;
            _src.spatialBlend = 1f;
            _src.rolloffMode = AudioRolloffMode.Linear;
            _src.minDistance = 4f;
            _src.maxDistance = maxDistance;
            _src.dopplerLevel = 0f;
            _src.priority = 96;
        }

        /// <summary>Where this hurdle stands. Called once as it is placed, before anything can hit it.</summary>
        public void SetMark(Vector3 position, Quaternion rotation)
        {
            _mark = position;
            _markRot = rotation;
            StandUp();
        }

        /// <summary>Back on its mark for the next attempt, still and upright.</summary>
        public void StandUp()
        {
            Knocked = false;
            ByRig = null;
            LastImpulse = 0f;
            transform.SetPositionAndRotation(_mark, _markRot);
            if (_body == null) return;
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
        }

        void FixedUpdate()
        {
            if (Knocked) return;
            bool tipped = Vector3.Angle(transform.up, Vector3.up) > knockedDegrees;
            bool shifted = (transform.position - _mark).sqrMagnitude > knockedMetres * knockedMetres;
            if (!tipped && !shifted) return;

            Knocked = true;
            if (_src != null && clatter != null)
            {
                _src.pitch = UnityEngine.Random.Range(0.92f, 1.1f);
                _src.PlayOneShot(clatter, volume * PoDecath.Audio.AudioMix.Level(PoDecath.Audio.AudioMix.Bus.Sfx));
            }
            KnockedOver?.Invoke(this);
        }

        /// <summary>
        /// Remembers who touched it last while it was still standing. Contacts after it has gone over are
        /// the deck and its neighbours, and are not worth crediting to anyone.
        /// </summary>
        void OnCollisionEnter(Collision c)
        {
            if (Knocked || c.collider == null) return;
            Clipped(c);
            AthleteRig rig = c.collider.GetComponentInParent<AthleteRig>();
            if (rig != null) ByRig = rig;
        }

        /// <summary>
        /// A contact, recorded and then heard. Scaled by the impulse, so a shin brushing the bar is almost
        /// silent and a hard clip that the hurdle survives is nearly as loud as one that it does not.
        /// Whether it stays up is decided a frame or two later in FixedUpdate; this is the moment of
        /// contact either way, which is why it does not wait to find out.
        ///
        /// The impulse is stored before the sound is played and regardless of whether there is a clip to
        /// play, because the effects layer and the camera shake both need it and neither should depend on
        /// whether an audio bank was baked.
        /// </summary>
        void Clipped(Collision c)
        {
            float impulse = c.impulse.magnitude;
            if (impulse < minClipImpulse) return;

            LastImpulse = impulse;
            LastContact = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
            Struck?.Invoke(this, impulse);

            if (_src == null || clip == null) return;
            float weight = Mathf.Clamp01(impulse / Mathf.Max(0.01f, referenceClipImpulse));
            _src.pitch = UnityEngine.Random.Range(0.95f, 1.12f);
            _src.PlayOneShot(clip, volume * (0.3f + 0.7f * weight) * PoDecath.Audio.AudioMix.Level(PoDecath.Audio.AudioMix.Bus.Sfx));
        }
    }
}
