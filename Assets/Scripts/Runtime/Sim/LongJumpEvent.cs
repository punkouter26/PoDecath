using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Long jump on the runway and sand pit inside the rooftop loop. Competitors jump one at a time,
    /// <see cref="attemptsEach"/> rounds each, and the board is ranked on each athlete's best legal mark.
    ///
    /// How much of this is simulated: the run-up is the athlete's own run-to-target policy on a real
    /// 18 m runway, the flight is PhysX, and the landing is a real collision with the sand, measured
    /// where the body actually broke it. The one scripted moment is the take-off itself — a single
    /// upward velocity added to the base at the board, standing in for the take-off leg's extension,
    /// because no jump policy has been trained yet (the same reason RaceEvent animates its pull-up).
    /// Everything either side of that one impulse is the simulation's own answer, so the marks differ
    /// run to run exactly as the run-up speed and the stride pattern into the board differ.
    ///
    /// Measurement follows the real rule twice over: the distance is taken from the foul line whatever
    /// the athlete took off from, so arriving short of the board costs real metres; and the mark is the
    /// break in the sand *nearest* the board, so sitting back on landing costs metres too.
    /// </summary>
    public class LongJumpEvent : RaceEvent
    {
        /// <summary>Where one attempt has got to.</summary>
        public enum Stage { Waiting, Approach, Flight, Settle }

        /// <summary>One athlete's card: every attempt taken, and the best legal mark on it.</summary>
        public class Card
        {
            public Athlete athlete;
            public readonly List<float> marks = new List<float>();     // legal marks only, metres
            public readonly List<string> notes = new List<string>();   // one label per attempt taken
            public float best;
            public bool hasMark;
            public int taken;
        }

        [Header("Long jump")]
        public LongJumpPit pit;
        [Tooltip("Rounds. Every competitor takes one attempt per round, in registration order.")]
        public int attemptsEach = 3;

        [Header("Take-off")]
        [Tooltip("Vertical velocity added to the base at the board, in m/s. This is the one scripted part "
               + "of the jump and it stands in for the take-off leg. At a run-up of about 8 m/s, 3 m/s "
               + "puts the arc at roughly 6 m, which is where a decent club jumper lands.")]
        public float takeoffRise = 3.0f;
        [Tooltip("Extra forward velocity at the board. Real take-offs lose horizontal speed rather than "
               + "gain it, so this is normally 0 or slightly negative.")]
        public float takeoffDrive = 0f;
        [Tooltip("How far the lead foot reaches ahead of the base. Used to decide when the body has "
               + "arrived at the board and to spot a foot landing over the foul line.")]
        public float footLead = 0.32f;
        [Tooltip("Carrot distance for the run-up. The run-to-target policies were trained on a fixed "
               + "target and drift sideways against one: measured on the first attempt, the sprint policy "
               + "left a 1.22 m runway by 1.75 m and landed on the deck beside the sand. Re-aiming at a "
               + "point this far ahead on the centre line every step, the way the lap event's carrot "
               + "does, keeps the heading correction alive all the way to the board.")]
        public float approachLookahead = 9f;

        [Header("Attempt timing")]
        [Tooltip("Abandoned as a pass if the competitor has not reached the board by then.")]
        public float attemptSeconds = 22f;
        [Tooltip("How long the jumper is left in the sand while the mark is read.")]
        public float settleSeconds = 1.8f;
        [Tooltip("Pause on the runway before each attempt.")]
        public float betweenAttempts = 1.5f;

        [Header("Waiting area")]
        [Tooltip("How far off the centre line the competitors not jumping stand.")]
        public float waitLateral = 3.2f;
        public float waitSpacing = 1.3f;

        // ---- live state (only one competitor is on the runway at a time) ----
        public Card[] Cards { get; private set; } = Array.Empty<Card>();
        public Athlete Competitor { get; private set; }
        public Stage CurrentStage { get; private set; } = Stage.Waiting;
        /// <summary>1-based round currently being jumped.</summary>
        public int Round => _round + 1;
        /// <summary>The last attempt's label, e.g. "6.12 m" or "foul".</summary>
        public string LastMark { get; private set; } = "-";

        readonly List<Athlete> _order = new List<Athlete>();
        int _round, _index;
        float _stageTimer;
        // Per-attempt working state.
        bool _foul, _tookOff, _hasPlant;
        float _plantX, _takeoffX, _takeoffSpeed, _markX;
        bool[] _footWasDown = Array.Empty<bool>();

        void Awake()
        {
            if (pit != null) raceDistance = pit.RunwayLength;
            autoRestart = false;   // a jump competition ends on the board, not on a timer
        }

        // ---------------------------------------------------------------- competition flow

        public override void StartRace()
        {
            if (pit == null) { Debug.LogError("[LongJumpEvent] No LongJumpPit assigned.", this); return; }
            _order.Clear();
            Cards = new Card[Athletes.Count];
            for (int i = 0; i < Athletes.Count; i++)
            {
                Cards[i] = new Card { athlete = Athletes[i] };
                _order.Add(Athletes[i]);
            }
            _round = 0;
            _index = 0;
            LastMark = "-";
            base.StartRace();            // resets and parks everyone, then opens the countdown
            OpenAttempt();
        }

        /// <summary>Puts the next competitor on the runway and starts the countdown for its attempt.</summary>
        void OpenAttempt()
        {
            if (_order.Count == 0) { Finish(); return; }
            Competitor = _order[_index];
            Reference = Competitor;      // the HUD and the director follow whoever is jumping
            PlaceOnRunway(Competitor);
            CurrentStage = Stage.Waiting;
            _foul = _tookOff = _hasPlant = false;
            _plantX = _takeoffX = _takeoffSpeed = 0f;
            _markX = float.MaxValue;
            _stageTimer = 0f;
            Countdown = Mathf.Max(0.1f, betweenAttempts);
            Current = Phase.Countdown;
            Stability = 1f;
        }

        protected override void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            switch (Current)
            {
                case Phase.Countdown:
                    Countdown -= dt;
                    ParkWaiting();
                    if (Competitor != null && Competitor.IsRL)
                        Competitor.rig.ResetPose(SpawnPosition(Competitor) + Vector3.up * Competitor.spawnHeight, SpawnRotation(Competitor));
                    if (Countdown <= 0f) Go();
                    break;
                case Phase.Running:
                    RaceTime += dt;
                    ParkWaiting();
                    TickAttempt(dt);
                    break;
                case Phase.Finished:
                    ParkWaiting();
                    _phaseTimer += dt;
                    if (autoRestart && _phaseTimer >= restartDelay) StartRace();
                    break;
            }
        }

        protected override void Go()
        {
            Current = Phase.Running;
            CurrentStage = Stage.Approach;
            _stageTimer = 0f;
            if (Competitor == null) return;
            SetCourseTarget(Competitor);
            if (Competitor.IsRL)
            {
                if (Competitor.runner != null) Competitor.runner.enabled = true;
                int n = Competitor.rig != null && Competitor.rig.feet != null ? Competitor.rig.feet.Length : 0;
                _footWasDown = new bool[n];
                for (int i = 0; i < n; i++)
                    _footWasDown[i] = Competitor.rig.feet[i] != null && Competitor.rig.feet[i].InContact;
            }
            Competitor.heuristic?.Go();
        }

        void TickAttempt(float dt)
        {
            Athlete a = Competitor;
            if (a == null) { Finish(); return; }
            _stageTimer += dt;
            switch (CurrentStage)
            {
                case Stage.Approach: Approach(a, dt); break;
                case Stage.Flight: Flight(a); break;
                case Stage.Settle: Settle(a); break;
            }
        }

        // ---------------------------------------------------------------- one attempt

        void Approach(Athlete a, float dt)
        {
            Vector3 pos = BodyPosition(a);
            float x = pit.Along(pos);
            a.speed = a.IsRL
                ? new Vector3(a.rig.BaseLinearVelocityWorld.x, 0f, a.rig.BaseLinearVelocityWorld.z).magnitude
                : (a.heuristic != null ? a.heuristic.Speed : 0f);
            a.time = _stageTimer;

            if (a.IsRL)
            {
                if (DetectFall(a, pos)) { ScoreAttempt(a, "fell on the run-up"); return; }
                Stability = Mathf.Lerp(Stability, Mathf.Clamp01((a.rig.UprightDot - fallUprightDot) / (1f - fallUprightDot)), 0.05f);
                WatchFootPlants(a);
                a.command?.SetTarget(pit.Point(x + approachLookahead));   // carrot on the centre line
            }

            // The body has arrived at the board: take off from the last foot the athlete actually planted.
            if (x + footLead >= pit.takeoffX) { TakeOff(a, x); return; }
            if (_stageTimer >= attemptSeconds) ScoreAttempt(a, "pass");
        }

        /// <summary>
        /// Records where each foot last came down. The take-off point is the last plant before the board,
        /// so an athlete whose stride puts it 40 cm behind the line loses those 40 cm off the measurement,
        /// and a foot that comes down over the line is a foul — both the real rules.
        /// </summary>
        void WatchFootPlants(Athlete a)
        {
            FootContactSensor[] feet = a.rig.feet;
            if (feet == null) return;
            if (_footWasDown.Length != feet.Length) _footWasDown = new bool[feet.Length];
            for (int i = 0; i < feet.Length; i++)
            {
                FootContactSensor f = feet[i];
                if (f == null) continue;
                bool down = f.InContact;
                if (down && !_footWasDown[i])
                {
                    float fx = pit.Along(f.transform.position);
                    if (fx > pit.takeoffX) _foul = true;      // took off over the line
                    _plantX = fx;
                    _hasPlant = true;
                }
                _footWasDown[i] = down;
            }
        }

        void TakeOff(Athlete a, float bodyX)
        {
            _tookOff = true;
            _takeoffX = _hasPlant ? _plantX : bodyX + footLead;
            // The kinematic bot has no stride to get wrong, so it hits the board every time. (It also
            // moves per frame, not per physics step, so the raw position here overshoots the line by up
            // to a frame's travel — without this clamp that read as a foul on every attempt.)
            if (!a.IsRL) _takeoffX = Mathf.Min(_takeoffX, pit.takeoffX);
            if (_takeoffX > pit.takeoffX) _foul = true;

            Vector3 dir = pit.Direction;
            if (a.IsRL && a.rig != null && a.rig.root != null)
            {
                Vector3 v = a.rig.root.linearVelocity;
                _takeoffSpeed = new Vector3(v.x, 0f, v.z).magnitude;
                // The one scripted moment: the take-off leg's extension, as a vertical velocity on the
                // base. The policy stays enabled so the legs keep cycling through the flight, which is
                // roughly what a hitch-kick looks like; PhysX owns the arc from here.
                v.y = Mathf.Max(v.y, takeoffRise);
                v += dir * takeoffDrive;
                a.rig.root.linearVelocity = v;
            }
            else if (a.heuristic != null)
            {
                _takeoffSpeed = a.heuristic.Speed;
                a.heuristic.Launch(dir * (_takeoffSpeed + takeoffDrive) + Vector3.up * takeoffRise, pit.sandY);
            }
            CurrentStage = Stage.Flight;
            _stageTimer = 0f;
        }

        void Flight(Athlete a)
        {
            Vector3 pos = BodyPosition(a);
            a.speed = a.IsRL
                ? new Vector3(a.rig.BaseLinearVelocityWorld.x, 0f, a.rig.BaseLinearVelocityWorld.z).magnitude
                : (a.heuristic != null ? a.heuristic.Speed : 0f);

            bool landed;
            if (a.IsRL)
            {
                // Ignore the first fraction of a second, or the foot still on the board reads as a landing.
                landed = _stageTimer > 0.12f && (AnyFootDown(a) || pos.y <= pit.sandY + a.spawnHeight * 0.55f);
            }
            else
            {
                landed = a.heuristic == null || !a.heuristic.Airborne;
            }
            if (!landed && _stageTimer < 4f) return;

            if (a.IsRL && a.runner != null) a.runner.enabled = false;   // no stand-up policy; it settles in the sand
            a.heuristic?.Stop();
            CurrentStage = Stage.Settle;
            _stageTimer = 0f;
            _markX = float.MaxValue;
        }

        void Settle(Athlete a)
        {
            _markX = Mathf.Min(_markX, NearestBreak(a));
            if (_stageTimer < settleSeconds) return;
            if (_foul) { ScoreAttempt(a, "foul"); return; }
            float mark = Mathf.Max(0f, _markX - pit.takeoffX);
            RecordMark(a, mark);
        }

        /// <summary>
        /// The break in the sand nearest the board, which is what the tape is pulled to. Every link low
        /// enough to be in the sand counts, so a jumper who sits back loses the metres it sat back by.
        /// </summary>
        float NearestBreak(Athlete a)
        {
            if (!a.IsRL || a.rig == null) return pit.Along(BodyPosition(a));
            float sandTop = pit.sandY + 0.32f;
            float best = float.MaxValue;
            if (a.rig.root != null && a.rig.root.transform.position.y <= sandTop)
                best = pit.Along(a.rig.root.transform.position);
            foreach (ArticulationBody ab in a.rig.joints)
            {
                if (ab == null) continue;
                Vector3 p = ab.transform.position;
                if (p.y > sandTop) continue;
                best = Mathf.Min(best, pit.Along(p));
            }
            return best == float.MaxValue ? pit.Along(BodyPosition(a)) : best;
        }

        bool AnyFootDown(Athlete a)
        {
            FootContactSensor[] feet = a.rig != null ? a.rig.feet : null;
            if (feet == null) return false;
            foreach (FootContactSensor f in feet) if (f != null && f.InContact) return true;
            return false;
        }

        // ---------------------------------------------------------------- scoring

        void RecordMark(Athlete a, float mark)
        {
            Card c = CardOf(a);
            c.marks.Add(mark);
            bool beyond = mark > pit.pitFarX - pit.takeoffX;   // landed past the far edge of the sand
            string note = beyond ? $"{mark:F2} m (past the pit)" : $"{mark:F2} m";
            FinishAttempt(a, c, note, mark);
        }

        void ScoreAttempt(Athlete a, string note) => FinishAttempt(a, CardOf(a), note, -1f);

        void FinishAttempt(Athlete a, Card c, string note, float mark)
        {
            c.taken++;
            c.notes.Add(note);
            if (mark >= 0f && (!c.hasMark || mark > c.best)) { c.best = mark; c.hasMark = true; }
            a.distance = c.best;
            a.time = c.best;              // the board sorts on metres, not seconds
            a.finished = c.hasMark;
            LastMark = note;
            Debug.Log($"[LongJumpEvent] round {Round}  {a.name}: {note}" +
                      (mark >= 0f ? $"  (took off {(pit.takeoffX - _takeoffX):F2} m behind the line at {_takeoffSpeed:F1} m/s)" : ""));

            Park(a);
            a.fell = false;               // going down in the sand is the landing, not a fall
            Advance();
        }

        void Advance()
        {
            _index++;
            if (_index >= _order.Count)
            {
                _index = 0;
                _round++;
            }
            if (_round >= Mathf.Max(1, attemptsEach)) { Finish(); return; }
            OpenAttempt();
        }

        protected override void Finish()
        {
            Competitor = null;
            CurrentStage = Stage.Waiting;
            Current = Phase.Finished;
            _phaseTimer = 0f;
            BuildResults();
            var sb = new StringBuilder();
            foreach (Athlete a in Athletes)
            {
                if (sb.Length > 0) sb.Append("  |  ");
                sb.Append(SummaryFor(a));
            }
            LastResult = sb.ToString();
            Attempt++;
            Debug.Log($"[LongJumpEvent] competition {Attempt}: {LastResult}");
            RaiseFinished();
        }

        // ---------------------------------------------------------------- placement

        /// <summary>Competitors on the runway start their run-up on the centre line.</summary>
        protected override Vector3 SpawnPosition(Athlete a) =>
            a == Competitor ? pit.Point(pit.runwayStartX + 0.4f) : WaitPosition(a);

        protected override Quaternion SpawnRotation(Athlete a) =>
            Quaternion.FromToRotation(Vector3.right, Flat(pit.Direction));

        protected override void SetCourseTarget(Athlete a)
        {
            // Aim well past the pit so the policy never eases off before the board.
            a.command?.SetTarget(pit.Point(pit.pitFarX + 12f));
        }

        protected override float MeasureDistance(Athlete a, Vector3 pos) => pit.Measure(pos);

        protected override float FloorY(Athlete a) => pit.surfaceY;

        protected override void ResetHeuristic(Athlete a, Vector3 p) => a.heuristic.ResetTo(p, pit.Direction);

        /// <summary>
        /// The mark each competitor stands on while somebody else jumps: two rows beside the runway on the
        /// north side, so the cameras on the south side always have a clear line to the jumper.
        /// </summary>
        Vector3 WaitPosition(Athlete a)
        {
            int slot = a.lane;
            float row = slot % 2;
            float back = (slot / 2) * waitSpacing;
            return pit.Point(pit.takeoffX - 2.5f - back, waitLateral + row * 1.4f);
        }

        void PlaceOnRunway(Athlete a)
        {
            Vector3 p = SpawnPosition(a);
            Quaternion rot = SpawnRotation(a);
            a.stopping = false;
            a.fell = false;
            a.speed = 0f;
            a.minUpright = 1f; a.minHeightFrac = 1f; a.uprightSum = 0f; a.uprightSamples = 0;
            if (a.IsRL)
            {
                if (a.rig.root != null) a.rig.root.immovable = false;
                if (a.runner != null) { a.runner.enabled = false; a.runner.ResetEpisode(p + Vector3.up * a.spawnHeight, rot); }
                else a.rig.ResetPose(p + Vector3.up * a.spawnHeight, rot);
                SetCourseTarget(a);
            }
            else if (a.heuristic != null)
            {
                a.heuristic.ResetTo(p, pit.Direction);
            }
        }

        /// <summary>
        /// Parks an athlete on its waiting mark and pins it there. Same reasoning as RaceEvent's pull-up:
        /// the policies have no stand-still behaviour, so a competitor left under policy control folds up
        /// while it waits its turn.
        /// </summary>
        void Park(Athlete a)
        {
            a.stopping = true;
            if (a.IsRL && a.runner != null) a.runner.enabled = false;
            a.heuristic?.Stop();
            Vector3 p = WaitPosition(a);
            Quaternion rot = SpawnRotation(a);
            if (a.IsRL && a.rig != null)
            {
                a.rig.ResetPose(p + Vector3.up * a.spawnHeight, rot);
                if (a.rig.root != null) a.rig.root.immovable = true;
            }
            else if (a.go != null)
            {
                a.go.transform.SetPositionAndRotation(p, rot);
            }
        }

        void ParkWaiting()
        {
            foreach (Athlete a in Athletes)
            {
                if (a == Competitor || !a.stopping) continue;
                if (!a.IsRL || a.rig == null || a.rig.root == null) continue;
                if (a.rig.root.immovable) continue;
                a.rig.ResetPose(WaitPosition(a) + Vector3.up * a.spawnHeight, SpawnRotation(a));
                a.rig.root.immovable = true;
            }
        }

        protected override void ResetAthlete(Athlete a)
        {
            a.time = 0f; a.distance = 0f; a.speed = 0f; a.finished = false; a.fell = false; a.fellAt = 0f;
            a.minUpright = 1f; a.minHeightFrac = 1f; a.uprightSum = 0f; a.uprightSamples = 0;
            if (a.IsRL && a.rig != null && a.rig.root != null) a.rig.root.immovable = false;
            Park(a);
        }

        protected override void HoldStopped(Athlete a) { }   // parking already pins the waiting competitors

        static Vector3 Flat(Vector3 v)
        {
            Vector3 f = new Vector3(v.x, 0f, v.z);
            return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.right;
        }

        Vector3 BodyPosition(Athlete a) => a.IsRL ? a.rig.BasePosition : a.go.transform.position;

        Card CardOf(Athlete a)
        {
            if (Cards != null && a.lane >= 0 && a.lane < Cards.Length && Cards[a.lane] != null) return Cards[a.lane];
            return new Card { athlete = a };   // defensive: never drop an attempt on the floor
        }

        // ---------------------------------------------------------------- presentation

        /// <summary>Furthest best mark wins; anybody without a mark sorts to the bottom.</summary>
        protected override int Rank(Athlete x, Athlete y)
        {
            Card cx = CardOf(x), cy = CardOf(y);
            if (cx.hasMark != cy.hasMark) return cx.hasMark ? -1 : 1;
            if (cx.hasMark) return cy.best.CompareTo(cx.best);
            return 0;
        }

        protected override string StatusFor(Athlete a)
        {
            Card c = CardOf(a);
            return c.hasMark ? $"{c.best:F2} m  ({c.marks.Count}/{c.taken})" : "no mark";
        }

        protected override string SummaryFor(Athlete a)
        {
            Card c = CardOf(a);
            string all = c.notes.Count > 0 ? string.Join(", ", c.notes) : "-";
            return c.hasMark ? $"{Tag(a)} {a.name} {c.best:F2} m [{all}]" : $"{Tag(a)} {a.name} no mark [{all}]";
        }

        public override string ResultsSubtitle(List<RaceResult> results)
        {
            int scored = 0;
            foreach (RaceResult r in results) if (r.finished) scored++;
            return $"{scored} of {results.Count} recorded a mark over {Mathf.Max(1, attemptsEach)} rounds";
        }

        public override string HudResultLine(List<RaceResult> results)
        {
            RaceResult top = results[0];
            return top.finished ? $"Won by {top.name}  {top.time:F2} m" : "No marks recorded";
        }

        public override string HudDistanceLine()
        {
            if (Current == Phase.Finished) return $"Best  {(Reference != null ? Reference.distance : 0f):F2} m   complete";
            string who = Competitor != null ? Competitor.name : "-";
            string best = Competitor != null && CardOf(Competitor).hasMark ? $"{CardOf(Competitor).best:F2} m" : "-";
            return $"Round {Round}/{Mathf.Max(1, attemptsEach)}   {who}   best {best}   last {LastMark}";
        }
    }
}
