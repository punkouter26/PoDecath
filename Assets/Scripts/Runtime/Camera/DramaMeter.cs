using System.Collections.Generic;
using UnityEngine;
using PoDecath.Sim;

namespace PoDecath.Cam
{
    /// <summary>
    /// How interesting the race is right now, as a number, so the gallery and the crowd can react to it.
    ///
    /// The director already cuts on a timer with an override for incidents, and the crowd already has
    /// moods. Both are working from "what has happened". What neither has is any sense of what is *about*
    /// to happen, which is the thing a human gallery is actually good at: holding a wide shot while the
    /// field is strung out, going tight as a gap closes, and cutting to a runner a beat before it goes
    /// down because it has been fighting the bend for two seconds.
    ///
    /// Every term below comes off something the simulation already measures, and each is scaled against a
    /// threshold this project established by measurement rather than by taste:
    ///
    /// - **Closeness and closing rate.** The gap between the leader and second, and how fast it is
    ///   shrinking. <c>RaceAudio</c> already treats 2.5 m as "close", so that is the scale used here.
    /// - **Fall risk.** Two signals. Uprightness against <see cref="RaceEvent.fallUprightDot"/>, the
    ///   threshold the event itself uses to call a fall; and lateral acceleration, because the project's
    ///   own measurements put clean laps at 1.89 m/s2 (0.19 g) and a 0.6-1.0 fall rate at 3.44 m/s2
    ///   (0.35 g) on the 8.8 m bend. An athlete pulling 3 m/s2 sideways is in trouble whether or not it is
    ///   tilted yet, and that is a second of warning the director did not previously have.
    /// - **Lateness.** The same gap means more on the last lap than on the first.
    /// - **Incidents.** A fall or a recovery keeps the number up for a few seconds after the event.
    ///
    /// This class only measures. It moves no camera and plays no sound; <see cref="BroadcastDirector"/>
    /// and <c>RaceAudio</c> read it. That separation is on purpose — a tension model that also cut the
    /// cameras could not be checked against the race without watching the cameras.
    /// </summary>
    [DefaultExecutionOrder(90)]   // before the director (100) and the mix (120): they read this frame's value
    public class DramaMeter : MonoBehaviour
    {
        [Header("Wiring")]
        public RaceEvent race;

        [Header("Scales")]
        [Tooltip("Gap in metres between the first two at which a race stops being close. RaceAudio uses "
               + "the same 2.5 m, and the two should agree or the crowd and the camera disagree about "
               + "what they are watching.")]
        public float closeGap = 2.5f;

        [Tooltip("Lateral acceleration, in m/s2, that counts as full fall risk. The project measured a "
               + "0.6-1.0 fall rate at 3.44 m/s2 on the 8.8 m bend, so that is what this is set to.")]
        public float dangerousLateral = 3.44f;

        [Tooltip("Seconds an incident keeps the tension up after it happened.")]
        public float incidentMemory = 4f;

        [Tooltip("How fast the published tension follows the raw one. Low is a director that takes a "
               + "moment to notice, which is what a real one does.")]
        public float response = 2.2f;

        [Tooltip("Samples a second. The terms are all smoothed anyway; sampling per frame would buy noise.")]
        public float sampleHz = 10f;

        /// <summary>0..1: how much is at stake right now. Smoothed, and safe to read every frame.</summary>
        public float Tension { get; private set; }

        /// <summary>The athlete closest to going down, or null when nobody is in any trouble.</summary>
        public RaceEvent.Athlete MostAtRisk { get; private set; }

        /// <summary>That athlete's risk, 0..1. Zero when <see cref="MostAtRisk"/> is null.</summary>
        public float WorstRisk { get; private set; }

        /// <summary>The athlete currently closing fastest on the one in front of it, if anybody is.</summary>
        public RaceEvent.Athlete Chaser { get; private set; }

        /// <summary>Metres between the first two, or -1 when there are not two of them still racing.</summary>
        public float LeadGap { get; private set; } = -1f;

        /// <summary>Metres per second that gap is shrinking by. Negative means the leader is pulling away.</summary>
        public float ClosingRate { get; private set; }

        /// <summary>
        /// One line naming what makes this moment worth watching, for the commentary and the lower third.
        /// Empty when nothing in particular does.
        /// </summary>
        public string Story { get; private set; } = "";

        /// <summary>Per-athlete working state. Kept across attempts and reset with the race.</summary>
        class Track
        {
            public Vector3 lastVelocity;
            public float lateral;        // smoothed lateral acceleration, m/s2
            public float upright = 1f;
            public float uprightRate;    // per second, negative while going over
            public float risk;
        }

        readonly Dictionary<RaceEvent.Athlete, Track> _tracks = new Dictionary<RaceEvent.Athlete, Track>();
        float _next;
        float _lastSample;
        float _lastGap = -1f;
        float _incidentLeft;
        int _knownFallen, _knownFinished, _knownRecoveries;
        int _lastAttempt = -1;
        float _raw;

        void Update()
        {
            if (race == null) return;

            if (race.Attempt != _lastAttempt)
            {
                _lastAttempt = race.Attempt;
                _tracks.Clear();
                _knownFallen = _knownFinished = _knownRecoveries = 0;
                _incidentLeft = 0f;
                _lastGap = -1f;
                _raw = Tension = 0f;
                Story = "";
            }

            _incidentLeft = Mathf.Max(0f, _incidentLeft - Time.deltaTime);

            if (Time.time >= _next)
            {
                float dt = _lastSample > 0f ? Time.time - _lastSample : 1f / Mathf.Max(1f, sampleHz);
                _lastSample = Time.time;
                _next = Time.time + 1f / Mathf.Max(1f, sampleHz);
                Sample(dt);
            }

            Tension = Mathf.MoveTowards(Tension, _raw, response * Time.deltaTime);
        }

        void Sample(float dt)
        {
            if (race.Current != RaceEvent.Phase.Running)
            {
                // A countdown is its own kind of tense and a finished race is not tense at all.
                _raw = race.Current == RaceEvent.Phase.Countdown ? 0.45f : 0.1f;
                MostAtRisk = Chaser = null;
                WorstRisk = 0f;
                LeadGap = -1f;
                Story = race.Current == RaceEvent.Phase.Countdown ? "on the marks" : "";
                return;
            }

            Incidents();
            Risks(dt);
            float closeness = Closeness(dt);
            float progress = race.raceDistance > 0f ? Mathf.Clamp01(Leader() / race.raceDistance) : 0f;

            // The terms are combined by taking the strongest rather than by adding, because they are
            // alternatives, not contributions: a race can be gripping because two runners are level OR
            // because one is about to fall over, and adding them would make a quiet race with one wobble
            // look like a finish.
            float incident = _incidentLeft > 0f ? _incidentLeft / Mathf.Max(0.01f, incidentMemory) : 0f;
            float best = Mathf.Max(Mathf.Max(closeness, WorstRisk), incident);

            // Lateness is a multiplier on whatever the race is doing, not a term of its own. Two runners
            // level at ten metres is every race there has ever been; two runners level with a lap to go is
            // the reason anyone is watching.
            _raw = Mathf.Clamp01(best * Mathf.Lerp(0.6f, 1f, progress) + 0.12f);

            Story = Narrate(closeness, incident, progress);
        }

        /// <summary>Furthest anybody has got, which is what "how far in are we" means for the whole field.</summary>
        float Leader()
        {
            float lead = 0f;
            foreach (RaceEvent.Athlete a in race.Athletes) lead = Mathf.Max(lead, a.distance);
            return lead;
        }

        /// <summary>
        /// The gap between the first two still racing, and how fast it is changing. A closing gap counts
        /// for much more than a static one of the same size: a pass about to happen is the event, and a
        /// two-metre gap that has been two metres for a lap is not.
        /// </summary>
        float Closeness(float dt)
        {
            RaceEvent.Athlete first = null, second = null;
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (a.fell || a.finished) continue;
                if (first == null || a.distance > first.distance) { second = first; first = a; }
                else if (second == null || a.distance > second.distance) second = a;
            }

            if (first == null || second == null)
            {
                LeadGap = -1f;
                ClosingRate = 0f;
                Chaser = null;
                return 0f;
            }

            float gap = first.distance - second.distance;
            ClosingRate = _lastGap >= 0f && dt > 0f ? (_lastGap - gap) / dt : 0f;
            _lastGap = gap;
            LeadGap = gap;
            Chaser = ClosingRate > 0.15f ? second : null;

            float tight = 1f - Mathf.Clamp01(gap / Mathf.Max(0.01f, closeGap));
            // A runner eating a metre a second is worth as much as one already alongside.
            float closing = Mathf.Clamp01(ClosingRate / 1f) * Mathf.Clamp01(1f - gap / (closeGap * 3f));
            return Mathf.Clamp01(Mathf.Max(tight, closing));
        }

        /// <summary>
        /// Per-athlete fall risk, from the two things that actually precede a fall on this track: the body
        /// tipping, and the body being asked to corner harder than it can.
        /// </summary>
        void Risks(float dt)
        {
            MostAtRisk = null;
            WorstRisk = 0f;

            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (!a.IsRL || a.rig == null || a.fell || a.finished) continue;

                if (!_tracks.TryGetValue(a, out Track t))
                {
                    t = new Track { lastVelocity = Flat(a.rig.BaseLinearVelocityWorld), upright = a.rig.UprightDot };
                    _tracks[a] = t;
                }

                Vector3 v = Flat(a.rig.BaseLinearVelocityWorld);
                if (dt > 1e-3f)
                {
                    // Lateral acceleration: the part of this athlete's change in velocity that is across
                    // its own direction of travel. Along-track acceleration is a runner speeding up, which
                    // is not a risk; across-track is a runner fighting a bend, which is.
                    Vector3 accel = (v - t.lastVelocity) / dt;
                    Vector3 heading = v.sqrMagnitude > 0.04f ? v.normalized : a.rig.BaseForward;
                    float lateral = Vector3.ProjectOnPlane(accel, heading).magnitude;
                    // Heavily smoothed: differentiating a velocity at 10 Hz is noisy, and one spike is not
                    // a runner losing a corner.
                    t.lateral = Mathf.Lerp(t.lateral, lateral, 0.25f);

                    float upright = a.rig.UprightDot;
                    t.uprightRate = Mathf.Lerp(t.uprightRate, (upright - t.upright) / dt, 0.4f);
                    t.upright = upright;
                }
                t.lastVelocity = v;

                float threshold = Mathf.Max(0.01f, race.fallUprightDot);
                float tilt = Mathf.Clamp01((1f - t.upright) / Mathf.Max(0.01f, 1f - threshold));
                float corner = Mathf.Clamp01(t.lateral / Mathf.Max(0.01f, dangerousLateral));
                // Going over fast is worse than being tilted and stable: a body losing half a unit of
                // uprightness a second has about a second left.
                float falling = Mathf.Clamp01(-t.uprightRate / 0.5f);

                t.risk = Mathf.Clamp01(Mathf.Max(Mathf.Max(tilt, corner), tilt * 0.5f + falling * 0.5f));
                if (t.risk <= WorstRisk) continue;
                WorstRisk = t.risk;
                MostAtRisk = a;
            }
        }

        /// <summary>How close one named athlete is to going down, 0..1. Zero for anyone not being tracked.</summary>
        public float RiskOf(RaceEvent.Athlete a) =>
            a != null && _tracks.TryGetValue(a, out Track t) ? t.risk : 0f;

        /// <summary>A fall, a recovery or a finisher keeps the number up for a few seconds afterwards.</summary>
        void Incidents()
        {
            int fallen = 0, finished = 0, recoveries = 0;
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (a.fell) fallen++;
                if (a.finished) finished++;
                recoveries += a.recoveries;
            }
            if (fallen > _knownFallen || finished > _knownFinished || recoveries > _knownRecoveries)
                _incidentLeft = incidentMemory;
            _knownFallen = fallen;
            _knownFinished = finished;
            _knownRecoveries = recoveries;
        }

        /// <summary>
        /// The shortest true thing about this moment. Ordered by what a commentator would actually lead
        /// with: somebody in trouble beats a close race, which beats a race merely in progress.
        /// </summary>
        string Narrate(float closeness, float incident, float progress)
        {
            if (WorstRisk > 0.7f && MostAtRisk != null) return $"{MostAtRisk.name} is in trouble";
            if (incident > 0.4f) return "incident on the track";
            if (LeadGap >= 0f && LeadGap < 0.8f) return "nothing in it";
            if (Chaser != null && ClosingRate > 0.4f) return $"{Chaser.name} is closing";
            if (progress > 0.9f) return "into the last of it";
            if (closeness < 0.15f && progress > 0.4f) return "strung out";
            return "";
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
