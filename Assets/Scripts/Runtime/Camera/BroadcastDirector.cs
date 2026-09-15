using UnityEngine;
using Unity.Cinemachine;
using PoDecath.Sim;

namespace PoDecath.Cam
{
    /// <summary>
    /// Cuts the race like an athletics broadcast. Seven fixed jobs, the way a real gallery is rigged:
    /// a low shot on the grid for the countdown, a tight one off the gun, a lead dolly running backwards
    /// in front of the leader down the straights — the default shot, chosen so the pack trails into
    /// frame behind whoever is winning — a high camera outside each bend, a stadium wide for incidents,
    /// a head-on from in front of the leader up the home straight, and a side-on at the line.
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
        public enum Shot { StartLine, OffTheGun, Rail, Bend, Wide, HeadOn, Finish, Hero, Cable, Reverse }

        [Header("Wiring")]
        public RaceEvent race;
        public TrackPath path;
        [Tooltip("Set for the long jump; the director then cuts the runway instead of the loop.")]
        public LongJumpPit pit;
        [Tooltip("Optional. With it the cut rate rides the race's tension and the gallery will cut to a "
               + "runner about to go down. Without it the director behaves exactly as it always did.")]
        public DramaMeter drama;

        [Header("Shots")]
        public CinemachineCamera startLineCam;
        public CinemachineCamera offTheGunCam;
        [Tooltip("The default mid-race job: a dolly on the deck running ahead of the leader, looking back "
               + "at them, so whoever is chasing trails into frame behind. Despite the name it no longer "
               + "sits on the rail — the rail shot spent the whole race side-on to the leader, which on a "
               + "portrait screen framed one athlete and dropped the rest of the field out of shot.")]
        public CinemachineCamera railCam;
        public CinemachineCamera bendCam;
        public CinemachineCamera wideCam;
        public CinemachineCamera headOnCam;
        public CinemachineCamera finishCam;
        [Tooltip("Deck-level hero shot: ankle height, just off the leader's shoulder, for a second or two "
               + "at a time. Feet filling a portrait frame is the fastest-looking picture in athletics.")]
        public CinemachineCamera heroCam;
        [Tooltip("Cable cam: high above the centre line ahead of the leader, looking back down the race. "
               + "The deep shot a portrait screen wants — the field strung out below the lens.")]
        public CinemachineCamera cableCam;
        [Tooltip("Reverse angle: behind the line, looking back at the field once the race is decided.")]
        public CinemachineCamera reverseCam;

        [Header("Cutting")]
        [Tooltip("Shortest time a shot is held before the director is allowed to cut again.")]
        public float minShotSeconds = 2.6f;
        [Tooltip("Shortest hold when the race is at full tension. A gallery cuts faster as a race gets "
               + "closer; this is what it speeds up to.")]
        public float urgentShotSeconds = 1.3f;
        [Tooltip("How long the wide shot stays on a fall before the running order resumes.")]
        public float incidentSeconds = 3f;

        [Header("Anticipation")]
        [Tooltip("Fall risk above which the gallery abandons its planned shot for whoever is in trouble — "
               + "before they are down, not after. Needs a DramaMeter; 0 turns it off.")]
        [Range(0f, 1f)] public float anticipateRisk = 0.72f;
        [Tooltip("How long the camera stays with a runner it cut to in anticipation, whether or not "
               + "anything came of it. Sometimes nothing does, and that is what a live gallery looks like.")]
        public float anticipateSeconds = 2.2f;

        [Header("Placement (metres)")]
        [Tooltip("How far outside the deck edge the trackside cameras sit. The deck is ringed by a 1 m "
               + "barrier, so a camera parked close and low just films the barrier.")]
        public float trackside = 11f;
        [Tooltip("The lead dolly's height above the deck. It now runs on the deck ahead of the leader, "
               + "so there is no barrier between it and the subject and low is what makes speed read — the "
               + "old 4.6 m was sized for shooting over the rail from outside, which this shot no longer "
               + "does. The long-jump run-up still uses it trackside, where the height still matters.")]
        public float railHeight = 2.6f;
        [Tooltip("How far ahead of the leader the lead dolly runs, in metres of track. Far enough that the "
               + "leader sits in the lower frame and everyone chasing trails into shot behind them — the "
               + "whole point of the shot on a portrait screen.")]
        public float railLead = 12f;
        public float bendHeight = 9.0f;
        public float headOnLead = 14f;
        public float wideHeight = 30f;
        public float wideBack = 40f;

        [Tooltip("Height above the deck for the shot on the grid. Like every trackside shot it has to "
               + "clear the barrier and the bunting strung along it, or half the frame is rail.")]
        public float startLineHeight = 3.4f;
        [Tooltip("Height above the deck for the tight shot off the gun.")]
        public float offTheGunHeight = 3.2f;
        [Tooltip("Height above the deck for the shot at the line. This was 2.4 m, which is chest height on "
               + "a camera parked 8.8 m outside a barrier: the rail ran across the middle of the frame and "
               + "the bunting across the top of it, for the single shot the whole race builds to.")]
        public float finishHeight = 3.6f;
        [Tooltip("Height above the deck for the head-on shot up the home straight. Lower than the rest on "
               + "purpose: running at a low camera is what makes a sprint look fast.")]
        public float headOnHeight = 2.4f;

        [Header("Movement smoothing")]
        [Tooltip("How fast the moving dollies glide along the track, per second. Without it a lead change "
               + "teleports both cameras to the new leader — the jerkiest thing in the old gallery.")]
        public float dollyGlide = 2.5f;
        [Tooltip("How fast the aim swings to a new subject, per second. A third-of-a-second swing makes a "
               + "lead change legible instead of violent.")]
        public float aimGlide = 6f;
        [Tooltip("Seconds of the leader's own speed added ahead of the dollies, so the runner sits still "
               + "in frame instead of drifting through it.")]
        public float leadLookahead = 0.3f;
        [Tooltip("Extra metres the dollies pull back while the framing nudge is active.")]
        public float widenMetres = 6f;

        [Header("Framing nudge")]
        [Tooltip("When the framing metric reports fewer than this many runners on an individual shot, the "
               + "shot pulls itself wider until the field is legible again. The metric already existed; "
               + "this is what makes it a control rather than a number nobody reads.")]
        public int minInFrame = 2;
        public bool framingNudge = true;

        [Header("Special shots")]
        [Tooltip("Seconds between hero-shot visits, on straights only. 0 disables the hero shot.")]
        public float heroEvery = 14f;
        [Tooltip("How long the hero shot stays on air once cut to.")]
        public float heroSeconds = 1.8f;
        [Tooltip("Distance ahead of the leader the hero camera sits.")]
        public float heroLead = 3.2f;
        [Tooltip("Lateral offset of the hero camera off the centre line.")]
        public float heroLateral = 1.4f;
        [Tooltip("Height of the hero camera. Ankle height on purpose.")]
        public float heroHeight = 0.5f;
        [Tooltip("Fraction of the race over which the cable cam owns the picture, before the home "
               + "straight hands over to the low head-on.")]
        public Vector2 cableWindow = new Vector2(0.58f, 0.86f);
        [Tooltip("Distance ahead of the leader the cable cam sits.")]
        public float cableLead = 12f;
        [Tooltip("Height of the cable cam above the deck.")]
        public float cableHeight = 7f;
        [Tooltip("Seconds of finish shot before the reverse angle takes over, once the race is decided.")]
        public float finishReverseAfter = 2.6f;
        [Tooltip("Degrees per second the grid shot drifts round the field during the countdown. A still "
               + "opening on a screen that is otherwise all motion reads as stuck.")]
        public float startDriftDegPerSec = 7f;

        [Header("Framing check")]
        [Tooltip("Layers that count as blocking the view of an athlete. Leave the athletes' own layer out "
               + "of it or every subject blocks itself. Read by SubjectVisible, which is a measurement "
               + "rather than a rule: nothing here changes a shot on its own.")]
        public LayerMask occlusionMask = ~0;

        // Gliding state. Both snap on a new attempt, when there is nothing on screen to swing from.
        float _focusS;                                   // smoothed arc position of the moving dollies
        Vector3 _aimSmooth;                              // smoothed position of the aim subject
        bool _snapDolly = true, _snapAim = true;
        bool _wasInBend;                                 // bend-exit cue: the rising edge schedules a cut
        float _heroTimer, _heroLeft, _finishedAge;       // special-shot clocks
        float _narrow, _widen;                           // framing nudge: seconds narrow, 0..1 pull-back

        public Shot Current { get; private set; } = Shot.StartLine;
        public string CurrentName => Current.ToString();

        /// <summary>
        /// Who the gallery is on: the leader, or whoever has just gone down while the incident is being
        /// shown, or the competitor on the runway. This is what a lower third would carry, so the broadcast
        /// overlay reads it rather than working the leader out a second time.
        /// </summary>
        public RaceEvent.Athlete Featured { get; private set; }

        /// <summary>True when the shot on air is framed on one athlete rather than on the whole field.</summary>
        public bool OnIndividual => Current != Shot.Wide && Current != Shot.StartLine;

        Transform _leaderSubject, _fieldSubject;
        float _shotAge;
        float _incidentLeft;
        float _anticipateLeft;
        RaceEvent.Athlete _watching;
        int _lastAttempt = -1;
        int _knownFallen, _knownFinished;
        float _startS, _finishS;
        // Long jump: the competitor and stage last seen, so a change of either is a cut.
        RaceEvent.Athlete _jumpCompetitor;
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
            Aim(heroCam, _leaderSubject); Aim(cableCam, _leaderSubject); Aim(reverseCam, _leaderSubject);
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
                _anticipateLeft = 0f;
                _watching = null;
                _shotAge = minShotSeconds;   // a new race may open on its own shot straight away
                _snapDolly = _snapAim = true;          // nothing on screen yet: snap instead of swing
                _heroTimer = heroEvery * 0.6f;         // first hero visit part-way into the race
                _heroLeft = 0f;
                _finishedAge = 0f;
                _narrow = _widen = 0f;
                _wasInBend = false;
            }

            RaceEvent.Athlete leader = Leader(out RaceEvent.Athlete faller, out int fallen, out int finished);
            Vector3 centroid = FieldCentre();
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

            // Who the moving cameras follow. Normally the leader; while an anticipated incident is running,
            // whoever is about to be in it.
            RaceEvent.Athlete focus = Anticipate(leader, ref cutNow) ?? leader;

            // The aim glides to whoever is on camera rather than teleporting: a lead change swings the
            // aimed cameras through a fraction of a second instead of yanking all of them at once.
            if (focus != null)
            {
                Vector3 aimTarget = Subject(focus);
                _aimSmooth = _snapAim ? aimTarget : Vector3.Lerp(_aimSmooth, aimTarget, 1f - Mathf.Exp(-aimGlide * Time.deltaTime));
                _snapAim = false;
                _leaderSubject.position = _aimSmooth;
            }
            Featured = _incidentLeft > 0f && faller != null ? faller : focus;

            // The dolly's arc position glides the same way, with wrap handling at the lap line: a raw read
            // of the leader's arc would teleport both moving cameras across the loop the moment one
            // runner overtook another.
            float rawS = focus != null ? ArcOf(focus) : _startS;
            if (_snapDolly) { _focusS = rawS; _snapDolly = false; }
            else
            {
                float lap = path.LapLength;
                float d = Mathf.Repeat(rawS - _focusS + lap * 0.5f, lap) - lap * 0.5f;
                _focusS += d * (1f - Mathf.Exp(-dollyGlide * Time.deltaTime));
            }
            float focusS = _focusS;

            // Leaving a bend is the director's cue: the high outside shot has done its job, so hand off
            // to the lead dolly on the beat instead of waiting out the hold.
            bool inBendNow = InBend(focusS);
            if (_wasInBend && !inBendNow) cutNow = true;
            _wasInBend = inBendNow;

            // The hero shot visits on a timer, on straights only: a deck-level camera at ankle height
            // once or twice a lap, never through the fights in the bends.
            if (heroCam != null && heroEvery > 0f && race.Current == RaceEvent.Phase.Running)
            {
                _heroTimer -= Time.deltaTime;
                if (_heroTimer <= 0f && !inBendNow && focus != null)
                {
                    _heroLeft = heroSeconds;
                    _heroTimer = heroEvery;
                    cutNow = true;
                }
            }
            if (_heroLeft > 0f) _heroLeft -= Time.deltaTime;

            if (race.Current == RaceEvent.Phase.Finished) _finishedAge += Time.deltaTime;
            else _finishedAge = 0f;

            PlaceCameras(focusS, focus);

            Shot want = Choose(focus, finished, focusS);
            _shotAge += Time.deltaTime;
            if (want != Current && (cutNow || _shotAge >= HoldSeconds))
            {
                // Planned cuts land on a footstrike — the stride clock the policy already runs — so the
                // picture changes when the eye expects it. Incidents cut immediately: a fall does not wait
                // for a stride. A missed window pins the hold just under the threshold so the cut retries
                // the next frame rather than slipping a full hold later.
                bool onFootstrike = true;
                if (!cutNow && focus != null && focus.runner != null)
                {
                    float half = focus.runner.StridePhase % 0.5f;
                    onFootstrike = half < 0.05f || half > 0.45f;
                    if (!onFootstrike) _shotAge = HoldSeconds - 0.05f;
                }
                if (onFootstrike)
                {
                    Current = want;
                    _shotAge = 0f;
                }
            }
            Apply();

            // The framing metric becomes a control: an individual shot that has held fewer than
            // minInFrame runners for over a second pulls itself back until the field is legible again.
            if (framingNudge && OnIndividual)
                _narrow = AthletesInFrame() < minInFrame ? _narrow + Time.deltaTime : Mathf.Max(0f, _narrow - Time.deltaTime * 2f);
            else
                _narrow = 0f;
            _widen = Mathf.MoveTowards(_widen, _narrow > 1f ? 1f : 0f, Time.deltaTime * 1.5f);
        }

        /// <summary>
        /// How long the current shot has to be held before the director may cut again.
        ///
        /// A gallery cuts slowly through a settled middle and quickly through a finish, and it does not do
        /// that on a timer — it does it because there is more happening. With a <see cref="DramaMeter"/>
        /// wired, the hold slides between the two bounds with the race's tension; without one it is the
        /// fixed hold it always was.
        /// </summary>
        float HoldSeconds =>
            drama != null ? Mathf.Lerp(minShotSeconds, urgentShotSeconds, drama.Tension) : minShotSeconds;

        /// <summary>
        /// Cutting to a runner that is about to go down, a beat before it does.
        ///
        /// This is the one thing on this class that a gallery working purely from "what has happened"
        /// cannot do, and it is worth the complexity because the shot it produces — a tight shot already on
        /// the athlete as it loses the bend — is the shot the whole event is about. The risk comes from
        /// <see cref="DramaMeter"/>, which builds it out of uprightness and lateral acceleration; the
        /// second of those is what gives the warning, because a runner cornering above about 0.35 g on this
        /// track is in trouble well before it is visibly tilted.
        ///
        /// Nothing is cut back early if the runner recovers: the camera stays with it for
        /// <see cref="anticipateSeconds"/> either way. A gallery that snapped away the instant an athlete
        /// steadied would look like it was reading the future rather than watching the race.
        ///
        /// Returns the athlete to follow, or null to follow the leader as usual.
        /// </summary>
        RaceEvent.Athlete Anticipate(RaceEvent.Athlete leader, ref bool cutNow)
        {
            if (_anticipateLeft > 0f)
            {
                _anticipateLeft -= Time.deltaTime;
                // Once it is actually down the incident path owns the shot, and holding on to it here would
                // fight the wide shot that the fall is supposed to cut to.
                if (_watching != null && !_watching.fell && !_watching.finished) return _watching;
                _anticipateLeft = 0f;
                _watching = null;
            }

            if (drama == null || anticipateRisk <= 0f) return null;
            RaceEvent.Athlete risky = drama.MostAtRisk;
            if (risky == null || drama.WorstRisk < anticipateRisk) return null;
            if (risky == leader) return null;           // already on camera
            if (risky.fell || risky.finished) return null;

            _watching = risky;
            _anticipateLeft = anticipateSeconds;
            cutNow = true;
            return risky;
        }

        Shot Choose(RaceEvent.Athlete leader, int finished, float leaderS)
        {
            if (race.Current == RaceEvent.Phase.Countdown || race.Current == RaceEvent.Phase.Idle) return Shot.StartLine;
            if (race.Current == RaceEvent.Phase.Finished)
                return reverseCam != null && _finishedAge > finishReverseAfter ? Shot.Reverse : Shot.Finish;
            if (_incidentLeft > 0f) return Shot.Wide;
            if (leader == null) return Shot.Finish;

            float f = race.raceDistance > 0f ? leader.distance / race.raceDistance : 0f;
            if (f < 0.07f) return Shot.OffTheGun;            // away from the line
            if (finished > 0 || f > 0.94f) return Shot.Finish;
            if (_heroLeft > 0f && heroCam != null) return Shot.Hero;
            if (f > cableWindow.y) return Shot.HeadOn;       // home straight: low, tight, at the leader
            if (f >= cableWindow.x && cableCam != null) return Shot.Cable;
            if (InBend(leaderS)) return Shot.Bend;
            return Shot.Rail;                                // the default job: in front of the leader, pack behind in shot
        }

        void PlaceCameras(float leaderS, RaceEvent.Athlete focus)
        {
            float outward = Outward;
            Vector3 up = Vector3.up;
            // Lookahead: the dollies sit a fraction of a second of the leader's own speed further ahead,
            // so the runner holds their place in frame instead of drifting through it.
            float look = focus != null ? Mathf.Max(0f, focus.speed) * leadLookahead : 0f;
            float extra = _widen * widenMetres;   // the framing nudge pulling the shot wider

            // The grid shot drifts slowly round the field through the countdown instead of sitting
            // locked-off: a still opening on a screen that is otherwise all motion reads as stuck.
            float a = Time.time * startDriftDegPerSec * Mathf.Deg2Rad;
            Vector3 grid = path.Position(_startS, 0f);
            Place(startLineCam, grid + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (path.deckWidth * 1.15f) + up * startLineHeight);
            Place(offTheGunCam, path.Position(_startS + 7f, outward * 0.7f) + up * offTheGunHeight);
            // The lead dolly: on the deck, ahead of the leader, looking back down the race. Centre line so
            // nobody's lane puts them out of frame; ahead so the field chases into shot behind the leader.
            Place(railCam, path.Position(leaderS + railLead + look + extra, 0f) + up * (railHeight + extra * 0.2f));
            Place(bendCam, path.Position(NearestBendApex(leaderS), outward + 3f) + up * bendHeight);
            Place(headOnCam, path.Position(leaderS + headOnLead + look, 0f) + up * headOnHeight);
            Place(finishCam, path.Position(_finishS, outward * 0.8f) + up * finishHeight);
            // Hero: ankle height just off the shoulder. Cable: high above the centre line ahead of the
            // race, the deep shot. Reverse: behind the line, looking back once it is decided.
            Place(heroCam, path.Position(leaderS + heroLead, heroLateral) + up * heroHeight);
            Place(cableCam, path.Position(leaderS + cableLead, 0f) + up * cableHeight);
            Place(reverseCam, path.Position(_finishS + 4.5f, 0f) + up * 1.7f);

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

        /// <summary>
        /// Whether the athlete on air can actually be seen from the live camera: inside the frustum, and
        /// with nothing on <see cref="occlusionMask"/> across the line to it.
        ///
        /// This exists because the gallery had no idea. It aims correctly and places correctly and then
        /// has no way to answer "is the shot any good", so a camera framing a barrier and a camera framing
        /// a race look identical from in here. The scene sweep reads it, which turns "the finish shot looks
        /// wrong" into a number that can be compared before and after a change.
        ///
        /// A measurement, deliberately: nothing in this class acts on it. Rejecting shots automatically is
        /// a much bigger behavioural change than raising four camera heights, and it should be made with
        /// this reading in hand rather than instead of it.
        /// </summary>
        public bool SubjectVisible
        {
            get
            {
                Camera cam = Camera.main;
                if (cam == null || Featured == null) return false;
                Vector3 p = Subject(Featured);
                Vector3 vp = cam.WorldToViewportPoint(p);
                if (vp.z <= 0f || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) return false;
                return !Physics.Linecast(cam.transform.position, p, occlusionMask, QueryTriggerInteraction.Ignore);
            }
        }

        /// <summary>
        /// How many of the field are inside the live camera's frustum right now. On a portrait screen this
        /// is the number that says whether a shot is a race or a close-up: measured on the shipped gallery
        /// it was 2 of 11 at the finish, because the lens angles were authored as vertical ones.
        /// See <see cref="PortraitLens"/>.
        /// </summary>
        public int AthletesInFrame()
        {
            Camera cam = Camera.main;
            if (cam == null || race == null) return 0;
            int n = 0;
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (a == null || !a.IsRL) continue;
                Vector3 vp = cam.WorldToViewportPoint(Subject(a));
                if (vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f) n++;
            }
            return n;
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
            SetPriority(heroCam, Shot.Hero);
            SetPriority(cableCam, Shot.Cable);
            SetPriority(reverseCam, Shot.Reverse);
        }

        void SetPriority(CinemachineCamera cam, Shot shot)
        {
            if (cam != null) cam.Priority = Current == shot ? 30 : 10;
        }

        // ---------------------------------------------------------------- long jump

        void DirectJump(LongJumpEvent jump)
        {
            RaceEvent.Athlete who = jump.Competitor;
            Featured = who;
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
            if (want != Current && (cutNow || _shotAge >= HoldSeconds))
            {
                Current = want;
                _shotAge = 0f;
            }
            Apply();
        }

        Shot ChooseJump(LongJumpEvent jump, float x)
        {
            if (race.Current == RaceEvent.Phase.Finished) return Shot.Wide;
            if (race.Current != RaceEvent.Phase.Running || jump.Competitor == null) return Shot.StartLine;
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
        RaceEvent.Athlete Leader(out RaceEvent.Athlete faller, out int fallen, out int finished)
        {
            faller = null; fallen = 0; finished = 0;
            RaceEvent.Athlete racing = null, any = null;
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

        static Vector3 Subject(RaceEvent.Athlete a)
        {
            Vector3 p = a.IsRL ? a.rig.BasePosition : (a.go != null ? a.go.transform.position : Vector3.zero);
            return p + Vector3.up * 0.25f;   // aim at the chest, not the hips
        }

        /// <summary>
        /// Arc length along the loop. Every athlete on the track already keeps one in its TrackFollower,
        /// so read it rather than searching for it. Only an athlete without one falls through to a
        /// projection, and that one gets
        /// the global scan: <see cref="TrackPath.Project"/> searches a few metres around the value handed
        /// to it, so seeding it from the start line would park the moving cameras on the grid all race.
        /// </summary>
        float ArcOf(RaceEvent.Athlete a)
        {
            if (a.follower != null) return a.follower.S;
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
