using System.Collections.Generic;
using UnityEngine;
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
        public DashEvent race;
        [Tooltip("Set for the long jump; the landing burst comes off the pit's own measurement.")]
        public LongJumpPit pit;

        [Header("Sizes")]
        [Tooltip("How hard a fall throws dust up. A fall at speed is worth more than a stumble.")]
        public float fallSpeedReference = 7f;

        DashEvent.Phase _lastPhase = DashEvent.Phase.Idle;
        LongJumpEvent.Stage _lastStage;
        int _lastAttempt = -1;
        readonly HashSet<DashEvent.Athlete> _fallen = new HashSet<DashEvent.Athlete>();
        readonly HashSet<DashEvent.Athlete> _finished = new HashSet<DashEvent.Athlete>();
        readonly Dictionary<DashEvent.Athlete, int> _recoveries = new Dictionary<DashEvent.Athlete, int>();
        readonly List<Hurdle> _hurdles = new List<Hurdle>();

        LongJumpEvent Jump => race as LongJumpEvent;

        void Start() => HookHurdles();

        void OnDestroy()
        {
            foreach (Hurdle h in _hurdles) if (h != null) h.KnockedOver -= OnHurdleKnocked;
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
            DashEvent.Phase phase = race.Current;
            if (phase == DashEvent.Phase.Running && _lastPhase == DashEvent.Phase.Countdown)
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
            foreach (DashEvent.Athlete a in race.Athletes)
            {
                if (a.fell && _fallen.Add(a))
                {
                    float force = Mathf.Clamp01(a.speed / Mathf.Max(1f, fallSpeedReference));
                    VfxLibrary.Play(VfxLibrary.Effect.FallDust, Ground(a), Vector3.up, 0.5f + force);
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
                DashEvent.Athlete who = jump.Competitor;
                Vector3 at = who != null ? Ground(who) : pit.SandPoint(pit.pitNearX + 1f);
                // Sand goes the way the jumper was travelling, which is what makes the landing read as an
                // arrival rather than an explosion.
                VfxLibrary.Play(VfxLibrary.Effect.SandBurst, at, pit.Direction + Vector3.up * 0.8f, 1f);
            }
            _lastStage = stage;
        }

        void OnHurdleKnocked(Hurdle h)
        {
            if (h == null) return;
            VfxLibrary.Play(VfxLibrary.Effect.HurdleScuff, h.transform.position, h.transform.forward + Vector3.up * 0.4f, 1f);
        }

        /// <summary>Where an athlete meets the deck, which is where anything it kicks up starts.</summary>
        static Vector3 Ground(DashEvent.Athlete a)
        {
            Vector3 p = a.IsRL ? a.rig.BasePosition : (a.go != null ? a.go.transform.position : Vector3.zero);
            if (Physics.Raycast(p + Vector3.up * 1.2f, Vector3.down, out RaycastHit hit, 4f, ~0, QueryTriggerInteraction.Ignore))
                return hit.point;
            return p;
        }
    }
}
