using System;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Gets one athlete back on its feet.
    ///
    /// Until now a fall was the end of a runner's race: <see cref="RaceEvent.DetectFall"/> marked the DNF
    /// and switched the policy off, because there was no policy that could do anything about being on the
    /// ground. There is one now (<c>athlete_getup.onnx</c>, trained by <c>training/envs/get_up.py</c>), and
    /// this is the piece that decides when to hand control to it and when to hand control back.
    ///
    /// The whole thing is a four-state machine and the states are the honest ones:
    ///
    ///   Running    — upright and racing. Nothing to do.
    ///   Recovering — down, with the get-up policy driving and a zero command.
    ///   Settling   — back on its feet, but not yet trusted. It has to stay up for a moment before racing
    ///                again, because a policy that stands and immediately topples has not recovered, and
    ///                handing the running policy a body that is still falling just wastes the recovery.
    ///   Spent      — it tried for <see cref="giveUpSeconds"/> and is still down. This is a DNF, the same
    ///                as before. A recovery attempt that never ends is worse than a fall, because the race
    ///                cannot finish and the results card never comes up.
    ///
    /// The thresholds deliberately are not symmetric. Going down is judged by the event's own numbers so
    /// the HUD and the broadcast agree about who is down; standing back up is judged more strictly, so an
    /// athlete propped on one elbow at the exact fall threshold does not flicker between the two policies.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class RecoveryController : MonoBehaviour
    {
        public enum State { Running, Recovering, Settling, Spent }

        [Header("Wiring")]
        public AthleteRig rig;
        public PolicyRunner runner;

        [Header("Down")]
        [Tooltip("Uprightness below this counts as down. Matches RaceEvent.fallUprightDot so that the "
               + "overlay, the audio and this component never disagree about who is on the deck.")]
        public float downUprightDot = 0.4f;
        [Tooltip("Height as a fraction of spawn height below which the athlete counts as down.")]
        public float downHeightFraction = 0.6f;

        [Header("Up again")]
        [Tooltip("Uprightness needed to count as standing. Well above the down threshold on purpose: the "
               + "gap is what stops an athlete on the edge flickering between the two policies.")]
        public float upUprightDot = 0.82f;
        public float upHeightFraction = 0.8f;
        [Tooltip("How long it has to hold that before the running policy gets the body back. The get-up "
               + "policy was trained with a one-second hold as its objective, so this is the same bargain.")]
        public float settleSeconds = 0.8f;

        [Header("Giving up")]
        [Tooltip("Longest a single recovery may take before the athlete is written off as a DNF. Without a "
               + "limit a body wedged against a barrier keeps the whole race from ever finishing.")]
        public float giveUpSeconds = 8f;
        [Tooltip("Recoveries allowed per attempt. A runner that goes down five times is not having a race, "
               + "and each recovery costs it far more time than the fall did.")]
        public int maxRecoveries = 3;

        /// <summary>Where in the machine this athlete is.</summary>
        public State Current { get; private set; } = State.Running;

        /// <summary>How many times it has got itself up this attempt. Shown on the results board.</summary>
        public int Recoveries { get; private set; }

        /// <summary>Seconds spent on the ground this attempt, which is time lost to the field.</summary>
        public float TimeDown { get; private set; }

        /// <summary>Raised when an athlete gets itself back up, so the crowd and the overlay can react.</summary>
        public event Action<RecoveryController> Recovered;

        /// <summary>Raised when it stops trying. The event turns this into a DNF.</summary>
        public event Action<RecoveryController> GaveUp;

        /// <summary>True while the get-up policy is driving; the event leaves such an athlete racing.</summary>
        public bool Busy => Current == State.Recovering || Current == State.Settling;

        /// <summary>Whether there is anything to try with at all.</summary>
        public bool Available => runner != null && runner.HasRecoveryModel && Recoveries < maxRecoveries;

        float _spawnHeight = 0.95f;
        int _groundMask;
        float _attemptTime;
        float _settleTime;

        /// <summary>Called by the event as it registers the athlete, so the height test has a reference.</summary>
        public void Configure(float spawnHeight)
        {
            _spawnHeight = Mathf.Max(0.05f, spawnHeight);
            string layerName = runner != null && !string.IsNullOrEmpty(runner.creatureLayerName)
                ? runner.creatureLayerName : "Creature";
            int creature = LayerMask.NameToLayer(layerName);
            _groundMask = creature >= 0 ? ~(1 << creature) : ~0;
        }

        /// <summary>Back to racing, for a restart. Does not clear the recovery count's history in the results.</summary>
        public void ResetForAttempt()
        {
            Current = State.Running;
            Recoveries = 0;
            TimeDown = 0f;
            _attemptTime = 0f;
            _settleTime = 0f;
            if (runner != null) runner.UseRecovery = false;
        }

        /// <summary>
        /// Asks this athlete to try to get up. Returns false if it cannot — no policy loaded, or it has
        /// already used its attempts — in which case the caller books the DNF exactly as it always did.
        /// </summary>
        public bool TryRecover()
        {
            if (!Available || Busy) return Busy;
            Current = State.Recovering;
            _attemptTime = 0f;
            _settleTime = 0f;
            runner.UseRecovery = true;
            runner.enabled = true;   // the event switches fallen runners off; this is what switches one back on
            return true;
        }

        void Update()
        {
            if (!Busy || rig == null || runner == null) return;

            float dt = Time.deltaTime;
            _attemptTime += dt;
            TimeDown += dt;

            float upright = rig.UprightDot;
            float heightFrac = (rig.BasePosition.y - FloorY()) / _spawnHeight;
            bool standing = upright >= upUprightDot && heightFrac >= upHeightFraction;

            if (Current == State.Recovering)
            {
                if (standing)
                {
                    Current = State.Settling;
                    _settleTime = 0f;
                }
                else if (_attemptTime >= giveUpSeconds)
                {
                    GiveUp();
                }
                return;
            }

            // Settling: hold it, and drop straight back to recovering if it starts going over again rather
            // than handing a toppling body to a policy that has only ever been given an upright one.
            if (!standing)
            {
                Current = State.Recovering;
                _settleTime = 0f;
                if (_attemptTime >= giveUpSeconds) GiveUp();
                return;
            }

            _settleTime += dt;
            if (_settleTime < settleSeconds) return;

            Current = State.Running;
            Recoveries++;
            runner.UseRecovery = false;
            Recovered?.Invoke(this);
        }

        void GiveUp()
        {
            Current = State.Spent;
            runner.UseRecovery = false;
            runner.enabled = false;
            GaveUp?.Invoke(this);
        }

        /// <summary>
        /// The deck under this athlete. A rooftop track is not at y = 0 and the infield deck is at a
        /// different height again, so the height test has to be measured against whatever is actually below.
        /// </summary>
        /// <summary>
        /// World Y of the deck under the athlete.
        ///
        /// The ray must not see the athlete. Cast with every layer enabled it hits the body's own torso
        /// about a quarter of a metre above the pelvis -- the ray starts above the pelvis and the chest
        /// is the first thing below it -- so the "floor" came out *above* the base and heightFrac in
        /// <see cref="Update"/> went negative. That made `standing` unsatisfiable, and `standing` is the
        /// only way out of Recovering: every athlete that got itself up was still driven to
        /// giveUpSeconds and booked as a DNF. The policy was doing its job and nothing was watching.
        /// </summary>
        float FloorY()
        {
            Vector3 p = rig.BasePosition;
            int mask = _groundMask != 0 ? _groundMask : ~0;
            return Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 8f, mask,
                                   QueryTriggerInteraction.Ignore)
                ? hit.point.y
                : 0f;
        }
    }
}
