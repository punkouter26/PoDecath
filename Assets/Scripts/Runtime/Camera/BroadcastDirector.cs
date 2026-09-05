using UnityEngine;
using Unity.Cinemachine;
using PoDecath.Sim;

namespace PoDecath.Cam
{
    /// <summary>
    /// Cuts the race like an athletics broadcast. Seven fixed jobs, the way a real gallery is rigged:
    /// a low shot on the grid for the countdown, a tight one off the gun, a rail camera running the field
    /// down the straights, a high camera outside each bend, a stadium wide for the settled middle, a head-on
    /// from in front of the leader up the home straight, and a side-on at the line.
    ///
    /// The director owns the camera positions (Cinemachine only aims them), so the moving shots can be
    /// parked on the arc length of the track rather than hand-placed. Shots are held for
    /// <see cref="minShotSeconds"/> so the cut rate stays watchable; a fall or a finisher cuts immediately,
    /// the way a live gallery abandons its planned shot for an incident.
    ///
    /// "Leader" means the leader of the race still being run: once a runner crosses the line the camera
    /// moves to whoever is still racing, so the fight for the remaining places stays on screen.
    ///
    /// With a <see cref="pit"/> wired and a <see cref="LongJumpEvent"/> as the race, the same seven
    /// positions are re-rigged for the long jump: a low shot behind the jumper on the mark, a tight one as
    /// it sets off, a rail camera tracking the run-up, a side-on at the board that holds through the
    /// flight (the shot the event is known for), a low shot at the sand for the landing, a head-on down
    /// the runway and a high wide over the whole pit for the board.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class BroadcastDirector : MonoBehaviour
    {
        public enum Shot { StartLine, OffTheGun, Rail, Bend, Wide, HeadOn, Finish }

        [Header("Wiring")]
        public DashEvent race;
        public TrackPath path;
        [Tooltip("Set for the long jump; the director then cuts the runway instead of the loop.")]
        public LongJumpPit pit;

        [Header("Shots")]
        public CinemachineCamera startLineCam;
        public CinemachineCamera offTheGunCam;
        public CinemachineCamera railCam;
        public CinemachineCamera bendCam;
        public CinemachineCamera wideCam;
        public CinemachineCamera headOnCam;
        public CinemachineCamera finishCam;

        [Header("Cutting")]
        [Tooltip("Shortest time a shot is held before the director is allowed to cut again.")]
        public float minShotSeconds = 2.6f;
        [Tooltip("How long the wide shot stays on a fall before the running order resumes.")]
        public float incidentSeconds = 3f;

        [Header("Placement (metres)")]
        [Tooltip("How far outside the deck edge the trackside cameras sit. The deck is ringed by a 1 m "
               + "barrier, so a camera parked close and low just films the barrier.")]
        public float trackside = 8f;
        public float railHeight = 3.0f;
        public float bendHeight = 8.0f;
        public float headOnLead = 14f;
        public float wideHeight = 30f;
        public float wideBack = 40f;

        public Shot Current { get; private set; } = Shot.StartLine;
        public string CurrentName => Current.ToString();

        Transform _leaderSubject, _fieldSubject;
        float _shotAge;
        float _incidentLeft;
        int _lastAttempt = -1;
        int _knownFallen, _knownFinished;
        float _startS, _finishS;
        // Long jump: the competitor and stage last seen, so a change of either is a cut.
        DashEvent.Athlete _jumpCompetitor;
        LongJumpEvent.Stage _jumpStage;
        float _jumpStageAge;

        float Outward => (path != null ? path.deckWidth * 0.5f : 2.6f) + trackside;

        void Awake()
        {
            _leaderSubject = new GameObject("BroadcastSubject_Leader").transform;
            _fieldSubject = new GameObject("BroadcastSubject_Field").transform;
            _leaderSubject.SetParent(transform, false);
            _fieldSubject.SetParent(transform, false);
            // A lap starts and finishes on the same line; a dash finishes raceDistance further round.
            if (race is LapEvent lap) { _startS = lap.startS; _finishS = _startS; }
            else if (path != null && race != null) { _startS = path.ProjectGlobal(race.startLine); _finishS = _startS + race.raceDistance; }

            Aim(startLineCam, _fieldSubject); Aim(wideCam, _fieldSubject);
            Aim(offTheGunCam, _leaderSubject); Aim(railCam, _leaderSubject); Aim(bendCam, _leaderSubject);
            Aim(headOnCam, _leaderSubject); Aim(finishCam, _leaderSubject);
        }

        static void Aim(CinemachineCamera cam, Transform subject)
        {
            if (cam == null) return;
            cam.LookAt = subject;
            cam.Follow = null;   // the director places these; Cinemachine only aims them
        }

        void LateUpdate()
        {
            if (race == null) return;
            if (pit != null && race is LongJumpEvent jump) { DirectJump(jump); return; }
            if (path == null) return;

            if (race.Attempt != _lastAttempt)
            {
                _lastAttempt = race.Attempt;
                _knownFallen = _knownFinished = 0;
                _incidentLeft = 0f;
                _shotAge = minShotSeconds;   // a new race may open on its own shot straight away
            }

            DashEvent.Athlete leader = Leader(out DashEvent.Athlete faller, out int fallen, out int finished);
            Vector3 centroid = FieldCentre();
            if (leader != null) _leaderSubject.position = Subject(leader);
            _fieldSubject.position = centroid;

            // A new fall or a new finisher is worth abandoning the planned shot for.
            bool cutNow = false;
            if (fallen > _knownFallen)
            {
                _knownFallen = fallen;
                _incidentLeft = incidentSeconds;
                cutNow = true;
            }
            if (finished > _knownFinished) { _knownFinished = finished; cutNow = true; }
            if (_incidentLeft > 0f)
            {
                _incidentLeft -= Time.deltaTime;
                if (faller != null) _fieldSubject.position = Subject(faller);
            }

            float leaderS = leader != null ? ArcOf(leader) : _startS;
            PlaceCameras(leaderS);

            Shot want = Choose(leader, finished, leaderS);
            _shotAge += Time.deltaTime;
            if (want != Current && (cutNow || _shotAge >= minShotSeconds))
            {
                Current = want;
                _shotAge = 0f;
            }
            Apply();
        }

        Shot Choose(DashEvent.Athlete leader, int finished, float leaderS)
        {
            if (race.Current == DashEvent.Phase.Countdown || race.Current == DashEvent.Phase.Idle) return Shot.StartLine;
            if (race.Current == DashEvent.Phase.Finished) return Shot.Finish;
            if (_incidentLeft > 0f) return Shot.Wide;
            if (leader == null) return Shot.Finish;

            float f = race.raceDistance > 0f ? leader.distance / race.raceDistance : 0f;
            if (f < 0.07f) return Shot.OffTheGun;            // away from the line
            if (finished > 0 || f > 0.94f) return Shot.Finish;
            if (f > 0.86f) return Shot.HeadOn;               // home straight, running at camera
            if (InBend(leaderS)) return Shot.Bend;
            if (f > 0.35f && f < 0.55f) return Shot.Wide;     // settled middle: show the whole field
            return Shot.Rail;
        }

        void PlaceCameras(float leaderS)
        {
            float outward = Outward;
            Vector3 up = Vector3.up;

            Place(startLineCam, path.Position(_startS - 6f, outward * 0.75f) + up * 2.2f);
            Place(offTheGunCam, path.Position(_startS + 7f, outward * 0.7f) + up * 2.0f);
            Place(railCam, path.Position(leaderS, outward) + up * railHeight);
            Place(bendCam, path.Position(NearestBendApex(leaderS), outward + 3f) + up * bendHeight);
            Place(headOnCam, path.Position(leaderS + headOnLead, 0f) + up * 1.9f);
            Place(finishCam, path.Position(_finishS, outward * 0.8f) + up * 2.4f);

            // Stadium wide: high, set back off the finish straight, framing the whole loop.
            Vector3 loopCentre = path.transform.position;
            loopCentre.y = path.deckTopY;
            Vector3 back = path.Position(_finishS, outward) - loopCentre;
            back.y = 0f;
            if (back.sqrMagnitude < 0.01f) back = Vector3.forward;
            Place(wideCam, loopCentre + back.normalized * wideBack + up * wideHeight);
        }

        static void Place(CinemachineCamera cam, Vector3 p)
        {
            if (cam != null) cam.transform.position = p;
        }

        void Apply()
        {
            SetPriority(startLineCam, Shot.StartLine);
            SetPriority(offTheGunCam, Shot.OffTheGun);
            SetPriority(railCam, Shot.Rail);
            SetPriority(bendCam, Shot.Bend);
            SetPriority(wideCam, Shot.Wide);
            SetPriority(headOnCam, Shot.HeadOn);
            SetPriority(finishCam, Shot.Finish);
        }

        void SetPriority(CinemachineCamera cam, Shot shot)
        {
            if (cam != null) cam.Priority = Current == shot ? 30 : 10;
        }

        // ---------------------------------------------------------------- long jump

        void DirectJump(LongJumpEvent jump)
        {
            DashEvent.Athlete who = jump.Competitor;
            bool cutNow = false;
            if (who != _jumpCompetitor || jump.CurrentStage != _jumpStage || race.Attempt != _lastAttempt)
            {
                _jumpCompetitor = who;
                _jumpStage = jump.CurrentStage;
                _lastAttempt = race.Attempt;
                _jumpStageAge = 0f;
                cutNow = true;   // a new jumper, a take-off or a landing is always worth the cut
            }
            _jumpStageAge += Time.deltaTime;
            _shotAge += Time.deltaTime;

            Vector3 centroid = FieldCentre();
            _fieldSubject.position = centroid;
            _leaderSubject.position = who != null ? Subject(who) : centroid;
            float x = who != null ? pit.Along(Subject(who)) : pit.runwayStartX;

            PlaceJumpCameras(x);
            Shot want = ChooseJump(jump, x);
            if (want != Current && (cutNow || _shotAge >= minShotSeconds))
            {
                Current = want;
                _shotAge = 0f;
            }
            Apply();
        }

        Shot ChooseJump(LongJumpEvent jump, float x)
        {
            if (race.Current == DashEvent.Phase.Finished) return Shot.Wide;
            if (race.Current != DashEvent.Phase.Running || jump.Competitor == null) return Shot.StartLine;
            switch (jump.CurrentStage)
            {
                case LongJumpEvent.Stage.Approach:
                    if (x < pit.runwayStartX + 5f) return Shot.OffTheGun;
                    // Settle on the board shot before the take-off, so the cut never lands mid-flight.
                    if (x > pit.takeoffX - 7f) return Shot.Bend;
                    return Shot.Rail;
                case LongJumpEvent.Stage.Flight:
                    return Shot.Bend;
                case LongJumpEvent.Stage.Settle:
                    return _jumpStageAge > 0.8f ? Shot.Finish : Shot.Bend;
                default:
                    return Shot.StartLine;
            }
        }

        /// <summary>
        /// Camera positions in the pit's frame. Everything sits on the south side of the runway so cuts
        /// never cross the line, and the trackside distance keeps the rail camera just outside the loop's
        /// inner barrier, looking over it.
        /// </summary>
        void PlaceJumpCameras(float jumperX)
        {
            const float side = -1f;
            Vector3 up = Vector3.up;
            float outward = pit.HalfPit + trackside * 0.6f;
            float mid = (pit.runwayStartX + pit.pitFarX) * 0.5f;

            Place(startLineCam, pit.Point(pit.runwayStartX - 5f, side * 1.6f) + up * 2.0f);
            Place(offTheGunCam, pit.Point(pit.runwayStartX + 6f, side * outward * 0.55f) + up * 1.7f);
            Place(railCam, pit.Point(Mathf.Clamp(jumperX, pit.runwayStartX, pit.takeoffX), side * outward) + up * railHeight);
            Place(bendCam, pit.Point(pit.takeoffX + 2.5f, side * (outward + 2.5f)) + up * 2.8f);
            Place(headOnCam, pit.Point(pit.pitFarX + 4.5f, 0f) + up * 1.6f);
            Place(finishCam, pit.Point(pit.pitFarX + 1.5f, side * (pit.HalfPit + 2.8f)) + up * 2.0f);
            Place(wideCam, pit.Point(mid, side * wideBack * 0.55f) + up * wideHeight * 0.7f);
        }

        /// <summary>Leader of the race still being run; once everyone is done, whoever got furthest.</summary>
        DashEvent.Athlete Leader(out DashEvent.Athlete faller, out int fallen, out int finished)
        {
            faller = null; fallen = 0; finished = 0;
            DashEvent.Athlete racing = null, any = null;
            foreach (var a in race.Athletes)
            {
                if (a.fell) { fallen++; if (faller == null) faller = a; }
                if (a.finished) finished++;
                if (any == null || a.distance > any.distance) any = a;
                if (a.fell || a.finished) continue;
                if (racing == null || a.distance > racing.distance) racing = a;
            }
            return racing ?? any;
        }

        Vector3 FieldCentre()
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var a in race.Athletes) { sum += Subject(a); n++; }
            return n > 0 ? sum / n : transform.position;
        }

        static Vector3 Subject(DashEvent.Athlete a)
        {
            Vector3 p = a.IsRL ? a.rig.BasePosition : (a.go != null ? a.go.transform.position : Vector3.zero);
            return p + Vector3.up * 0.25f;   // aim at the chest, not the hips
        }

        /// <summary>
        /// Arc length along the loop. Every athlete on the track already keeps one — an RL runner in its
        /// TrackFollower, the heuristic bot in the arc it integrates itself — so read it rather than
        /// searching for it. Only an athlete on neither falls through to a projection, and that one gets
        /// the global scan: <see cref="TrackPath.Project"/> searches a few metres around the value handed
        /// to it, so seeding it from the start line would park the moving cameras on the grid all race.
        /// </summary>
        float ArcOf(DashEvent.Athlete a)
        {
            if (a.follower != null) return a.follower.S;
            if (a.heuristic != null && a.heuristic.path == path) return a.heuristic.S;
            Vector3 p = a.IsRL ? a.rig.BasePosition : (a.go != null ? a.go.transform.position : Vector3.zero);
            return path.ProjectGlobal(p);
        }

        bool InBend(float s)
        {
            float L = path.StraightLength, A = path.ArcLength;
            s = Mathf.Repeat(s, path.LapLength);
            return (s >= L && s < L + A) || s >= 2f * L + A;
        }

        /// <summary>Apex of whichever bend the leader is closest to.</summary>
        float NearestBendApex(float s)
        {
            float L = path.StraightLength, A = path.ArcLength, lap = path.LapLength;
            float first = L + A * 0.5f, second = 2f * L + A + A * 0.5f;
            float d1 = Mathf.Abs(Mathf.Repeat(s - first + lap * 0.5f, lap) - lap * 0.5f);
            float d2 = Mathf.Abs(Mathf.Repeat(s - second + lap * 0.5f, lap) - lap * 0.5f);
            return d1 <= d2 ? first : second;
        }
    }
}
