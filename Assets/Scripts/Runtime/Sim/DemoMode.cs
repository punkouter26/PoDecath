using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoDecath.Sim
{
    /// <summary>
    /// Kiosk mode: every event on the menu, run one after another with the whole roster, round and round,
    /// with nobody touching the screen. DEMO on the menu starts it; MENU (or FIELD on a results card) stops it.
    ///
    /// It drives the game through the same doors a player uses and nothing else: the roster goes in through
    /// <see cref="RaceRoster"/>, the lap count and hurdles through <see cref="SessionSettings.SetEvent"/>, the
    /// scene through LoadScene, and it moves on only once the event has finished and its results card has
    /// been on screen for <see cref="ResultsSeconds"/>. So a demo loop exercises exactly the loop a player
    /// sees, and a dead end anywhere in it shows up as a demo that stops moving (the watchdog then says so
    /// in the console and moves on anyway, because a kiosk that sticks is worse than one that skips).
    ///
    /// No scene carries a component for this. <see cref="Boot"/> hooks scene loads once per app run and adds
    /// a <see cref="DemoRunner"/> to whichever scene loads while the demo is on, so no scene needs rebuilding
    /// (a rebuild throws the baked lighting away).
    ///
    /// The flag survives an app restart (PlayerPrefs), so a kiosk phone that reboots or is killed by the OS
    /// comes back into the loop at the event it was on rather than sitting on the menu. Not in the editor:
    /// there every Play starts with the demo off, so nobody opens a scene and finds it driving itself.
    /// </summary>
    public static class DemoMode
    {
        /// <summary>One event of the loop, as the menu describes it.</summary>
        public struct Leg
        {
            public string label, scene;
            public int laps;
            public bool hurdles;
        }

        /// <summary>How long a results card stays up before the next event loads (real seconds; the demo check shortens it).</summary>
        public static float ResultsSeconds = 10f;
        /// <summary>Longest an event may take before the loop gives up on it (the 1500 m runs about 6 minutes).</summary>
        public const float WatchdogSeconds = 15f * 60f;

        const string KeyOn = "podecath.demo.on", KeyLegs = "podecath.demo.legs", KeyNames = "podecath.demo.names", KeyNext = "podecath.demo.next";

        static readonly List<Leg> _legs = new List<Leg>();
        static readonly List<string> _names = new List<string>();
        static int _index;

        public static bool Active { get; private set; }
        public static Leg Current => _legs.Count > 0 ? _legs[Mathf.Clamp(_index, 0, _legs.Count - 1)] : default;
        public static Leg Next => _legs.Count > 0 ? _legs[(_index + 1) % _legs.Count] : default;
        public static int Count => _legs.Count;
        public static int Index => _index;

        /// <summary>Set by the runner while a results card counts down to the next event; read by the frame.</summary>
        public static float SecondsToNext { get; internal set; } = -1f;

        /// <summary>Starts the loop at the first leg and loads it.</summary>
        public static void Begin(IList<Leg> legs, IList<string> names)
        {
            _legs.Clear(); _legs.AddRange(legs);
            _names.Clear(); _names.AddRange(names);
            if (_legs.Count == 0 || _names.Count == 0) { Debug.LogWarning("[Demo] nothing to run: no events or no athletes."); return; }
            _index = 0;
            Active = true;
            Save();
            Launch();
        }

        /// <summary>Stops the loop where it is. The race on screen carries on as an ordinary race.</summary>
        public static void Stop()
        {
            if (!Active) return;
            Active = false;
            SecondsToNext = -1f;
            Screen.sleepTimeout = SleepTimeout.SystemSetting;
            PlayerPrefs.SetInt(KeyOn, 0);
            PlayerPrefs.Save();
            Debug.Log("[Demo] stopped.");
        }

        /// <summary>The next event, wrapping round to the first after the last.</summary>
        public static void Advance()
        {
            // Not while a screen is loading: the load would be refused and the loop would count an event it never ran.
            if (!Active || _legs.Count == 0 || UI.SceneLoader.Busy) return;
            _index = (_index + 1) % _legs.Count;
            Save();
            Launch();
        }

        /// <summary>Loads the current leg with the whole field, the way START does.</summary>
        public static void Launch()
        {
            if (!Active) return;
            Leg leg = Current;
            RaceRoster.Set(_names);
            SessionSettings.SetEvent(leg.laps, leg.hurdles);
            SessionSettings.SeasonRace = false;   // demo races are free races: never a season leg
            SessionSettings.ApplyQuality();
            Screen.sleepTimeout = SleepTimeout.NeverSleep;   // a kiosk screen must not dim between events
            SecondsToNext = -1f;
            Prune();
            Debug.Log($"[Demo] event {_index + 1} of {_legs.Count}: {leg.label} ({_names.Count} athletes).");
            UI.SceneLoader.Load(leg.scene, leg.label);
        }

        // ---------------------------------------------------------------- persistence

        static void Save()
        {
            var legs = new List<string>();
            foreach (Leg l in _legs) legs.Add($"{l.label}\t{l.scene}\t{l.laps}\t{(l.hurdles ? 1 : 0)}");
            PlayerPrefs.SetInt(KeyOn, Active ? 1 : 0);
            PlayerPrefs.SetString(KeyLegs, string.Join("\n", legs));
            PlayerPrefs.SetString(KeyNames, string.Join("\n", _names));
            PlayerPrefs.SetInt(KeyNext, _index);
            PlayerPrefs.Save();
        }

        static void Load()
        {
            if (PlayerPrefs.GetInt(KeyOn, 0) != 1) return;
            _legs.Clear(); _names.Clear();
            foreach (string line in PlayerPrefs.GetString(KeyLegs, "").Split('\n'))
            {
                string[] f = line.Split('\t');
                if (f.Length < 4 || string.IsNullOrEmpty(f[1])) continue;
                int.TryParse(f[2], out int laps);
                _legs.Add(new Leg { label = f[0], scene = f[1], laps = laps, hurdles = f[3] == "1" });
            }
            foreach (string n in PlayerPrefs.GetString(KeyNames, "").Split('\n')) if (!string.IsNullOrEmpty(n)) _names.Add(n);
            _index = Mathf.Clamp(PlayerPrefs.GetInt(KeyNext, 0), 0, Mathf.Max(0, _legs.Count - 1));
            Active = _legs.Count > 0 && _names.Count > 0;
        }

        /// <summary>
        /// A kiosk runs all day, and every race leaves a race log, a tuning log and a telemetry summary on the
        /// device. The clips already keep only their newest twenty; this keeps the newest few hundred of the rest.
        /// </summary>
        static void Prune()
        {
            foreach (string sub in new[] { "races", "telemetry" })
            {
                try
                {
                    var dir = new DirectoryInfo(Path.Combine(Application.persistentDataPath, sub));
                    if (!dir.Exists) continue;
                    FileInfo[] files = dir.GetFiles("*.json");
                    if (files.Length <= 300) continue;
                    System.Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                    for (int i = 300; i < files.Length; i++) files[i].Delete();
                }
                catch (System.Exception e) { Debug.LogWarning($"[Demo] could not tidy {sub}: {e.Message}"); }
            }
        }

        // ---------------------------------------------------------------- boot

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            Active = false;
#if UNITY_EDITOR
            PlayerPrefs.SetInt(KeyOn, 0);
#else
            Load();
#endif
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single || !Active) return;
            new GameObject("DemoRunner").AddComponent<DemoRunner>();
        }
    }

    /// <summary>
    /// The demo's hands in one scene. In a race: waits for the event to finish and its results card to be up,
    /// holds the card for <see cref="DemoMode.ResultsSeconds"/>, then loads the next event. On the menu (the
    /// app came back up mid-loop, or DEMO was just pressed): loads the current event after a short beat.
    /// </summary>
    public class DemoRunner : MonoBehaviour
    {
        RaceEvent _race;
        float _started, _finishedAt = -1f;
        bool _left;

        void Start()
        {
            _started = Time.unscaledTime;
            _race = FindAnyObjectByType<RaceEvent>();
        }

        void Update()
        {
            if (_left || !DemoMode.Active) { DemoMode.SecondsToNext = -1f; return; }
            if (UI.SceneLoader.Busy) return;   // a screen is already on its way in

            if (_race == null)
            {
                // The menu, or a scene with no event: go (back) into the loop.
                if (Time.unscaledTime - _started > 2f) Leave(false);
                return;
            }

            if (_race.Current == RaceEvent.Phase.Finished)
            {
                // Wait for the card, unless it never comes: RaceEvent's own dead-end guard would restart the
                // race behind a missing card, and the demo should move on instead.
                if (_finishedAt < 0f) _finishedAt = Time.unscaledTime;
                float since = Time.unscaledTime - _finishedAt;
                float hold = _race.ResultsShown ? DemoMode.ResultsSeconds : DemoMode.ResultsSeconds + 5f;
                DemoMode.SecondsToNext = Mathf.Max(0f, hold - since);
                if (since >= hold) Leave(false);
                return;
            }

            // Back to a race (RESTART or AGAIN was pressed): the countdown starts again at the next finish.
            _finishedAt = -1f;
            DemoMode.SecondsToNext = -1f;
            if (Time.unscaledTime - _started > DemoMode.WatchdogSeconds) Leave(true);
        }

        void Leave(bool stuck)
        {
            _left = true;
            if (stuck) Debug.LogWarning($"[Demo] {DemoMode.Current.label} ran past {DemoMode.WatchdogSeconds / 60f:F0} min without finishing; moving on.");
            if (_race == null) DemoMode.Launch();
            else DemoMode.Advance();
        }
    }
}
