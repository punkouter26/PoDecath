using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>
    /// Lap race around the rooftop TrackPath. RL athletes chase a carrot placed by TrackFollower; the
    /// heuristic bot follows the path kinematically. Distance is progress along the track, so
    /// raceDistance = laps x lap length. Everything else (countdown, falls, results, restart) is DashEvent.
    ///
    /// The field lines up on DashEvent's staggered grid — <see cref="DashEvent.maxLanes"/> abreast, further
    /// rows set back along the track. The deck is only 5.3 m wide and the lane pitch is solved for five
    /// abreast, so eight in a row would start off the roof. Each runner still covers a full lap measured from its own start point
    /// (TrackFollower.Progress counts from wherever it was reset), so the stagger costs nobody distance.
    /// </summary>
    public class LapEvent : DashEvent
    {
        public TrackPath path;
        public int laps = 1;
        [Tooltip("Arc length of the start line along the loop (0 = start of the dash straight).")]
        public float startS = 0f;
        [Tooltip("Carrot distance for RL athletes. Training used 6 m, but 9 m smooths the bend entry: 5/5 clean laps vs 1/4 at 6 m.")]
        public float lookahead = 9f;
        [Tooltip("Clock allowed per lap. A lap-trained policy runs about 25 s and the RED bot about 11 s, "
               + "so 60 s is generous without letting a stalled field sit there for ever. maxRaceSeconds "
               + "is recomputed from this and the lap count, because a 1500 m cannot share a 400 m's clock.")]
        public float secondsPerLap = 60f;
        [Tooltip("Ceiling on the recomputed clock however many laps are asked for. A 1500 m that nobody "
               + "is going to finish should not hold the screen for a quarter of an hour.")]
        public float maxRaceSecondsCap = 600f;
        [Tooltip("Set when the event is running over hurdles; only used to word the results.")]
        public HurdleSet hurdles;

        void Awake()
        {
            // One scene serves every lap distance. The picker puts the lap count in SessionSettings on its
            // way out, so 100 m, 400 m and 1500 m are the same LapEvent with a different number here.
            if (SessionSettings.Laps > 0) laps = SessionSettings.Laps;
            laps = Mathf.Max(1, laps);
            if (path != null) raceDistance = laps * path.LapLength;
            maxRaceSeconds = Mathf.Clamp(laps * secondsPerLap, 30f, maxRaceSecondsCap);
        }

        // The grid itself is DashEvent's (maxLanes abreast, rowSpacing between rows); the lap only maps a
        // slot onto the loop. Set maxLanes to 2 here: that keeps every runner within 0.54 m of the centre
        // line, the band the policies were trained on. Measured over a field of eight, every runner at
        // +-0.54 m finished and every runner at +-1.61 m fell, because a 1.6 m offset turns the 8.8 m bend
        // into a 7.2 m one.

        /// <summary>
        /// Arc length this runner starts from. Its lap is measured from here, so the stagger costs nobody
        /// distance. The grid is laid out forwards from <see cref="startS"/> — front row furthest along,
        /// last row exactly on the line — rather than backwards from it. Running the rows backwards puts
        /// the rear of an eight-strong field at s = -2.5 m, which wraps onto the closing bend, and a policy
        /// asked to set off from a standing start mid-curve falls inside a couple of metres.
        /// </summary>
        float StartArc(Athlete a) => startS + RowAdvance(a.lane);

        /// <summary>TrackPath counts lateral offset the opposite way round from the straight's own axis.</summary>
        float TrackLateral(Athlete a) => -LaneOffset(a.lane);

        protected override Vector3 SpawnPosition(Athlete a) => path != null ? path.Position(StartArc(a), TrackLateral(a)) : base.SpawnPosition(a);

        protected override Quaternion SpawnRotation(Athlete a)
        {
            if (path == null) return base.SpawnRotation(a);
            Vector3 t = path.Tangent(StartArc(a));
            return Quaternion.FromToRotation(Vector3.right, new Vector3(t.x, 0f, t.z).normalized);
        }

        protected override void SetCourseTarget(Athlete a)
        {
            if (path == null) { base.SetCourseTarget(a); return; }
            if (a.follower != null) { a.follower.lookahead = lookahead; a.follower.ResetAt(StartArc(a), TrackLateral(a)); }
            else a.command?.SetTarget(path.Position(StartArc(a) + lookahead, TrackLateral(a)));
        }

        protected override float MeasureDistance(Athlete a, Vector3 pos)
        {
            if (a.follower != null) return a.follower.Progress;
            if (a.heuristic != null) return a.heuristic.Distance;
            return base.MeasureDistance(a, pos);
        }

        protected override float FloorY(Athlete a) => path != null ? path.deckTopY : base.FloorY(a);

        protected override void ResetHeuristic(Athlete a, Vector3 p)
        {
            if (path != null) a.heuristic.ResetOnTrack(path, StartArc(a), TrackLateral(a));
            else base.ResetHeuristic(a, p);
        }

        // ---- results wording ----

        int Down(Athlete a) => hurdles != null ? hurdles.KnockedBy(a) : 0;

        /// <summary>
        /// The board's right-hand column, plus what the runner did to the hurdles when there are any.
        /// Nobody has been trained to clear one yet, so how many a runner put down is most of the story
        /// of its race and belongs next to the time.
        /// </summary>
        protected override string StatusFor(Athlete a)
        {
            int down = Down(a);
            return down > 0 ? $"{base.StatusFor(a)}  ·  {down} down" : base.StatusFor(a);
        }

        protected override string SummaryFor(Athlete a)
        {
            int down = Down(a);
            return down > 0 ? $"{base.SummaryFor(a)} ({down} hurdle{(down == 1 ? "" : "s")} down)" : base.SummaryFor(a);
        }

        public override string ResultsSubtitle(System.Collections.Generic.List<RaceResult> results)
        {
            int finishers = 0;
            foreach (RaceResult r in results) if (r.finished) finishers++;
            string what = laps > 1 ? $"{raceDistance:F0} m ({laps} laps)" : $"{raceDistance:F0} m lap";
            string over = hurdles != null && hurdles.Count > 0 ? $", {hurdles.Down} of {hurdles.Count} hurdles down" : "";
            return $"{finishers} of {results.Count} completed the {what}{over}";
        }
    }
}
