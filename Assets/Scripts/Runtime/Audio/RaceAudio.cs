using UnityEngine;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.Audio
{
    /// <summary>
    /// The broadcast mix: a crowd whose mood is driven by what the race is actually doing, the cues an
    /// athletics feed carries — the starter's pistol, the countdown, the bell for the last lap, a cheer for
    /// every finisher, a groan for every fall — and the wind over a rooftop that is twenty-four metres up.
    ///
    /// Nothing here is on a timeline. The crowd reads the same state the overlay does (phase, how far the
    /// leader has got, how close the first two are) and moves between a handful of moods with it, so a race
    /// that is over by halfway sounds different from one decided on the line.
    ///
    /// Two things are deliberately not done here. Footfalls belong to the athletes and live on
    /// <see cref="FootstepAudio"/>. And the crowd itself is not played from this object: it is a ring of 3D
    /// emitters round the deck (<see cref="CrowdRing"/>), so that cutting to the far bend swings the crowd
    /// round behind the camera instead of leaving it welded to the middle of the mix.
    ///
    /// Runs after <see cref="BroadcastDirector"/> so a cut is heard on the frame it happens.
    /// </summary>
    [DefaultExecutionOrder(120)]
    public class RaceAudio : MonoBehaviour
    {
        /// <summary>
        /// What the crowd is doing. These are moods, not volumes: each one has its own level, its own
        /// layers on top of the bed, and its own rate of arrival, which is what stops the crowd sounding
        /// like one noise with a fader on it.
        /// </summary>
        public enum Mood
        {
            /// <summary>Before anything: a murmur, no layers.</summary>
            Waiting,
            /// <summary>On the grid, hushing for the gun.</summary>
            Hush,
            /// <summary>The race is on and nothing is decided; the bed rises with the leader's progress.</summary>
            Building,
            /// <summary>Two athletes inside a couple of metres of each other. The rhythmic clap comes in.</summary>
            Close,
            /// <summary>Somebody is down, or somebody has won. The loudest the crowd gets.</summary>
            Roar,
            /// <summary>Settled applause after the event is decided.</summary>
            Applause,
        }

        [Header("Wiring")]
        public AudioBank bank;
        public DashEvent race;
        [Tooltip("Optional. Without it there is no whoosh under a cut, and nothing else changes.")]
        public BroadcastDirector director;
        [Tooltip("The 3D crowd. Without it the bed falls back to a single 2D source on this object.")]
        public CrowdRing crowd;
        [Tooltip("Set for the long jump; the sand thud is played from the pit.")]
        public LongJumpPit pit;

        [Header("Levels")]
        [Range(0f, 1f)] public float crowdVolume = 0.55f;
        [Range(0f, 1f)] public float sfxVolume = 0.85f;
        [Range(0f, 1f)] public float windVolume = 0.3f;
        [Tooltip("How fast the crowd follows the race. Low is a crowd that takes a moment to notice.")]
        public float crowdResponse = 1.6f;

        AudioSource _fallbackCrowd;   // only used when no ring was built
        AudioSource _chant;
        AudioSource _wind;
        AudioSource _flat;            // countdown and stings: broadcast furniture, not things in the stadium

        DashEvent.Phase _lastPhase = DashEvent.Phase.Idle;
        BroadcastDirector.Shot _lastShot;
        LongJumpEvent.Stage _lastStage;
        DashEvent.Athlete _lastFeatured;
        int _lastAttempt = -1;
        int _knownFinished, _knownFallen, _knownRecoveries, _lastBeep = -1;
        bool _bellRung;
        float _level;
        float _nextSample;
        float _excitement;
        float _chantLevel;
        Mood _mood = Mood.Waiting;
        float _moodAge;

        /// <summary>What the crowd is doing right now. Read by the telemetry overlay.</summary>
        public Mood CurrentMood => _mood;

        void Awake()
        {
            _flat = Make(spatial: false, priority: 32);
            if (crowd == null)
            {
                // No ring: a single 2D bed, which is what this used to be. Worse, but never silent.
                _fallbackCrowd = Make(spatial: false, priority: 0);
                _fallbackCrowd.clip = bank != null ? bank.crowdBed : null;
                _fallbackCrowd.loop = true;
                if (_fallbackCrowd.clip != null) _fallbackCrowd.Play();
            }
            else
            {
                crowd.bank = crowd.bank != null ? crowd.bank : bank;
            }

            if (bank != null && bank.crowdChant != null)
            {
                _chant = Make(spatial: false, priority: 16);
                _chant.clip = bank.crowdChant;
                _chant.loop = true;
                _chant.volume = 0f;
                _chant.Play();
            }
            if (bank != null && bank.windBed != null)
            {
                _wind = Make(spatial: false, priority: 12);
                _wind.clip = bank.windBed;
                _wind.loop = true;
                _wind.volume = 0f;
                _wind.Play();
            }
        }

        AudioSource Make(bool spatial, int priority)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = spatial ? 1f : 0f;
            src.priority = priority;
            src.volume = 0f;
            return src;
        }

        void Update()
        {
            if (race == null) return;
            AudioMix.Tick(Time.unscaledDeltaTime);

            if (race.Attempt != _lastAttempt)
            {
                _lastAttempt = race.Attempt;
                _knownFinished = _knownFallen = _knownRecoveries = 0;
                _bellRung = false;
            }

            Cues();
            if (Time.unscaledTime >= _nextSample)
            {
                _nextSample = Time.unscaledTime + 0.1f;
                Mood next = Decide();
                if (next != _mood) { _mood = next; _moodAge = 0f; }
                _excitement = LevelFor(_mood);
            }
            _moodAge += Time.unscaledDeltaTime;

            _level = Mathf.MoveTowards(_level, _excitement, crowdResponse * Time.unscaledDeltaTime);
            PushCrowd();
            PushChant();
            PushWind();
        }

        // ---------------------------------------------------------------- crowd

        /// <summary>
        /// Which mood the race is in. Ordered by how much it matters: an incident beats a close finish,
        /// which beats a race merely in progress.
        /// </summary>
        Mood Decide()
        {
            switch (race.Current)
            {
                case DashEvent.Phase.Idle: return Mood.Waiting;
                case DashEvent.Phase.Countdown: return Mood.Hush;
                case DashEvent.Phase.Finished: return _moodAge < 2.5f && _mood == Mood.Roar ? Mood.Roar : Mood.Applause;
            }

            // A jump competition has no field spread over a course to read; it has one competitor and a
            // stage, and the stage is the whole story.
            if (race is LongJumpEvent jump)
                return jump.CurrentStage switch
                {
                    LongJumpEvent.Stage.Flight => Mood.Roar,
                    LongJumpEvent.Stage.Settle => Mood.Applause,
                    LongJumpEvent.Stage.Approach => Mood.Building,
                    _ => Mood.Waiting,
                };

            float lead = -1f, second = -1f;
            bool anyDown = false, anyHome = false;
            foreach (DashEvent.Athlete a in race.Athletes)
            {
                if (a.fell && race.RaceTime - a.time < 2.5f) anyDown = true;
                if (a.finished && race.RaceTime - a.time < 2.5f) anyHome = true;
                float d = a.distance;
                if (d > lead) { second = lead; lead = d; }
                else if (d > second) second = d;
            }
            if (anyDown || anyHome) return Mood.Roar;

            float progress = race.raceDistance > 0f ? Mathf.Clamp01(lead / race.raceDistance) : 0f;
            // A close race is only worth clapping about once it is far enough in to mean something; two
            // runners level at ten metres is every race there has ever been.
            if (second >= 0f && Mathf.Abs(lead - second) < 2.5f && progress > 0.35f) return Mood.Close;
            return Mood.Building;
        }

        float LevelFor(Mood mood)
        {
            float progress = 0f;
            if (race.raceDistance > 0f)
            {
                float lead = 0f;
                foreach (DashEvent.Athlete a in race.Athletes) lead = Mathf.Max(lead, a.distance);
                progress = Mathf.Clamp01(lead / race.raceDistance);
            }
            return mood switch
            {
                Mood.Waiting => 0.12f,
                Mood.Hush => 0.22f,                                   // quieter than idle: they are waiting for it
                Mood.Building => 0.34f + 0.34f * progress * progress, // builds late, the way a stadium does
                Mood.Close => 0.72f + 0.18f * progress,
                Mood.Roar => 0.95f,
                _ => 0.6f,
            };
        }

        void PushCrowd()
        {
            float v = _level * crowdVolume;
            if (crowd != null) crowd.Level = _level * crowdVolume;
            else if (_fallbackCrowd != null) _fallbackCrowd.volume = v * AudioMix.Level(AudioMix.Bus.Crowd);
        }

        /// <summary>
        /// The rhythmic clap only exists in the Close mood, and it arrives and leaves slowly — a crowd
        /// takes several seconds to fall into a rhythm and rather longer to fall out of one.
        /// </summary>
        void PushChant()
        {
            if (_chant == null) return;
            float target = _mood == Mood.Close ? 0.5f : 0f;
            _chantLevel = Mathf.MoveTowards(_chantLevel, target, (target > _chantLevel ? 0.35f : 0.8f) * Time.unscaledDeltaTime);
            _chant.volume = _chantLevel * crowdVolume * AudioMix.Level(AudioMix.Bus.Crowd);
        }

        /// <summary>
        /// Wind with the camera's height above the deck. It is the cheapest possible reminder that this
        /// track is on a roof, and it does most of its work on the stadium wide, which is the shot that
        /// otherwise has nothing in it but a distant crowd.
        /// </summary>
        void PushWind()
        {
            if (_wind == null) return;
            Camera view = Camera.main;
            float height = 0f;
            if (view != null && race != null)
            {
                DashEvent.Athlete r = race.Reference;
                float ground = r != null ? (r.IsRL ? r.rig.BasePosition.y : (r.go != null ? r.go.transform.position.y : 0f)) : 0f;
                height = Mathf.Max(0f, view.transform.position.y - ground);
            }
            float t = Mathf.Clamp01(height / 30f);
            _wind.volume = Mathf.Lerp(0.25f, 1f, t) * windVolume * AudioMix.Level(AudioMix.Bus.Crowd);
        }

        // ---------------------------------------------------------------- cues

        void Cues()
        {
            DashEvent.Phase phase = race.Current;
            Vector3 line = race.startLine + Vector3.up * 1.2f;

            // The gun, from behind the grid where the starter stands. A long jump opens each attempt on the
            // same countdown and gets the same shot.
            if (phase == DashEvent.Phase.Running && _lastPhase == DashEvent.Phase.Countdown)
            {
                Vector3 at = race.startLine - race.direction.normalized * 3f + Vector3.up * 1.8f;
                Spatial(bank != null ? bank.pistol : null, at, 1f);
                AudioMix.Duck(AudioMix.Bus.Crowd, 0.55f);   // the report clears the crowd out for a moment
                AudioMix.Release(AudioMix.Bus.Crowd);
            }
            if (phase == DashEvent.Phase.Countdown)
            {
                int tick = Mathf.CeilToInt(race.Countdown);
                if (tick != _lastBeep && tick > 0 && race.Countdown > 0.05f)
                    Flat(bank != null ? bank.countBeep : null, 0.5f, AudioMix.Bus.Broadcast);
                _lastBeep = tick;
            }
            else _lastBeep = -1;

            if (phase == DashEvent.Phase.Finished && _lastPhase != DashEvent.Phase.Finished)
                Crowd(bank != null ? bank.crowdApplause : bank != null ? bank.crowdSwell : null, 1f, 0.97f);
            _lastPhase = phase;

            int finished = 0, fallen = 0, recovered = 0;
            foreach (DashEvent.Athlete a in race.Athletes)
            {
                if (a.finished) finished++;
                if (a.fell) fallen++;
                recovered += a.recoveries;
            }
            // A runner getting back onto its feet is the one thing in this event a crowd reliably makes a
            // noise about, and it is the only cue here that is applause rather than a cheer: it is
            // appreciation for still being in the race, not celebration of winning it.
            if (recovered > _knownRecoveries)
            {
                Crowd(bank != null ? bank.crowdApplause : bank.crowdSwell, 0.7f, Random.Range(0.97f, 1.05f));
                _knownRecoveries = recovered;
            }
            // The first one home gets the loudest cheer; the rest of the field gets progressively less.
            if (finished > _knownFinished)
            {
                float weight = Mathf.Lerp(1f, 0.35f, Mathf.InverseLerp(1f, 6f, _knownFinished + 1));
                Crowd(bank != null ? bank.crowdSwell : null, weight, Random.Range(0.96f, 1.05f));
                if (_knownFinished == 0) Flat(bank != null ? bank.sting : null, 0.5f, AudioMix.Bus.Broadcast);
                _knownFinished = finished;
            }
            if (fallen > _knownFallen)
            {
                Crowd(bank != null ? bank.crowdGroan : null, 0.85f, Random.Range(0.94f, 1.04f));
                _knownFallen = fallen;
            }

            Bell(line);
            JumpCues();
            Whoosh();
            LowerThirdSting();
        }

        /// <summary>
        /// The bell goes as the leader starts its last lap, which is only ever a thing in a race with more
        /// than one. A single lap has no bell, the way a 100 m has no bell.
        /// </summary>
        void Bell(Vector3 line)
        {
            if (_bellRung || !(race is LapEvent lap) || lap.laps < 2 || lap.path == null) return;
            if (race.Current != DashEvent.Phase.Running) return;
            float bellAt = (lap.laps - 1) * lap.path.LapLength;
            foreach (DashEvent.Athlete a in race.Athletes)
            {
                if (a.fell || a.distance < bellAt) continue;
                Spatial(bank != null ? bank.lapBell : null, line, 0.8f);
                _bellRung = true;
                return;
            }
        }

        /// <summary>Landing in the pit, from the pit — not from the middle of the mix.</summary>
        void JumpCues()
        {
            if (!(race is LongJumpEvent jump)) return;
            LongJumpEvent.Stage stage = jump.CurrentStage;
            if (stage == _lastStage) return;

            if (stage == LongJumpEvent.Stage.Settle && _lastStage == LongJumpEvent.Stage.Flight)
            {
                DashEvent.Athlete who = jump.Competitor;
                Vector3 at = who != null
                    ? (who.IsRL ? who.rig.BasePosition : who.go.transform.position)
                    : (pit != null ? pit.SandPoint(pit.pitNearX + 1f) : transform.position);
                Spatial(bank != null ? bank.sandThud : null, at, 0.9f, Random.Range(0.95f, 1.06f));
            }
            _lastStage = stage;
        }

        void Whoosh()
        {
            if (director == null) return;
            if (director.Current == _lastShot) return;
            _lastShot = director.Current;
            Flat(bank != null ? bank.whoosh : null, 0.28f, AudioMix.Bus.Broadcast, Random.Range(0.9f, 1.12f));
        }

        /// <summary>
        /// The stab under a lower third. It fires on a change of who is on air rather than on every cut,
        /// because the overlay's lower third does the same — a sting under a shot that names nobody new is
        /// the sound of a graphic that is not there.
        /// </summary>
        void LowerThirdSting()
        {
            if (director == null || !director.OnIndividual) return;
            DashEvent.Athlete featured = director.Featured;
            if (featured == _lastFeatured) return;
            _lastFeatured = featured;
            if (featured == null || race.Current != DashEvent.Phase.Running) return;
            Flat(bank != null ? bank.sting : null, 0.22f, AudioMix.Bus.Broadcast);
        }

        // ---------------------------------------------------------------- output

        /// <summary>A cue that happens somewhere in the stadium, played from there.</summary>
        void Spatial(AudioClip clip, Vector3 at, float volume, float pitch = 1f)
        {
            if (clip == null) return;
            SpatialCue.Play(clip, at, Mathf.Clamp01(volume) * sfxVolume, AudioMix.Bus.Sfx, pitch);
        }

        /// <summary>A cue that belongs to the broadcast rather than the stadium, so it has no position.</summary>
        void Flat(AudioClip clip, float volume, AudioMix.Bus bus, float pitch = 1f)
        {
            if (clip == null || _flat == null) return;
            _flat.pitch = pitch;
            _flat.PlayOneShot(clip, Mathf.Clamp01(volume) * sfxVolume * AudioMix.Level(bus));
        }

        /// <summary>
        /// A reaction from the crowd. Spread over the whole ring rather than played from a point, because
        /// a cheer does not have a location — but it does have a direction, and the ring gives it one.
        /// </summary>
        void Crowd(AudioClip clip, float volume, float pitch = 1f)
        {
            if (clip == null) return;
            // The ring detunes its own emitters, so a reaction spread across it needs no pitch of its own;
            // the fallback single source does, or two cheers in a row are audibly the same file twice.
            if (crowd != null) { crowd.Burst(clip, volume * crowdVolume); return; }
            if (_flat == null) return;
            _flat.pitch = pitch;
            _flat.PlayOneShot(clip, Mathf.Clamp01(volume) * crowdVolume * AudioMix.Level(AudioMix.Bus.Crowd));
        }
    }
}
