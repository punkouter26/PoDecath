using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// 100 m dash on the rooftop straight. Zero-interaction: countdown, race, result, auto-restart.
    /// RL athletes are driven by their run-to-target policy toward a point past the finish line;
    /// a fall marks DNF (get-up policy is a placeholder). Heuristic athletes run a kinematic pace profile.
    /// Exposes the reference (first RL) athlete's stats for the HUD.
    /// </summary>
    [DefaultExecutionOrder(-30)]
    public class DashEvent : MonoBehaviour
    {
        public enum Phase { Idle, Countdown, Running, Finished }

        [Serializable]
        public class Athlete
        {
            public string name;
            public AthleteKind kind;
            public Color color;
            public GameObject go;
            public CreatureRig rig;
            public PolicyRunner runner;
            public VelocityCommandSource command;
            public HeuristicRunner heuristic;
            public TrackFollower follower;
            public float spawnHeight = 0.95f;
            public int lane;
            /// <summary>Unique 1-based suffix shown in the results, so eight copies of one policy stay tellable apart.</summary>
            public int number;
            [NonSerialized] public bool stopping;
            [NonSerialized] public float stopTime;
            [NonSerialized] public Vector3 stopFrom, stopTo;
            [NonSerialized] public Quaternion stopFromRot, stopToRot;
            [NonSerialized] public float time;
            [NonSerialized] public float distance;
            [NonSerialized] public float speed;
            [NonSerialized] public bool finished;
            [NonSerialized] public bool fell;
            [NonSerialized] public float fellAt;
            // Balance sampling (RL only, one sample per physics step while running).
            [NonSerialized] public float minUpright;
            [NonSerialized] public float minHeightFrac;
            [NonSerialized] public float uprightSum;
            [NonSerialized] public int uprightSamples;
            public float MeanUpright => uprightSamples > 0 ? uprightSum / uprightSamples : 0f;
            public bool IsRL => rig != null;
        }

        [Header("Course")]
        public Vector3 startLine = Vector3.zero;
        public Vector3 direction = Vector3.right;
        public float raceDistance = 100f;
        public float laneSpacing = 1.6f;
        [Tooltip("Lanes abreast. The deck is 5.3 m wide and the lane pitch is solved for five across it, so a "
               + "bigger field goes into further rows rather than off the roof.")]
        public int maxLanes = 5;
        [Tooltip("Gap along the course between grid rows. Rows are set forward from the line like the lap "
               + "grid, and every runner covers the full distance from its own mark, so nobody loses anything.")]
        public float rowSpacing = 1.5f;

        [Header("Timing")]
        public float countdownSeconds = 3f;
        public float maxRaceSeconds = 45f;
        public float restartDelay = 4f;
        [Tooltip("Shorter pause before the automatic restart when the reference athlete fell.")]
        public float fallRestartDelay = 3f;
        public bool autoRestart = true;
        float _currentRestartDelay = 4f;

        [Header("Pull-up after the line")]
        [Tooltip("How long a finisher takes to settle from race pace onto a standing pose.")]
        public float pullUpSeconds = 0.9f;
        [Tooltip("How far a finisher coasts past the line while settling.")]
        public float pullUpCoast = 2.5f;

        [Header("Fall detection (RL)")]
        public float fallUprightDot = 0.4f;
        public float fallHeightFraction = 0.6f;

        /// <summary>
        /// One finished competitor, ranked. The dash sorts finishers by time and non-finishers by
        /// distance behind them; <see cref="DashEvent.Rank"/> and <see cref="DashEvent.StatusFor"/>
        /// let an event that is not a timed race order and label its own board.
        /// </summary>
        public readonly struct RaceResult
        {
            public readonly int rank, number;
            public readonly string name;
            public readonly AthleteKind kind;
            public readonly Color color;
            public readonly float time, distance;
            public readonly bool finished, fell;
            readonly string status;

            public RaceResult(int rank, Athlete a, string status)
            {
                this.rank = rank;
                number = a.number; name = a.name; kind = a.kind; color = a.color;
                time = a.time; distance = a.distance; finished = a.finished; fell = a.fell;
                this.status = status;
            }

            public string Status => status;
        }

        public List<Athlete> Athletes { get; } = new List<Athlete>();
        /// <summary>Ranked results of the last completed race.</summary>
        public List<RaceResult> Results { get; } = new List<RaceResult>();
        /// <summary>Fires once every runner has finished or fallen — the cue for the results modal.</summary>
        public event Action<List<RaceResult>> RaceComplete;
        public Phase Current { get; protected set; } = Phase.Idle;
        public float RaceTime { get; protected set; }
        public float Countdown { get; protected set; }
        public int Attempt { get; protected set; }
        public string LastResult { get; protected set; } = "-";
        public event Action<string> RaceFinished;

        // HUD-compatible view of the reference athlete
        public Athlete Reference { get; protected set; }
        public float Speed => Reference != null ? Reference.speed : 0f;
        public float Distance => Reference != null ? Reference.distance : 0f;
        public float Stability { get; protected set; } = 1f;
        public string Status => Current switch
        {
            Phase.Countdown => $"Ready  {Mathf.CeilToInt(Countdown)}",
            Phase.Running => "GO",
            Phase.Finished => "Finished",
            _ => "Idle",
        };

        protected float _phaseTimer;
        readonly List<Athlete> _ranking = new List<Athlete>();
        Vector3 Lateral => Vector3.Cross(Vector3.up, direction.normalized) * -1f;   // +Z side for +X direction

        public void Register(Athlete a)
        {
            a.lane = Athletes.Count;
            Athletes.Add(a);
            // The reference athlete (camera + HUD) is the first RL athlete; fall back to whoever registered first.
            if (a.IsRL && (Reference == null || !Reference.IsRL)) Reference = a;
            else if (Reference == null) Reference = a;
        }

        int Lanes => Mathf.Max(1, maxLanes);
        int Rows => Mathf.Max(1, Mathf.CeilToInt(Athletes.Count / (float)Lanes));

        /// <summary>Lateral offset of a grid slot; each row is centred on the deck for however many it holds.</summary>
        protected float LaneOffset(int lane)
        {
            int row = lane / Lanes;
            int inRow = Mathf.Min(Lanes, Athletes.Count - row * Lanes);
            return ((lane % Lanes) - (inRow - 1) * 0.5f) * laneSpacing;
        }

        /// <summary>How far along the course this slot's mark is: front row furthest, last row on the line.</summary>
        protected float RowAdvance(int lane) => (Rows - 1 - lane / Lanes) * rowSpacing;

        public Vector3 LanePosition(int lane) =>
            startLine + direction.normalized * RowAdvance(lane) + Lateral * LaneOffset(lane);

        public Quaternion FacingRotation => Quaternion.FromToRotation(Vector3.right, new Vector3(direction.x, 0f, direction.z).normalized);

        // ---- course hooks (LapEvent overrides these to run the loop instead of the straight) ----
        protected virtual Vector3 SpawnPosition(Athlete a) => LanePosition(a.lane);
        protected virtual Quaternion SpawnRotation(Athlete a) => FacingRotation;
        protected virtual void SetCourseTarget(Athlete a)
        {
            a.command?.SetTarget(LanePosition(a.lane) + direction.normalized * (raceDistance + 12f));
        }
        /// <summary>Distance from the runner's own mark, so a staggered grid costs nobody anything.</summary>
        protected virtual float MeasureDistance(Athlete a, Vector3 pos) => Vector3.Dot(pos - LanePosition(a.lane), direction.normalized);
        protected virtual float FloorY(Athlete a) => LanePosition(a.lane).y;

        public virtual void StartRace()
        {
            if (Athletes.Count == 0) { Current = Phase.Idle; return; }
            foreach (var a in Athletes) ResetAthlete(a);
            RaceTime = 0f;
            Countdown = countdownSeconds;
            Current = Phase.Countdown;
            Stability = 1f;
            _phaseTimer = 0f;
        }

        public void RestartNow() => StartRace();

        protected virtual void ResetAthlete(Athlete a)
        {
            a.time = 0f; a.distance = 0f; a.speed = 0f; a.finished = false; a.fell = false; a.fellAt = 0f;
            a.minUpright = 1f; a.minHeightFrac = 1f; a.uprightSum = 0f; a.uprightSamples = 0;
            a.stopping = false;
            if (a.follower != null) a.follower.enabled = true;
            if (a.IsRL && a.rig != null && a.rig.root != null) a.rig.root.immovable = false;
            Vector3 p = SpawnPosition(a);
            Quaternion rot = SpawnRotation(a);
            if (a.IsRL)
            {
                if (a.runner != null) a.runner.enabled = false;
                if (a.runner != null) a.runner.ResetEpisode(p + Vector3.up * a.spawnHeight, rot);
                else a.rig.ResetPose(p + Vector3.up * a.spawnHeight, rot);
                SetCourseTarget(a);
            }
            else if (a.heuristic != null)
            {
                ResetHeuristic(a, p);
            }
        }

        protected virtual void ResetHeuristic(Athlete a, Vector3 p) => a.heuristic.ResetTo(p, direction);

        protected virtual void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            switch (Current)
            {
                case Phase.Countdown:
                    Countdown -= dt;
                    // hold RL athletes upright on the line until the gun
                    foreach (var a in Athletes)
                        if (a.IsRL) a.rig.ResetPose(SpawnPosition(a) + Vector3.up * a.spawnHeight, SpawnRotation(a));
                    if (Countdown <= 0f) Go();
                    break;
                case Phase.Running:
                    RaceTime += dt;
                    bool allDone = true;
                    foreach (var a in Athletes)
                    {
                        if (a.stopping) HoldStopped(a);
                        UpdateAthlete(a, dt);
                        if (!a.finished && !a.fell) allDone = false;
                    }
                    if (Reference != null && Reference.IsRL)
                        Stability = Mathf.Lerp(Stability, Mathf.Clamp01((Reference.rig.UprightDot - fallUprightDot) / (1f - fallUprightDot)), 0.05f);
                    if (allDone || RaceTime >= maxRaceSeconds) Finish();
                    break;
                case Phase.Finished:
                    // Whoever crossed last is still mid-settle when the race ends; without this they are
                    // left to topple under the results card.
                    foreach (var a in Athletes) if (a.stopping) HoldStopped(a);
                    _phaseTimer += dt;
                    if (autoRestart && _phaseTimer >= _currentRestartDelay) StartRace();
                    break;
            }
        }

        protected virtual void Go()
        {
            Current = Phase.Running;
            RaceTime = 0f;
            foreach (var a in Athletes)
            {
                if (a.IsRL && a.runner != null) a.runner.enabled = true;
                a.heuristic?.Go();
            }
        }

        protected virtual void UpdateAthlete(Athlete a, float dt)
        {
            if (a.finished || a.fell) return;
            Vector3 pos = a.IsRL ? a.rig.BasePosition : a.go.transform.position;
            a.distance = MeasureDistance(a, pos);
            a.speed = a.IsRL ? new Vector3(a.rig.BaseLinearVelocityWorld.x, 0f, a.rig.BaseLinearVelocityWorld.z).magnitude : (a.heuristic != null ? a.heuristic.Speed : 0f);
            a.time = RaceTime;
            if (a.IsRL && DetectFall(a, pos)) return;
            if (a.distance >= raceDistance)
            {
                a.finished = true;
                StopAthlete(a);
            }
        }

        /// <summary>
        /// Samples uprightness and height for one physics athlete and reports whether it has gone down.
        /// A faller stops being driven, because the get-up policy is still a placeholder.
        /// </summary>
        protected bool DetectFall(Athlete a, Vector3 pos)
        {
            if (!a.IsRL || a.rig == null) return false;
            float heightFrac = (pos.y - FloorY(a)) / Mathf.Max(1e-4f, a.spawnHeight);
            float upright = a.rig.UprightDot;
            a.minUpright = Mathf.Min(a.minUpright, upright);
            a.minHeightFrac = Mathf.Min(a.minHeightFrac, heightFrac);
            a.uprightSum += upright; a.uprightSamples++;
            if (upright >= fallUprightDot && heightFrac >= fallHeightFraction) return false;
            a.fell = true; a.fellAt = a.distance;
            if (a.runner != null) a.runner.enabled = false;   // placeholder: get-up policy not trained yet
            return true;
        }

        /// <summary>Result line the live HUD shows once a whole field has been ranked.</summary>
        public virtual string HudResultLine(List<RaceResult> results)
        {
            RaceResult top = results[0];
            int finishers = 0;
            foreach (RaceResult r in results) if (r.finished) finishers++;
            return top.finished
                ? $"Won by {top.name}  {top.time:F2} s   ({finishers}/{results.Count} finished)"
                : $"No finishers ({results.Count} started)";
        }

        /// <summary>
        /// A runner that has crossed the line pulls up rather than carrying on round. The policy stays in
        /// charge — it is a run-to-target network, so parking the target on its own body asks it for zero
        /// travel, which it answers by decelerating and standing. The carrot follower is switched off so
        /// nothing drags it forward again.
        /// </summary>
        protected virtual void StopAthlete(Athlete a)
        {
            a.stopping = true;
            a.stopTime = Time.fixedTime;
            if (a.follower != null) a.follower.enabled = false;
            a.heuristic?.Stop();
            if (!a.IsRL || a.rig == null) return;
            if (a.runner != null) a.runner.enabled = false;   // the pull-up drives the rig from here

            a.stopFrom = a.rig.BasePosition;
            a.stopFromRot = a.rig.BaseRotation;
            Vector3 fwd = a.rig.BaseForward; fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.right;
            a.stopTo = a.stopFrom + fwd * pullUpCoast;
            a.stopTo.y = FloorY(a) + a.spawnHeight;
            a.stopToRot = Quaternion.FromToRotation(Vector3.right, fwd);
        }

        /// <summary>
        /// Settles a finisher onto a standing pose just past the line, then pins it there.
        ///
        /// This is animated, not simulated, and deliberately so. Both policies are run-to-target networks
        /// with no stand-still behaviour anywhere in their training, so a finisher left under policy
        /// control folds up within about a third of a second; braking the root instead is no better,
        /// because nothing is left holding the body upright. Measured over a field of four, policy-driven
        /// finishers ended at an uprightness of -0.05 to 0.67 -- face down to badly slumped. Until a
        /// stand/get-up policy exists, driving the settle by hand is the only way a finisher stays on its
        /// feet, so the runner coasts <see cref="pullUpCoast"/> metres, straightens, and holds.
        /// </summary>
        protected virtual void HoldStopped(Athlete a)
        {
            if (!a.IsRL || a.rig == null || a.rig.root == null) return;
            ArticulationBody root = a.rig.root;
            if (root.immovable) return;

            float t = pullUpSeconds > 0f ? Mathf.Clamp01((Time.fixedTime - a.stopTime) / pullUpSeconds) : 1f;
            float ease = t * t * (3f - 2f * t);
            if (t < 1f)
            {
                a.rig.ResetPose(Vector3.Lerp(a.stopFrom, a.stopTo, ease), Quaternion.Slerp(a.stopFromRot, a.stopToRot, ease));
                return;
            }
            a.rig.ResetPose(a.stopTo, a.stopToRot);
            root.immovable = true;
        }

        // ---- presentation hooks (the HUD and the results modal read the event through these) ----

        /// <summary>House tag for the log line: which of the three roster kinds this athlete is.</summary>
        protected static string Tag(Athlete a) =>
            a.kind == AthleteKind.Heuristic ? "RED" : a.kind == AthleteKind.ReferenceRL ? "GREEN" : "CUSTOM";

        /// <summary>Board order. Finishers first, fastest first; then whoever got furthest.</summary>
        protected virtual int Rank(Athlete x, Athlete y)
        {
            if (x.finished != y.finished) return x.finished ? -1 : 1;      // finishers first
            if (x.finished) return x.time.CompareTo(y.time);                // then fastest
            return y.distance.CompareTo(x.distance);                        // then furthest
        }

        /// <summary>Right-hand column of the results card.</summary>
        protected virtual string StatusFor(Athlete a) =>
            a.finished ? $"{a.time:F2} s" : (a.fell ? $"DNF  fell at {a.distance:F0} m" : $"DNF  {a.distance:F0} m");

        /// <summary>One athlete's slice of the console summary line.</summary>
        protected virtual string SummaryFor(Athlete a)
        {
            if (a.finished) return $"{Tag(a)} {a.name} {a.time:F2}s";
            if (a.fell) return $"{Tag(a)} {a.name} fell at {a.fellAt:F0} m";
            return $"{Tag(a)} {a.name} DNF {a.distance:F0} m";
        }

        /// <summary>Sub-heading under RESULTS.</summary>
        public virtual string ResultsSubtitle(List<RaceResult> results)
        {
            int finishers = 0;
            foreach (RaceResult r in results) if (r.finished) finishers++;
            return $"{finishers} of {results.Count} completed the {raceDistance:F0} m lap";
        }

        /// <summary>Middle line of the live status card.</summary>
        public virtual string HudDistanceLine()
        {
            Athlete r = Reference;
            string time = r != null && r.finished ? $"Finish  {r.time:F2} s" : $"Time  {RaceTime:F2} s";
            return $"Distance  {Distance:F1} / {raceDistance:F0} m   {time}";
        }

        protected virtual void BuildResults()
        {
            _ranking.Clear();
            _ranking.AddRange(Athletes);
            _ranking.Sort(Rank);
            Results.Clear();
            for (int i = 0; i < _ranking.Count; i++) Results.Add(new RaceResult(i + 1, _ranking[i], StatusFor(_ranking[i])));
        }

        /// <summary>
        /// Announces a completed event. Both completion events live on this class, so a derived event
        /// that ends its own way (the long jump ends on the board, not on a clock) announces through here.
        /// </summary>
        protected void RaiseFinished()
        {
            RaceFinished?.Invoke(LastResult);
            RaceComplete?.Invoke(Results);
        }

        protected virtual void Finish()
        {
            Current = Phase.Finished;
            _phaseTimer = 0f;
            // A runner still upright when the clock ran out pulls up too, so nothing keeps running under the modal.
            foreach (var a in Athletes) if (!a.fell && !a.stopping) StopAthlete(a);
            _currentRestartDelay = Reference != null && Reference.fell ? fallRestartDelay : restartDelay;
            var sb = new StringBuilder();
            foreach (var a in Athletes)
            {
                if (sb.Length > 0) sb.Append("  |  ");
                sb.Append(SummaryFor(a));
                if (a.IsRL) sb.Append($" [up min {a.minUpright:F3} avg {a.MeanUpright:F3} h {a.minHeightFrac:F2}]");
            }
            LastResult = sb.ToString();
            Attempt++;
            Debug.Log($"[{GetType().Name}] attempt {Attempt}: {LastResult}");
            BuildResults();
            RaiseFinished();
        }
    }
}
