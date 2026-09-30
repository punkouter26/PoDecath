using UnityEngine;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.Audio
{
    /// <summary>
    /// The score: three looping stems — calm, drive, peak — that play for the whole scene and are never
    /// started, stopped or cut. The only thing this class ever changes is how loud each of them is.
    ///
    /// That is the whole technique, and it is chosen over switching between tracks for one reason: a
    /// race does not change mood on a bar line. Two runners start closing with eighty metres to go and
    /// the music has to already be there, in time, on the beat it would have been on anyway. Three stems
    /// written at the same tempo, in the same key and to the same length, started on the same DSP sample
    /// (<see cref="AudioSource.PlayScheduled"/> with one shared <see cref="AudioSettings.dspTime"/>), stay
    /// locked together for as long as the scene runs; fading one up is a band that was always playing
    /// getting louder, never a second piece of music arriving late.
    ///
    /// What moves the faders is state the race already publishes, the same state the crowd follows
    /// (<see cref="RaceAudio"/>), so the music and the stands never disagree about what is happening:
    ///
    /// - **Before the race** the calm stem alone, at a murmur.
    /// - **On the grid** the calm stem pulled down further. A countdown is heard by what drops out.
    /// - **After the gun** the drive stem comes in and stays in, riding <see cref="DramaMeter.Tension"/>.
    /// - **The peak stem** comes in when the tension crosses a threshold, and in the last stretch of a
    ///   race — fully if the first two are inside <see cref="closeGap"/>, part-way if the result is
    ///   already settled. A long jump gets it for the flight.
    /// - **At the finish** the peak goes first, the drive follows, and the calm stem swells for a few
    ///   seconds and settles: the resolve.
    /// - **Under a commentary line** the whole music bus is ducked, through <see cref="AudioMix"/>, and
    ///   released as the caption clears.
    ///
    /// Read only, like everything else in the broadcast layer: this class reads the event, the drama
    /// meter and the commentary caption, and writes nothing but its own three audio sources.
    ///
    /// The stems live on <see cref="AudioBank.musicCalm"/>, <see cref="AudioBank.musicDrive"/> and
    /// <see cref="AudioBank.musicPeak"/>. <c>PoDecath/Bake Audio Clips</c> synthesises a placeholder set
    /// (120 bpm, A minor, 8 bars); real stems replace them by dragging onto the bank, or by the sound pack
    /// importer from files named calm / drive / peak in a <c>music/</c> folder. They must be the same
    /// length as each other or they drift apart on every loop; this warns once if they are not.
    ///
    /// Runs after <see cref="Commentary"/> (126), so a line said this frame ducks the music this frame.
    /// </summary>
    [DefaultExecutionOrder(128)]
    public class MusicDirector : MonoBehaviour
    {
        /// <summary>What the score is doing. Named for the telemetry and the log, not used to decide anything.</summary>
        public enum Section
        {
            /// <summary>No stems in the bank, or not yet started.</summary>
            Silent,
            /// <summary>Before the countdown: the calm stem alone.</summary>
            Waiting,
            /// <summary>On the grid: calm, pulled down.</summary>
            Countdown,
            /// <summary>Running: calm and drive, with the drive riding the tension.</summary>
            Drive,
            /// <summary>Running and tight, or into the last stretch: all three.</summary>
            Peak,
            /// <summary>After the finish: peak and drive out, calm swelling and settling.</summary>
            Resolve,
        }

        const int Calm = 0, Drive = 1, Peak = 2;

        [Header("Wiring")]
        public AudioBank bank;
        public RaceEvent race;
        [Tooltip("Optional. Without it the drive and peak stems follow only the race's phase and the "
               + "leader's progress; the tension-led peak never happens.")]
        public DramaMeter drama;
        [Tooltip("Optional. With it the music is ducked under every line the commentary says. Read only: "
               + "the caption being on the picture is the signal.")]
        public Commentary commentary;

        [Header("Levels")]
        [Tooltip("The score's overall level before the Music bus. Deliberately low: the crowd is the "
               + "soundtrack of a race, and the music sits under it.")]
        [Range(0f, 1f)] public float musicVolume = 0.3f;
        [Tooltip("Calm stem level before the race, and where it settles after the resolve.")]
        [Range(0f, 1f)] public float waitingCalm = 0.55f;
        [Tooltip("Calm stem level under the countdown. Lower than waiting: the grid is heard by what drops out.")]
        [Range(0f, 1f)] public float countdownCalm = 0.28f;
        [Tooltip("How far the calm stem swells at the finish before it settles back to waitingCalm.")]
        [Range(0f, 1f)] public float resolveCalm = 0.9f;
        [Tooltip("The music bus is ducked to this while a commentary line is on the picture.")]
        [Range(0f, 1f)] public float duckUnderSpeech = 0.5f;

        [Header("When the peak comes in")]
        [Tooltip("DramaMeter tension at which the peak stem starts to come in. The meter idles at about "
               + "0.12 in a quiet race and sits at 0.45 on the grid, so this is above both.")]
        [Range(0f, 1f)] public float peakFromTension = 0.6f;
        [Tooltip("Tension at which the peak stem is fully in.")]
        [Range(0f, 1f)] public float peakFullTension = 0.85f;
        [Tooltip("Fraction of the race distance after which the leader is in the last stretch.")]
        [Range(0f, 1f)] public float lastStretch = 0.8f;
        [Tooltip("Gap in metres between the first two that makes a last stretch a close finish, and "
               + "brings the peak fully in. Same 2.5 m as DramaMeter.closeGap and RaceAudio, so the "
               + "crowd, the camera and the music agree on what close means.")]
        public float closeGap = 2.5f;

        [Header("Movement")]
        [Tooltip("Stem level per second when rising. A stem arriving over a second or so is a band "
               + "coming in; faster reads as an edit.")]
        public float rise = 0.8f;
        [Tooltip("Stem level per second when falling. Slower than rising: music that drops out the "
               + "moment a race stops being close sounds like it lost interest.")]
        public float fall = 0.4f;
        [Tooltip("Seconds the resolve takes to settle from resolveCalm back to waitingCalm.")]
        public float resolveSeconds = 7f;
        [Tooltip("Seconds between asking for the stems to start and them starting, so all three are "
               + "queued on the DSP clock before the first sample plays. Too short and one can miss it.")]
        public float scheduleLead = 0.25f;

        /// <summary>What the score is doing right now.</summary>
        public Section Current { get; private set; } = Section.Silent;

        /// <summary>A stem's level right now, 0..1, before <see cref="musicVolume"/> and the bus. 0 calm, 1 drive, 2 peak.</summary>
        public float Level(int stem) => stem >= 0 && stem < _level.Length ? _level[stem] : 0f;

        /// <summary>True once the stems have been scheduled on the DSP clock.</summary>
        public bool Started => _started;

        readonly AudioSource[] _src = new AudioSource[3];
        readonly float[] _level = new float[3];
        readonly float[] _target = new float[3];
        bool _started;
        bool _ducked;
        bool _ownsTick;
        bool _warnedLength;
        int _lastAttempt = -1;
        RaceEvent.Phase _lastPhase = RaceEvent.Phase.Idle;
        float _finishedAt = -1f;

        void Awake() => EnsureSources();

        /// <summary>
        /// The three stem voices. Also called before every start, because a script reload in play mode keeps
        /// this component but not its plain fields, and Awake does not run again: the array came back empty
        /// and TryStart threw on every frame for the rest of the session (870 times in three minutes).
        /// </summary>
        void EnsureSources()
        {
            for (int i = 0; i < _src.Length; i++)
            {
                if (_src[i] != null) continue;
                var src = gameObject.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = true;
                src.spatialBlend = 0f;
                src.priority = 4;                 // never the voice that gets culled; the whole score is three voices
                src.volume = 0f;
                src.dopplerLevel = 0f;
                // The score is not in the stadium. ListenerAcoustics puts the building's low pass and reverb
                // on the listener, and music heard through a wall is a mix fault, not realism.
                src.bypassListenerEffects = true;
                src.bypassReverbZones = true;
                _src[i] = src;
            }
        }

        void Start()
        {
            // AudioMix.Tick is owned by RaceAudio in a race. Where there is none, the duck under the
            // commentary would never release, so this ticks it instead — never both, or releases run double.
            _ownsTick = FindAnyObjectByType<RaceAudio>() == null;
        }

        void OnEnable()
        {
            _started = false;
        }

        void OnDisable()
        {
            foreach (AudioSource s in _src) if (s != null) s.Stop();
            _started = false;
            if (_ducked) { AudioMix.Release(AudioMix.Bus.Music); _ducked = false; }
            Current = Section.Silent;
        }

        void Update()
        {
            if (_ownsTick) AudioMix.Tick(Time.unscaledDeltaTime);
            if (!_started && !TryStart()) return;

            Decide();
            float dt = Time.unscaledDeltaTime;
            float bus = musicVolume * AudioMix.Level(AudioMix.Bus.Music);
            for (int i = 0; i < _src.Length; i++)
            {
                float rate = _target[i] > _level[i] ? rise : fall;
                _level[i] = Mathf.MoveTowards(_level[i], _target[i], rate * dt);
                if (_src[i] != null) _src[i].volume = _level[i] * bus;
            }
            DuckUnderSpeech();
        }

        // ---------------------------------------------------------------- start

        /// <summary>
        /// Queues all three stems on one DSP time. Waits for every clip's data first, because a stem that
        /// is still loading when its start time arrives starts late and stays late for the whole scene.
        /// </summary>
        bool TryStart()
        {
            if (bank == null || !bank.HasMusic) { Current = Section.Silent; return false; }
            AudioClip[] clips = { bank.musicCalm, bank.musicDrive, bank.musicPeak };

            foreach (AudioClip c in clips)
            {
                if (c == null) continue;
                if (c.loadState == AudioDataLoadState.Unloaded) c.LoadAudioData();
                if (c.loadState != AudioDataLoadState.Loaded)
                {
                    if (c.loadState == AudioDataLoadState.Failed) { Debug.LogWarning($"[Music] {c.name} failed to load; the score stays silent."); enabled = false; }
                    return false;
                }
            }

            WarnIfLengthsDiffer(clips);
            EnsureSources();
            double at = AudioSettings.dspTime + Mathf.Max(0.05f, scheduleLead);
            for (int i = 0; i < _src.Length; i++)
            {
                _src[i].clip = clips[i];
                _src[i].volume = 0f;
                if (clips[i] != null) _src[i].PlayScheduled(at);
            }
            _started = true;
            return true;
        }

        /// <summary>
        /// Stems of different lengths play perfectly well — and drift apart by the difference on every
        /// loop, which on a 16 s loop is audible within a minute. Said once, not fixed, because the only
        /// fix is re-cutting the files.
        /// </summary>
        void WarnIfLengthsDiffer(AudioClip[] clips)
        {
            if (_warnedLength) return;
            double first = -1.0;
            foreach (AudioClip c in clips)
            {
                if (c == null || c.frequency <= 0) continue;
                double seconds = (double)c.samples / c.frequency;
                if (first < 0.0) { first = seconds; continue; }
                if (System.Math.Abs(seconds - first) > 0.002)
                {
                    _warnedLength = true;
                    Debug.LogWarning($"[Music] stems are different lengths ({first:F3} s and {seconds:F3} s, "
                                   + $"{c.name}); they will drift apart on every loop. Cut them to the same length.");
                    return;
                }
            }
        }

        // ---------------------------------------------------------------- the faders

        /// <summary>The three target levels for this frame, from the phase first and the tension second.</summary>
        void Decide()
        {
            if (race == null)
            {
                Set(Section.Waiting, waitingCalm, 0f, 0f);
                return;
            }

            if (race.Attempt != _lastAttempt)
            {
                _lastAttempt = race.Attempt;
                _finishedAt = -1f;
            }
            RaceEvent.Phase phase = race.Current;
            if (phase == RaceEvent.Phase.Finished && _lastPhase != RaceEvent.Phase.Finished) _finishedAt = Time.unscaledTime;
            _lastPhase = phase;

            switch (phase)
            {
                case RaceEvent.Phase.Idle:
                    Set(Section.Waiting, waitingCalm, 0f, 0f);
                    return;
                case RaceEvent.Phase.Countdown:
                    Set(Section.Countdown, countdownCalm, 0f, 0f);
                    return;
                case RaceEvent.Phase.Finished:
                {
                    // The resolve: the calm stem lifts as the others leave, then settles back to where it was
                    // before the race. The fall rate takes the drive out over a couple of seconds on its own.
                    float since = _finishedAt >= 0f ? Time.unscaledTime - _finishedAt : resolveSeconds;
                    float settle = Mathf.Clamp01(since / Mathf.Max(0.1f, resolveSeconds));
                    Set(Section.Resolve, Mathf.Lerp(resolveCalm, waitingCalm, settle * settle), 0f, 0f);
                    return;
                }
            }

            // Running.
            float tension = drama != null ? drama.Tension : 0.5f;
            float progress = race.raceDistance > 0f ? Mathf.Clamp01(LeaderDistance() / race.raceDistance) : 0f;
            float closeness = drama != null && drama.LeadGap >= 0f
                ? 1f - Mathf.Clamp01(drama.LeadGap / Mathf.Max(0.01f, closeGap))
                : 0f;

            // Lateness counts in full only when there is still a race in it: a last stretch with the winner
            // twenty metres clear gets some of the peak, because it is still the end, but not all of it.
            float lateness = Mathf.InverseLerp(lastStretch, 1f, progress) * Mathf.Lerp(0.55f, 1f, closeness);
            if (race is LongJumpEvent jump && jump.CurrentStage == LongJumpEvent.Stage.Flight) lateness = 1f;

            float fromTension = Mathf.InverseLerp(peakFromTension, Mathf.Max(peakFromTension + 0.01f, peakFullTension), tension);
            float peak = Mathf.Clamp01(Mathf.Max(fromTension, lateness));
            float drive = Mathf.Lerp(0.65f, 1f, tension);
            // The pads step back as the brass comes in, so the peak is a change of colour as well as of level.
            float calm = Mathf.Lerp(0.85f, 0.55f, peak);
            Set(peak > 0.5f ? Section.Peak : Section.Drive, calm, drive, peak);
        }

        void Set(Section section, float calm, float drive, float peak)
        {
            Current = section;
            _target[Calm] = calm;
            _target[Drive] = drive;
            _target[Peak] = peak;
        }

        /// <summary>Furthest anybody has got: "how far in are we" for the whole field, as RaceAudio reads it.</summary>
        float LeaderDistance()
        {
            float lead = 0f;
            foreach (RaceEvent.Athlete a in race.Athletes) lead = Mathf.Max(lead, a.distance);
            return lead;
        }

        /// <summary>
        /// The caption being on the picture is the signal, because it is the one thing that is true on
        /// every platform: with a voice, it is on screen while the line is spoken; without one, the line is
        /// still being read, and music is still worth pulling back for it.
        /// </summary>
        void DuckUnderSpeech()
        {
            bool speaking = commentary != null && !string.IsNullOrEmpty(commentary.Caption);
            if (speaking && !_ducked)
            {
                AudioMix.Duck(AudioMix.Bus.Music, duckUnderSpeech);
                _ducked = true;
            }
            else if (!speaking && _ducked)
            {
                AudioMix.Release(AudioMix.Bus.Music);
                _ducked = false;
            }
        }
    }
}
