using System;
using UnityEngine;
using PoDecath.Sim;

namespace PoDecath.Audio
{
    /// <summary>
    /// One athlete's feet. Attached to the body that actually moves — the articulation root for an RL
    /// athlete, the runner object for the heuristic bot — so the sound comes from the right place in the
    /// stadium and pans and falls off with the broadcast camera.
    ///
    /// A physics athlete needs nothing invented: <see cref="FootContactSensor"/> is already on every foot
    /// for the policy's observations, so a step is the rising edge of a contact and its weight is the
    /// normal force that came with it. The heuristic bot has no physics at all, so its steps are counted
    /// off the distance it has covered instead — one every <see cref="strideMetres"/>, which is what keeps
    /// the RED pacer from running in silence next to a field that does not.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class FootstepAudio : MonoBehaviour
    {
        public AudioBank bank;
        [Tooltip("Set for a physics athlete: steps come from the foot contact sensors.")]
        public AthleteRig rig;
        [Tooltip("Set for the kinematic bot: steps are counted off distance covered.")]
        public HeuristicRunner heuristic;

        [Header("Mix")]
        [Range(0f, 1f)] public float volume = 0.55f;
        [Tooltip("Beyond this the step is inaudible; the broadcast cameras sit 8-40 m off the deck.")]
        public float maxDistance = 45f;
        [Tooltip("Doppler on the footfalls: a runner passing the rail camera bends in pitch. Zeroed for a "
               + "moment after every cut by AudioMix, because a cut is not motion.")]
        [Range(0f, 2f)] public float doppler = 0.55f;

        [Header("Physics athlete")]
        [Tooltip("Contacts below this are the foot resting or brushing, not a step.")]
        public float minForce = 60f;
        [Tooltip("Normal force that counts as a step at full weight.")]
        public float referenceForce = 600f;
        [Tooltip("Shortest gap between two steps from the same foot; below it, contact chatter is one step.")]
        public float refractorySeconds = 0.12f;

        [Header("Kinematic bot")]
        [Tooltip("Distance covered per footfall. A sprinter's stride is about 2.1 m and lands twice in it.")]
        public float strideMetres = 1.05f;

        [Header("Breathing")]
        [Tooltip("Working hard is audible from a few metres. Off for a field of sixteen on a phone.")]
        public bool breathe = true;
        [Tooltip("Optional. With one, breathing follows the athlete's actual joint effort and how long it "
               + "has been working, not just how fast it is going. Found on this object if not set.")]
        public EffortMeter effort;
        [Tooltip("Speed at which the athlete is breathing at full level.")]
        public float breathReferenceSpeed = 7f;
        [Range(0f, 1f)] public float breathVolume = 0.35f;
        [Tooltip("Beyond this nobody can hear anyone breathing, which is most of a stadium.")]
        public float breathDistance = 14f;

        /// <summary>
        /// Raised on every step this component detects, with the world point the foot came down at and how
        /// hard it landed (0..1). The detection here is the only place in the project that knows what a
        /// step is — the rising edge of a real contact for a physics athlete, distance covered for the
        /// kinematic bot — so anything else that wants to react to a footfall listens rather than working
        /// it out a second time. <c>FootstepDust</c> is the one that does.
        /// </summary>
        public event Action<Vector3, float> Stepped;

        AudioSource _src;
        AudioSource _breath;
        bool[] _down;
        float[] _lastStep;
        int _cycle;
        float _sinceStride;
        AudioBank.Surface _surface = AudioBank.Surface.Asphalt;
        float _nextSurfaceCheck;

        void Awake()
        {
            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.spatialBlend = 1f;
            _src.rolloffMode = AudioRolloffMode.Linear;
            _src.minDistance = 3f;
            _src.maxDistance = maxDistance;
            _src.dopplerLevel = 0f;
            _src.priority = 200;   // a field of sixteen must never push the crowd or the gun out of the mix

            int feet = rig != null && rig.feet != null ? rig.feet.Length : 0;
            _down = new bool[feet];
            _lastStep = new float[feet];

            if (effort == null) effort = GetComponent<EffortMeter>();

            if (!breathe || bank == null || bank.breath == null) return;
            _breath = gameObject.AddComponent<AudioSource>();
            _breath.clip = bank.breath;
            _breath.loop = true;
            _breath.playOnAwake = false;
            _breath.spatialBlend = 1f;
            _breath.rolloffMode = AudioRolloffMode.Linear;
            _breath.minDistance = 2f;
            _breath.maxDistance = breathDistance;
            _breath.dopplerLevel = 0f;
            _breath.volume = 0f;
            _breath.priority = 210;   // the first thing to be culled when the mix runs out of voices
            _breath.Play();
        }

        void Update()
        {
            if (_src != null) _src.dopplerLevel = doppler * AudioMix.DopplerScale;
            if (_breath == null) return;
            float speed = rig != null ? rig.BaseLinearVelocityWorld.magnitude
                        : heuristic != null ? heuristic.Speed : 0f;
            float work = Mathf.Clamp01(speed / Mathf.Max(1f, breathReferenceSpeed));

            // Speed alone gets this wrong in both directions: an athlete fighting to hold a line through a
            // bend is working far harder than its speed suggests, and one that has just been picked up off
            // the deck is barely moving while every joint in it is at the limit. Where an EffortMeter
            // exists, the torque reading is the better half of the answer and the speed is the sanity
            // check, so the two are taken together rather than one replacing the other.
            if (effort != null)
            {
                float strain = Mathf.Clamp01(effort.Effort / 0.5f);
                work = Mathf.Max(work, strain);
                // Fatigue only lifts the floor. It cannot make a standing athlete pant, but it does stop a
                // tired one sounding fresh the moment it eases off.
                work = Mathf.Max(work, effort.Fatigue * 0.55f * Mathf.Clamp01(speed / 1.5f));
            }

            // Faster and louder with effort. The pitch is what carries it: the same clip at 1.3x is not a
            // louder breath, it is a shorter one, which is what working hard actually sounds like.
            _breath.pitch = Mathf.Lerp(0.8f, 1.35f, work);
            _breath.volume = work * work * breathVolume * AudioMix.Level(AudioMix.Bus.Sfx);
        }

        /// <summary>
        /// What is underfoot. Read off the renderer of whatever the athlete is standing on rather than
        /// tracked by the event, so an athlete that has wandered off the runway onto the infield deck is
        /// heard to have done it. Sampled a few times a second, not per step: the ground does not change
        /// between one stride and the next.
        /// </summary>
        AudioBank.Surface Ground(Vector3 at)
        {
            if (Time.time < _nextSurfaceCheck) return _surface;
            _nextSurfaceCheck = Time.time + 0.35f;

            if (!Physics.Raycast(at + Vector3.up * 0.6f, Vector3.down, out RaycastHit hit, 2.5f, ~0, QueryTriggerInteraction.Ignore))
                return _surface;
            var mr = hit.collider.GetComponent<MeshRenderer>();
            string name = mr != null && mr.sharedMaterial != null ? mr.sharedMaterial.name : string.Empty;
            if (name.Contains("Sand")) _surface = AudioBank.Surface.Sand;
            else if (name.Contains("Runway") || name.Contains("Infield")) _surface = AudioBank.Surface.Rubber;
            else _surface = AudioBank.Surface.Asphalt;
            return _surface;
        }

        void FixedUpdate()
        {
            if (_src == null) return;
            if (bank == null || !bank.HasFootfalls) { if (Stepped == null) return; }   // still detect steps for the dust
            if (rig != null) PhysicsSteps();
            else if (heuristic != null) KinematicSteps();
        }

        void PhysicsSteps()
        {
            FootContactSensor[] feet = rig.feet;
            if (feet == null) return;
            for (int i = 0; i < feet.Length && i < _down.Length; i++)
            {
                FootContactSensor f = feet[i];
                if (f == null) continue;
                bool now = f.InContact;
                if (now && !_down[i] && f.NormalForce >= minForce && Time.fixedTime - _lastStep[i] >= refractorySeconds)
                {
                    _lastStep[i] = Time.fixedTime;
                    Step(Mathf.Clamp01(f.NormalForce / Mathf.Max(1f, referenceForce)), f.transform.position);
                }
                _down[i] = now;
            }
        }

        void KinematicSteps()
        {
            if (!heuristic.Running) { _sinceStride = 0f; return; }
            _sinceStride += heuristic.Speed * Time.fixedDeltaTime;
            if (_sinceStride < strideMetres) return;
            _sinceStride -= strideMetres;
            float top = Mathf.Max(1f, heuristic.topSpeed);
            Step(Mathf.Clamp01(0.4f + 0.6f * heuristic.Speed / top), heuristic.transform.position);
        }

        /// <summary>
        /// Plays one step. The clip rotates and the pitch wanders, because sixteen athletes firing the same
        /// sample at the same pitch stops sounding like a field and starts sounding like a machine.
        /// </summary>
        void Step(float weight, Vector3 at)
        {
            Stepped?.Invoke(at, weight);
            if (bank == null || !bank.HasFootfalls) return;   // detection still runs for the listeners above
            AudioClip clip = bank.Footfall(_cycle++, Ground(at));
            if (clip == null) return;
            _src.pitch = UnityEngine.Random.Range(0.9f, 1.12f);
            _src.PlayOneShot(clip, Mathf.Clamp01(0.35f + 0.65f * weight) * volume * AudioMix.Level(AudioMix.Bus.Sfx));
        }
    }
}
