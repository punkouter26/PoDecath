using System;
using System.Collections.Generic;
using UnityEngine;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.Audio
{
    /// <summary>
    /// Somebody calling the race.
    ///
    /// Almost none of this is new information. The event already knows the running order, the splits, who
    /// went down and where; <see cref="DramaMeter"/> already knows whether a gap is closing;
    /// <see cref="EffortMeter"/> already knows who is straining. What was missing was anyone saying it out
    /// loud, which is the difference between a simulation you watch and a race you follow.
    ///
    /// Two design decisions carry the whole thing.
    ///
    /// **It is a priority slot, not a queue.** Commentary that queues is commentary that is always talking
    /// about the last thing but one. A candidate line is offered with a priority; if it beats whatever is
    /// waiting, it replaces it; when the gap since the last line has elapsed, the winner is said and
    /// everything else is thrown away. A fall interrupts a split. A split never interrupts anything.
    ///
    /// **Lines expire.** A candidate older than <see cref="staleSeconds"/> is dropped unsaid, because "and
    /// there goes the lead" is worse than silence four seconds after the lead went.
    ///
    /// The caption is always produced, whether or not a voice exists to speak it — see
    /// <see cref="SpeechSynth"/> for which platforms have one. Nothing here knows or cares.
    ///
    /// Names, not pronouns, wherever a sentence can carry them, and "they" where it cannot: the roster is
    /// eight imported models and a coded bot, and the game has never been told what any of them are.
    /// </summary>
    [DefaultExecutionOrder(126)]
    public class Commentary : MonoBehaviour
    {
        [Header("Wiring")]
        public RaceEvent race;
        [Tooltip("Optional. Without it the colour lines about a race tightening never fire.")]
        public DramaMeter drama;
        [Tooltip("Optional. Without it the commentary is captions only, which is the state on every "
               + "platform that has no system voice.")]
        public SpeechSynth voice;

        [Header("Pacing")]
        [Tooltip("Shortest gap between two lines. A caller who never stops is worse than one who misses "
               + "things.")]
        public float minGapSeconds = 2.2f;
        [Tooltip("A line not said within this long of being thought of is dropped unsaid.")]
        public float staleSeconds = 3.5f;
        [Tooltip("How long a caption stays on the picture after it is said.")]
        public float captionSeconds = 3.4f;
        [Tooltip("Samples a second of the race state. Events that arrive on their own — a hurdle going "
               + "over — do not wait for this.")]
        public float sampleHz = 5f;

        [Header("Mix")]
        [Tooltip("The crowd is pulled down to this while a line is being said, then released.")]
        [Range(0f, 1f)] public float duckCrowdTo = 0.45f;

        /// <summary>The line on the picture right now, or empty when there is none.</summary>
        public string Caption { get; private set; } = "";

        /// <summary>Raised on every line as it is said, for anything that wants to log or display it.</summary>
        public event Action<string> Said;

        // ---------------------------------------------------------------- the waiting line

        string _pendingText = "";
        string _pendingCategory = "";
        int _pendingPriority;
        float _pendingAt;

        float _lastSaid = -999f;
        float _captionUntil;
        float _next;
        bool _ducked;

        readonly Dictionary<string, float> _categoryLast = new Dictionary<string, float>();
        readonly List<Hurdle> _hurdles = new List<Hurdle>();

        // Remembered race state, so a change can be spotted without the event having to announce it.
        int _lastAttempt = -1;
        RaceEvent.Phase _lastPhase = RaceEvent.Phase.Idle;
        RaceEvent.Athlete _lastLeader;
        int _knownFinished, _knownFallen, _knownRecoveries, _splitsCalled;
        bool _bellCalled;
        string _lastStory = "";
        LongJumpEvent.Stage _lastStage;

        LapEvent Lap => race as LapEvent;
        LongJumpEvent Jump => race as LongJumpEvent;

        void Start() => HookHurdles();

        void OnDestroy()
        {
            foreach (Hurdle h in _hurdles) if (h != null) h.KnockedOver -= OnHurdleKnocked;
        }

        /// <summary>
        /// Hurdles are built after the scene loads, so they are collected here rather than wired by the
        /// builder — the same arrangement <c>RaceVfx</c> uses, and for the same reason.
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
                _knownFinished = _knownFallen = _knownRecoveries = _splitsCalled = 0;
                _bellCalled = false;
                _lastLeader = null;
                _lastStory = "";
                _categoryLast.Clear();
                Drop();
                if (_hurdles.Count == 0) HookHurdles();
            }

            if (Time.unscaledTime >= _next)
            {
                _next = Time.unscaledTime + 1f / Mathf.Max(1f, sampleHz);
                Watch();
            }

            Speak();
            Captions();
        }

        // ---------------------------------------------------------------- what is worth saying

        /// <summary>
        /// Everything the commentary notices by looking, in the order a caller would notice it. Each block
        /// offers a line; the slot decides which one actually gets said.
        /// </summary>
        void Watch()
        {
            Phases();
            if (race.Current != RaceEvent.Phase.Running) return;

            Incidents();
            if (Jump != null) { JumpStages(); return; }

            LeadChanges();
            Splits();
            Colour();
        }

        void Phases()
        {
            RaceEvent.Phase phase = race.Current;
            if (phase == _lastPhase) return;
            RaceEvent.Phase was = _lastPhase;
            _lastPhase = phase;

            if (phase == RaceEvent.Phase.Countdown)
            {
                if (Jump != null)
                {
                    RaceEvent.Athlete who = Jump.Competitor;
                    Offer(who != null
                        ? $"Round {Jump.Round}. {who.name} on the runway."
                        : $"Round {Jump.Round}.", 90, "phase");
                    return;
                }
                Offer($"{race.Athletes.Count} on the line for the {race.raceDistance:F0} metres.", 90, "phase");
                return;
            }

            if (phase == RaceEvent.Phase.Running && was == RaceEvent.Phase.Countdown)
            {
                Offer(Jump != null ? "And away." : "And they're away.", 95, "phase");
                return;
            }

            if (phase == RaceEvent.Phase.Finished) Verdict();
        }

        /// <summary>
        /// Falls, recoveries and finishers. These are the lines that beat everything else, because they
        /// are the only ones that describe something irreversible.
        /// </summary>
        void Incidents()
        {
            int finished = 0, fallen = 0, recoveries = 0;
            RaceEvent.Athlete newFaller = null, newFinisher = null;

            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (a.fell) { fallen++; if (newFaller == null || a.fellAt > newFaller.fellAt) newFaller = a; }
                if (a.finished) { finished++; newFinisher = a; }
                recoveries += a.recoveries;
            }

            if (fallen > _knownFallen)
            {
                _knownFallen = fallen;
                if (newFaller != null) Offer($"{newFaller.name} is down, at {newFaller.distance:F0} metres.", 98, "fall");
            }
            if (recoveries > _knownRecoveries)
            {
                _knownRecoveries = recoveries;
                RaceEvent.Athlete up = MostRecentlyUp();
                Offer(up != null
                    ? $"{up.name} is back on their feet, and still in this."
                    : "Back up, and still in this.", 92, "recovery");
            }
            if (finished > _knownFinished)
            {
                int place = _knownFinished + 1;
                _knownFinished = finished;
                if (newFinisher == null) return;
                Offer(place == 1
                    ? $"{newFinisher.name} takes it, in {newFinisher.time:F2}."
                    : $"{Ordinal(place)} for {newFinisher.name}.", place == 1 ? 96 : 78, "finish");
            }
        }

        RaceEvent.Athlete MostRecentlyUp()
        {
            RaceEvent.Athlete best = null;
            foreach (RaceEvent.Athlete a in race.Athletes)
                if (a.recovering && (best == null || a.recoveries > best.recoveries)) best = a;
            return best;
        }

        /// <summary>
        /// A change at the front. The single most worth-saying thing in a race that nobody has fallen out
        /// of, and the running order the overlay already animates is where it comes from.
        /// </summary>
        void LeadChanges()
        {
            List<RaceEvent.Athlete> order = race.LiveOrder();
            if (order.Count == 0) return;
            RaceEvent.Athlete leader = order[0];

            if (_lastLeader == null) { _lastLeader = leader; return; }
            if (leader == _lastLeader) return;

            RaceEvent.Athlete passed = _lastLeader;
            _lastLeader = leader;
            // A "lead change" caused by the old leader falling over has already been called as a fall, and
            // saying both makes the commentary sound like it did not see the first one.
            if (passed.fell || passed.finished) return;
            Offer($"{leader.name} goes past {passed.name} and into the lead.", 85, "lead", 4f);
        }

        /// <summary>The bell, and a split per lap, on a race that has more than one.</summary>
        void Splits()
        {
            LapEvent lap = Lap;
            if (lap == null || lap.path == null || lap.laps <= 1) return;

            float lapLength = lap.path.LapLength;
            float lead = 0f;
            RaceEvent.Athlete leader = null;
            foreach (RaceEvent.Athlete a in race.Athletes)
                if (!a.fell && a.distance > lead) { lead = a.distance; leader = a; }
            if (leader == null) return;

            if (!_bellCalled && lead >= (lap.laps - 1) * lapLength)
            {
                _bellCalled = true;
                Offer("The bell. One lap to go.", 88, "bell");
                return;
            }

            int done = Mathf.FloorToInt(lead / lapLength);
            if (done <= _splitsCalled || done >= lap.laps) return;
            _splitsCalled = done;
            Offer($"{leader.name} through {done} in {race.RaceTime:F1}.", 55, "split", 6f);
        }

        /// <summary>
        /// Colour: the lines that fill the gaps a real caller fills. Lowest priority in the file, on long
        /// cooldowns, and every one of them is a fact the simulation measured rather than a flourish.
        /// </summary>
        void Colour()
        {
            if (drama != null && !string.IsNullOrEmpty(drama.Story) && drama.Story != _lastStory)
            {
                _lastStory = drama.Story;
                string line = StoryLine(drama.Story);
                if (!string.IsNullOrEmpty(line)) { Offer(line, 60, "story", 5f); return; }
            }

            // Whoever is straining hardest, if anyone is straining enough to be worth remarking on. This is
            // the joint-torque reading, not a guess from their speed, which is why it can be true of the
            // athlete in second rather than only of the one in front.
            RaceEvent.Athlete worker = null;
            float worst = 0.68f;
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (a.effort == null || a.fell || a.finished) continue;
                if (a.effort.Effort <= worst) continue;
                worst = a.effort.Effort;
                worker = a;
            }
            if (worker != null)
            {
                Offer(worker.effort.Fatigue > 0.6f
                    ? $"{worker.name} is having to work for this now."
                    : $"{worker.name} is putting everything through the legs there.", 40, "effort", 9f);
                return;
            }

            RaceEvent.Athlete leader = _lastLeader;
            if (leader != null && !leader.finished && !leader.fell && leader.speed > 0.5f)
                Offer($"{leader.name}, {leader.speed:F1} metres a second.", 30, "pace", 11f);
        }

        /// <summary>The drama meter's shorthand, turned into something a person would actually say.</summary>
        static string StoryLine(string story) => story switch
        {
            "nothing in it" => "There is nothing in this.",
            "strung out" => "It has strung out rather.",
            "into the last of it" => "Into the last of it now.",
            "incident on the track" => "",   // the fall itself has already been called
            "on the marks" => "",
            _ => story.EndsWith("is closing", StringComparison.Ordinal)
                 || story.EndsWith("is in trouble", StringComparison.Ordinal)
                 ? Capitalise(story) + "."
                 : "",
        };

        void JumpStages()
        {
            LongJumpEvent jump = Jump;
            LongJumpEvent.Stage stage = jump.CurrentStage;
            if (stage == _lastStage) return;
            LongJumpEvent.Stage was = _lastStage;
            _lastStage = stage;

            RaceEvent.Athlete who = jump.Competitor;
            if (stage == LongJumpEvent.Stage.Flight) { Offer("Up, and into the pit.", 80, "jump"); return; }
            if (stage != LongJumpEvent.Stage.Settle || was != LongJumpEvent.Stage.Flight) return;
            Offer(who != null ? $"{who.name} down at {who.distance:F2}." : "Down in the sand.", 86, "jump");
        }

        /// <summary>The line at the end of the event, once the board has been built.</summary>
        void Verdict()
        {
            if (race.Results.Count == 0) { Offer("And that is that.", 80, "verdict"); return; }
            RaceEvent.RaceResult top = race.Results[0];
            int finishers = 0;
            foreach (RaceEvent.RaceResult r in race.Results) if (r.finished) finishers++;

            if (Jump != null)
            {
                Offer($"{top.name} wins it with {top.distance:F2} metres.", 92, "verdict");
                return;
            }
            Offer(top.finished
                ? $"{top.name} wins in {top.time:F2}, with {finishers} of {race.Results.Count} home."
                : "Nobody got home.", 92, "verdict");
        }

        void OnHurdleKnocked(Hurdle h)
        {
            if (h == null || race == null || race.Current != RaceEvent.Phase.Running) return;
            RaceEvent.Athlete by = Owner(h);
            Offer(by != null ? $"{by.name} takes the hurdle with them." : "And a hurdle goes down.",
                  72, "hurdle", 2.5f);
        }

        /// <summary>Which athlete knocked a hurdle over, matched back through the body that hit it.</summary>
        RaceEvent.Athlete Owner(Hurdle h)
        {
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (h.ByRig != null && a.rig == h.ByRig) return a;
            }
            return null;
        }

        // ---------------------------------------------------------------- the slot

        /// <summary>
        /// Puts a line forward. It is kept only if it beats whatever is already waiting, and only if this
        /// category has not been used inside its own cooldown — which is what stops a field of sixteen
        /// turning the commentary into a list of who is working hard.
        /// </summary>
        void Offer(string text, int priority, string category, float categoryCooldown = 0f)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (categoryCooldown > 0f && _categoryLast.TryGetValue(category, out float last)
                && Time.unscaledTime - last < categoryCooldown) return;
            if (priority <= _pendingPriority && !string.IsNullOrEmpty(_pendingText)) return;

            _pendingText = text;
            _pendingPriority = priority;
            _pendingCategory = category;
            _pendingAt = Time.unscaledTime;
        }

        void Drop()
        {
            _pendingText = "";
            _pendingCategory = "";
            _pendingPriority = 0;
        }

        void Speak()
        {
            if (string.IsNullOrEmpty(_pendingText)) { ReleaseDuck(); return; }

            // Thought of too long ago to still be true.
            if (Time.unscaledTime - _pendingAt > staleSeconds) { Drop(); return; }
            if (Time.unscaledTime - _lastSaid < minGapSeconds) return;

            string text = _pendingText;
            string category = _pendingCategory;
            Drop();

            _lastSaid = Time.unscaledTime;
            _categoryLast[category] = Time.unscaledTime;
            Caption = text;
            _captionUntil = Time.unscaledTime + captionSeconds;

            if (voice != null) voice.Say(text);
            // The crowd comes down under the line and is released as the caption clears. RaceAudio ticks
            // the mix, so the release is a slow one rather than a snap back to full.
            AudioMix.Duck(AudioMix.Bus.Crowd, duckCrowdTo);
            _ducked = true;

            Said?.Invoke(text);
        }

        void Captions()
        {
            if (Caption.Length == 0 || Time.unscaledTime < _captionUntil) return;
            Caption = "";
            ReleaseDuck();
        }

        void ReleaseDuck()
        {
            if (!_ducked) return;
            _ducked = false;
            AudioMix.Release(AudioMix.Bus.Crowd);
        }

        static string Capitalise(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        static string Ordinal(int n) => n switch
        {
            1 => "First", 2 => "Second", 3 => "Third", 4 => "Fourth", 5 => "Fifth",
            6 => "Sixth", 7 => "Seventh", 8 => "Eighth", _ => $"{n}th",
        };
    }
}
