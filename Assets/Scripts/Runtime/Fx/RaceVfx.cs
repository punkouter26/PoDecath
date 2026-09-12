using System.Collections.Generic;
using UnityEngine;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// Turns what the event already knows into things you can see: smoke off the pistol, sand out of the
    /// pit, dust where somebody goes down, sparks off a knocked hurdle, confetti over the line.
    ///
    /// None of these are invented triggers. The race publishes a phase, every athlete carries
    /// <c>fell</c> and <c>finished</c>, <see cref="LongJumpEvent"/> publishes a stage and
    /// <see cref="Hurdle"/> raises an event when it goes over — this watches those and fires the
    /// matching effect, in the same shape <c>RaceAudio</c> already uses for the sound. Keeping the two
    /// separate rather than firing both from one place is deliberate: the mix and the picture are tuned by
    /// different senses and get turned down independently.
    /// </summary>
    [DefaultExecutionOrder(125)]
    public class RaceVfx : MonoBehaviour
    {
        [Header("Wiring")]
        public RaceEvent race;
        [Tooltip("Set for the long jump; the landing burst comes off the pit's own measurement.")]
        public LongJumpPit pit;

        [Header("Sizes")]
        [Tooltip("How hard a fall throws dust up. A fall at speed is worth more than a stumble.")]
        public float fallSpeedReference = 7f;

        [Header("Impact")]
        [Tooltip("Contact impulse, in N s, that scales an effect to full. A hurdle knocked clean over by a "
               + "runner at 4 m/s lands around here; a shin brushing the bar is a tenth of it. Measured "
               + "off Hurdle.LastImpulse rather than assumed, so re-tune it against what the console logs "
               + "rather than against taste.")]
        public float referenceImpulse = 45f;
        [Tooltip("Camera shake on a hurdle going over, at the reference impulse.")]
        [Range(0f, 1f)] public float knockShake = 0.75f;
        [Tooltip("Camera shake on an athlete hitting the deck at fallSpeedReference.")]
        [Range(0f, 1f)] public float fallShake = 0.55f;
        [Tooltip("Camera shake on a landing in the sand.")]
        [Range(0f, 1f)] public float landingShake = 0.45f;

        RaceEvent.Phase _lastPhase = RaceEvent.Phase.Idle;
        LongJumpEvent.Stage _lastStage;
        int _lastAttempt = -1;
        readonly HashSet<RaceEvent.Athlete> _fallen = new HashSet<RaceEvent.Athlete>();
        readonly HashSet<RaceEvent.Athlete> _finished = new HashSet<RaceEvent.Athlete>();
        readonly Dictionary<RaceEvent.Athlete, int> _recoveries = new Dictionary<RaceEvent.Athlete, int>();
        readonly List<Hurdle> _hurdles = new List<Hurdle>();

        LongJumpEvent Jump => race as LongJumpEvent;

        void Start() => HookHurdles();

        void OnDestroy()
        {
            foreach (Hurdle h in _hurdles)
            {
                if (h == null) continue;
                h.KnockedOver -= OnHurdleKnocked;
                h.Struck -= OnHurdleStruck;
            }
        }

        /// <summary>
        /// Hurdles are built by <see cref="HurdleSet"/> after the scene loads, so they are collected in
        /// Start rather than wired by the builder. They are stood back up between attempts rather than
        /// rebuilt, so this only has to happen once.
        /// </summary>
        void HookHurdles()
        {
            _hurdles.Clear();
            foreach (Hurdle h in FindObjectsByType<Hurdle>(FindObjectsSortMode.None))
            {
                _hurdles.Add(h);
                h.KnockedOver += OnHurdleKnocked;
                h.Struck += OnHurdleStruck;
            }
        }

        void Update()
        {
            if (race == null) return;

            if (race.Attempt != _lastAttempt)
            {
                _lastAttempt = race.Attempt;
                _fallen.Clear();
                _finished.Clear();
                _recoveries.Clear();
                if (_hurdles.Count == 0) HookHurdles();   // a set built late still gets its sparks
            }

            Phases();
            Athletes();
            JumpStages();
        }

        void Phases()
        {
            RaceEvent.Phase phase = race.Current;
            if (phase == RaceEvent.Phase.Running && _lastPhase == RaceEvent.Phase.Countdown)
            {
                // The starter stands behind and above the grid; the smoke has to come from where the
                // report does or it reads as a puff of dust off the deck.
                Vector3 at = race.startLine - race.direction.normalized * 3f + Vector3.up * 1.8f;
                VfxLibrary.Play(VfxLibrary.Effect.GunSmoke, at, Vector3.up, 1f);
            }
            _lastPhase = phase;
        }

        void Athletes()
        {
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (a.fell && _fallen.Add(a))
                {
                    float force = Mathf.Clamp01(a.speed / Mathf.Max(1f, fallSpeedReference));
                    Vector3 at = Ground(a);
                    VfxLibrary.Play(VfxLibrary.Effect.FallDust, at, Vector3.up, 0.5f + force);
                    // A runner going down at 4 m/s and one stumbling to a stop are the same event to the
                    // race and very different events to watch; the speed it was carrying is what separates
                    // them, and it is the one number the athlete already has.
                    CameraShake.Shake(at, fallShake * (0.3f + 0.7f * force));
                }
                // Back on its feet: a puff off the deck as it pushes up, which is the visual half of the
                // applause the mix plays on the same event.
                if (a.recoveries > _recoveries.GetValueOrDefault(a))
                {
                    _recoveries[a] = a.recoveries;
                    VfxLibrary.Play(VfxLibrary.Effect.FallDust, Ground(a), Vector3.up, 0.6f);
                }
                if (a.finished && _finished.Add(a))
                {
                    // Only the winner gets the full burst; a fourth-place finish gets a token one, the way
                    // a stadium's own pyrotechnics are spent on the first one home.
                    float weight = _finished.Count == 1 ? 1f : 0.35f;
                    VfxLibrary.Play(VfxLibrary.Effect.Confetti, Ground(a) + Vector3.up * 2.4f, Vector3.up, weight);
                }
            }
        }

        void JumpStages()
        {
            LongJumpEvent jump = Jump;
            if (jump == null || pit == null) return;
            LongJumpEvent.Stage stage = jump.CurrentStage;
            if (stage == _lastStage) return;

            if (stage == LongJumpEvent.Stage.Settle && _lastStage == LongJumpEvent.Stage.Flight)
            {
                RaceEvent.Athlete who = jump.Competitor;
                Vector3 at = who != null ? Ground(who) : pit.SandPoint(pit.pitNearX + 1f);
                // Sand goes the way the jumper was travelling, which is what makes the landing read as an
                // arrival rather than an explosion.
                VfxLibrary.Play(VfxLibrary.Effect.SandBurst, at, pit.Direction + Vector3.up * 0.8f, 1f);
                CameraShake.Shake(at, landingShake, Vector3.down);
            }
            _lastStage = stage;
        }

        /// <summary>
        /// A hurdle going over. The sparks and the camera are both scaled by the impulse that actually put
        /// it down, which is the difference between a runner clipping the bar hard enough to topple it and
        /// a runner going straight through it. Before this they looked identical.
        /// </summary>
        void OnHurdleKnocked(Hurdle h)
        {
            if (h == null) return;
            float weight = Weight(h.LastImpulse);
            Vector3 at = h.LastImpulse > 0f ? h.LastContact : h.transform.position;
            VfxLibrary.Play(VfxLibrary.Effect.HurdleScuff, at, h.transform.forward + Vector3.up * 0.4f, 0.5f + weight);
            CameraShake.Shake(at, knockShake * (0.45f + 0.55f * weight), h.transform.forward);
        }

        /// <summary>
        /// A hurdle clipped and left standing — the common case in a real race. A few sparks off the frame
        /// and no shake unless it was a serious hit, because the camera cannot flinch at every contact in a
        /// field of sixteen and still mean anything when one goes over.
        /// </summary>
        void OnHurdleStruck(Hurdle h, float impulse)
        {
            if (h == null || h.Knocked) return;   // a topple is reported by OnHurdleKnocked instead
            float weight = Weight(impulse);
            if (weight < 0.15f) return;
            VfxLibrary.Play(VfxLibrary.Effect.HurdleScuff, h.LastContact, h.transform.forward + Vector3.up * 0.3f, weight);
            CameraShake.Shake(h.LastContact, knockShake * weight * 0.5f, h.transform.forward);
        }

        /// <summary>A contact impulse in N s as a 0..1 fraction of the one that scales an effect to full.</summary>
        float Weight(float impulse) => Mathf.Clamp01(impulse / Mathf.Max(0.01f, referenceImpulse));

        /// <summary>Where an athlete meets the deck, which is where anything it kicks up starts.</summary>
        static Vector3 Ground(RaceEvent.Athlete a)
        {
            Vector3 p = a.IsRL ? a.rig.BasePosition : (a.go != null ? a.go.transform.position : Vector3.zero);
            if (Physics.Raycast(p + Vector3.up * 1.2f, Vector3.down, out RaycastHit hit, 4f, ~0, QueryTriggerInteraction.Ignore))
                return hit.point;
            return p;
        }
    }
}
