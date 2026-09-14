using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.Diag
{
    /// <summary>
    /// Records what the broadcast layer's guessed thresholds actually see, so they can be set from a
    /// race rather than from the number that was in the file.
    ///
    /// Three numbers were first guesses and are named as such in <c>DOCS/ROADMAP.md</c>:
    /// <c>RaceVfx.referenceImpulse</c> (45 N s: the hurdle hit that gets full sparks and full shake),
    /// <c>EffortMeter.fatigueJoules</c> (16 000 J: the work that reads as fully spent) and
    /// <c>BroadcastDirector.anticipateRisk</c> (0.72: the drama reading at which the director cuts to a
    /// runner *before* it goes down). Nothing had measured what a real hurdle knock, a real 400 m or a
    /// real fall produces. This does, and writes one JSON beside <see cref="RaceLog"/>'s:
    ///
    /// - every hurdle strike, with its impulse and whether the hurdle went over, so the reference can be
    ///   set at, say, the median topple;
    /// - each athlete's work in joules at the finish, so the fatigue scale can be set to a real race;
    /// - for every fall, the drama meter's worst risk over the two seconds before it and whether the
    ///   athlete that fell was the one it had picked — which is the anticipation threshold's ground
    ///   truth: a threshold above the typical pre-fall peak never fires, one below it fires on runners
    ///   who stay up.
    ///
    /// Reads only. It subscribes to events and polls the drama meter; nothing here changes a race.
    /// </summary>
    [DefaultExecutionOrder(210)]
    public class TuningLog : MonoBehaviour
    {
        public RaceEvent race;
        [Tooltip("Found in the scene if empty. Without one, falls are still logged, just without a risk reading.")]
        public DramaMeter drama;
        [Tooltip("Seconds of drama history kept, so a fall can be read against the risk that preceded it.")]
        public float lookBack = 2f;

        [Serializable] class Strike { public string hurdle; public float impulse; public bool toppled; public float t; }
        [Serializable] class Fall { public string athlete; public float t; public float riskBefore; public bool wasMostAtRisk; public float riskAtFall; }
        [Serializable] class Work { public string athlete; public float joules; public float fatigue; public bool finished; public bool fell; }

        [Serializable]
        class Record
        {
            public string scene, when;
            public float seconds;
            public float referenceImpulse, fatigueJoules, anticipateRisk;   // the values in force this race
            public List<Strike> strikes = new List<Strike>();
            public List<Fall> falls = new List<Fall>();
            public List<Work> work = new List<Work>();
            public string summary;
        }

        Record _rec;
        bool _recording;
        float _start;
        readonly List<Hurdle> _hurdles = new List<Hurdle>();
        readonly Dictionary<RaceEvent.Athlete, bool> _wasDown = new Dictionary<RaceEvent.Athlete, bool>();
        // Ring of (time, worst risk, most-at-risk) samples for the look-back.
        float[] _riskT = new float[0];
        float[] _risk = new float[0];
        RaceEvent.Athlete[] _riskWho = new RaceEvent.Athlete[0];
        int _head, _count;

        void OnEnable()
        {
            if (race == null) race = FindFirstObjectByType<RaceEvent>();
            if (race == null) { enabled = false; return; }
            if (drama == null) drama = FindFirstObjectByType<DramaMeter>();
            race.RaceStarted += OnStarted;
            race.RaceComplete += OnComplete;
            foreach (Hurdle h in FindObjectsByType<Hurdle>(FindObjectsSortMode.None))
            {
                h.Struck += OnStruck;
                h.KnockedOver += OnToppled;
                _hurdles.Add(h);
            }
            int n = Mathf.Max(8, Mathf.CeilToInt(lookBack * 30f));
            _riskT = new float[n]; _risk = new float[n]; _riskWho = new RaceEvent.Athlete[n];
        }

        void OnDisable()
        {
            if (race != null) { race.RaceStarted -= OnStarted; race.RaceComplete -= OnComplete; }
            foreach (Hurdle h in _hurdles) if (h != null) { h.Struck -= OnStruck; h.KnockedOver -= OnToppled; }
            _hurdles.Clear();
        }

        void OnStarted()
        {
            _rec = new Record
            {
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                when = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            var vfx = FindFirstObjectByType<Fx.RaceVfx>();
            _rec.referenceImpulse = vfx != null ? vfx.referenceImpulse : -1f;
            var director = FindFirstObjectByType<BroadcastDirector>();
            _rec.anticipateRisk = director != null ? director.anticipateRisk : -1f;
            _rec.fatigueJoules = -1f;
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                _wasDown[a] = a.fell;
                if (a.effort != null && _rec.fatigueJoules < 0f) _rec.fatigueJoules = a.effort.fatigueJoules;
            }
            _head = _count = 0;
            _start = Time.time;
            _recording = true;
        }

        void Update()
        {
            if (!_recording) return;
            float t = Time.time - _start;

            if (drama != null)
            {
                _riskT[_head] = t; _risk[_head] = drama.WorstRisk; _riskWho[_head] = drama.MostAtRisk;
                _head = (_head + 1) % _risk.Length;
                if (_count < _risk.Length) _count++;
            }

            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                _wasDown.TryGetValue(a, out bool was);
                if (a.fell && !was)
                {
                    var f = new Fall { athlete = a.name, t = t, riskAtFall = drama != null ? drama.WorstRisk : -1f };
                    f.riskBefore = -1f;
                    for (int i = 0; i < _count; i++)
                    {
                        if (t - _riskT[i] > lookBack) continue;
                        if (_risk[i] > f.riskBefore) { f.riskBefore = _risk[i]; f.wasMostAtRisk = _riskWho[i] == a; }
                    }
                    _rec.falls.Add(f);
                }
                _wasDown[a] = a.fell;
            }
        }

        void OnStruck(Hurdle h, float impulse)
        {
            if (!_recording) return;
            _rec.strikes.Add(new Strike { hurdle = h.name, impulse = impulse, toppled = false, t = Time.time - _start });
        }

        void OnToppled(Hurdle h)
        {
            if (!_recording) return;
            // The strike that toppled it was booked a moment ago with the same impulse; mark it rather
            // than add a second row.
            for (int i = _rec.strikes.Count - 1; i >= 0; i--)
                if (_rec.strikes[i].hurdle == h.name && Mathf.Approximately(_rec.strikes[i].impulse, h.LastImpulse))
                { _rec.strikes[i].toppled = true; return; }
            _rec.strikes.Add(new Strike { hurdle = h.name, impulse = h.LastImpulse, toppled = true, t = Time.time - _start });
        }

        void OnComplete(List<RaceEvent.RaceResult> results)
        {
            if (!_recording) return;
            _recording = false;
            _rec.seconds = Time.time - _start;
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                _rec.work.Add(new Work
                {
                    athlete = a.name,
                    joules = a.effort != null ? a.effort.Joules : -1f,
                    fatigue = a.effort != null ? a.effort.Fatigue : -1f,
                    finished = a.finished,
                    fell = a.fell,
                });
            }
            _rec.summary = Summarise();
            try
            {
                string json = JsonUtility.ToJson(_rec, true);
                string name = $"tuning_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                string dir = Path.Combine(Application.persistentDataPath, "races");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, name), json);
#if UNITY_EDITOR
                string projectDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "training", "logs", "races");
                Directory.CreateDirectory(projectDir);
                File.WriteAllText(Path.Combine(projectDir, name), json);
#endif
                Debug.Log($"[TuningLog] {name}: {_rec.summary}");
            }
            catch (Exception e) { Debug.LogWarning($"[TuningLog] could not write: {e.Message}"); }
        }

        /// <summary>One line that says what each threshold should probably be, from what happened.</summary>
        string Summarise()
        {
            var sb = new StringBuilder();

            var topples = new List<float>(); var clips = new List<float>();
            foreach (Strike s in _rec.strikes) (s.toppled ? topples : clips).Add(s.impulse);
            if (_rec.strikes.Count > 0)
                sb.Append($"hurdles: {clips.Count} clips (median {Median(clips):F1} N s), {topples.Count} topples "
                        + $"(median {Median(topples):F1} N s) against referenceImpulse {_rec.referenceImpulse:F0}. ");
            else sb.Append("hurdles: none struck. ");

            var joules = new List<float>();
            foreach (Work w in _rec.work) if (w.finished && w.joules >= 0f) joules.Add(w.joules);
            if (joules.Count > 0)
                sb.Append($"work: finishers median {Median(joules):F0} J, max {Max(joules):F0} J against fatigueJoules {_rec.fatigueJoules:F0}. ");
            else sb.Append("work: no finisher with a meter. ");

            if (_rec.falls.Count > 0)
            {
                var pre = new List<float>(); int picked = 0;
                foreach (Fall f in _rec.falls) { if (f.riskBefore >= 0f) pre.Add(f.riskBefore); if (f.wasMostAtRisk) picked++; }
                sb.Append($"falls: {_rec.falls.Count}, pre-fall worst risk median {Median(pre):F2} (min {Min(pre):F2}), "
                        + $"the meter had picked the faller {picked} of {_rec.falls.Count} times, against anticipateRisk {_rec.anticipateRisk:F2}.");
            }
            else sb.Append("falls: none.");
            return sb.ToString();
        }

        static float Median(List<float> v)
        {
            if (v.Count == 0) return -1f;
            var s = new List<float>(v); s.Sort();
            int n = s.Count;
            return n % 2 == 1 ? s[n / 2] : 0.5f * (s[n / 2 - 1] + s[n / 2]);
        }

        static float Max(List<float> v) { float m = float.MinValue; foreach (float x in v) if (x > m) m = x; return v.Count > 0 ? m : -1f; }
        static float Min(List<float> v) { float m = float.MaxValue; foreach (float x in v) if (x < m) m = x; return v.Count > 0 ? m : -1f; }
    }
}
