using System;
using UnityEngine;

namespace PoDecath.Sim
{
    /// <summary>The events a season can hold: the ones on the roof that have a scene today.</summary>
    public enum SeasonEvent { Sprint100, LongJump, Run400, Hurdles, Run1500 }

    /// <summary>
    /// Decathlon points, the way World Athletics scores them, with one honest adjustment.
    ///
    /// The official tables are a power law per event: <c>A·(B − T)^C</c> for a time T in seconds, and
    /// <c>A·(M − B)^C</c> for a mark M in centimetres. The constants below are the published ones.
    ///
    /// The adjustment: this project's athletes are policies driving a physics body at about half a human
    /// sprinter's speed. The reference athlete laps the 100 m in about 24 s, and the official table gives
    /// 0 points for anything slower than 18 s, so every race on the roof would score nothing and the
    /// season table would be a column of zeros. So each performance is first converted to a
    /// <em>human-equivalent</em> one by a single factor per event (<see cref="Entry.humanScale"/>), and the
    /// official formula is applied to that. The factors are calibrated so the reference athlete's typical
    /// performance scores about 700, which is a good club decathlete's event, and they are first guesses in
    /// the same sense as <c>referenceImpulse</c> in the tuning log: change them here, in the asset, once
    /// real races have been logged. Tick <see cref="official"/> to score the raw performance against the
    /// real table instead.
    /// </summary>
    [CreateAssetMenu(menuName = "PoDecath/Scoring Table", fileName = "ScoringTable")]
    public class ScoringTable : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public SeasonEvent evt;
            [Tooltip("True for a timed event (lower is better), false for a mark (higher is better).")]
            public bool track;
            public float A, B, C;
            [Tooltip("Performance x this = the human-equivalent performance the official formula is applied "
                   + "to. Below 1 for a timed event (the athletes are slower than people), above 1 for a mark.")]
            public float humanScale = 1f;
            [Tooltip("What the factor was calibrated against, so whoever changes it knows what it meant.")]
            public string calibratedOn;
        }

        [Tooltip("Score the raw performance against the World Athletics table, no conversion. Expect zeros: "
               + "the athletes are nowhere near human pace yet.")]
        public bool official = false;

        public Entry[] entries = Defaults();

        /// <summary>The published constants, with the human-equivalent factors this project calibrated.</summary>
        public static Entry[] Defaults() => new[]
        {
            // 100 m: 700 official points is 11.76 s. Reference lap 24.2 s (README, verified), so 11.76/24.2.
            new Entry { evt = SeasonEvent.Sprint100, track = true, A = 25.4347f, B = 18f, C = 1.81f, humanScale = 0.486f,
                        calibratedOn = "reference lap 24.2 s -> 11.76 s (700 pts)" },
            // Long jump: 700 points is 6.51 m. The scripted take-off gives the reference about 2.6 m.
            new Entry { evt = SeasonEvent.LongJump, track = false, A = 0.14354f, B = 220f, C = 1.4f, humanScale = 2.5f,
                        calibratedOn = "reference mark ~2.6 m -> 6.51 m (700 pts); guess until a take-off policy exists" },
            // 400 m: 700 points is 52.6 s. Four reference laps is about 100 s.
            new Entry { evt = SeasonEvent.Run400, track = true, A = 1.53775f, B = 82f, C = 1.81f, humanScale = 0.526f,
                        calibratedOn = "4 x 25 s laps = 100 s -> 52.6 s (700 pts); not yet measured" },
            // Hurdles: scored on the 110 m hurdles table. 700 points is 16.3 s; nobody clears a hurdle yet, so
            // a lap over them takes about 40 s with the knocks.
            new Entry { evt = SeasonEvent.Hurdles, track = true, A = 5.74352f, B = 28.5f, C = 1.92f, humanScale = 0.41f,
                        calibratedOn = "~40 s lap through hurdles -> 16.3 s (700 pts); a guess" },
            // 1500 m: 700 points is 4:37. Fifteen reference laps is about 375 s; the athletes do not tire the
            // way people do, so the factor is much closer to 1 than the sprint's.
            new Entry { evt = SeasonEvent.Run1500, track = true, A = 0.03768f, B = 480f, C = 1.85f, humanScale = 0.739f,
                        calibratedOn = "15 x 25 s laps = 375 s -> 277 s (700 pts); not yet measured" },
        };

        static ScoringTable _loaded;

        /// <summary>The table in Resources, or the defaults if the asset has not been built.</summary>
        public static ScoringTable Current
        {
            get
            {
                if (_loaded != null) return _loaded;
                _loaded = Resources.Load<ScoringTable>("ScoringTable");
                if (_loaded == null) _loaded = CreateInstance<ScoringTable>();
                return _loaded;
            }
        }

        public Entry Find(SeasonEvent evt)
        {
            foreach (Entry e in entries) if (e != null && e.evt == evt) return e;
            foreach (Entry e in Defaults()) if (e.evt == evt) return e;
            return null;
        }

        /// <summary>
        /// Points for one performance: seconds for a timed event, metres for a mark. 0 for no performance
        /// (a DNF, a no-mark) and for anything outside the table, which is how the official tables work too.
        /// </summary>
        public int Points(SeasonEvent evt, float performance)
        {
            Entry e = Find(evt);
            if (e == null || performance <= 0f || float.IsNaN(performance)) return 0;
            float human = official ? performance : performance * e.humanScale;
            double x;
            if (e.track) x = e.B - human;                   // seconds
            else x = human * 100.0 - e.B;                    // metres -> centimetres
            if (x <= 0.0) return 0;
            return (int)Math.Floor(e.A * Math.Pow(x, e.C));
        }

        /// <summary>The human-equivalent performance, for the athlete card: "runs like an 11.8 s sprinter".</summary>
        public float HumanEquivalent(SeasonEvent evt, float performance)
        {
            Entry e = Find(evt);
            return e == null || official ? performance : performance * e.humanScale;
        }

        public static bool IsTimed(SeasonEvent evt) => evt != SeasonEvent.LongJump;

        public static string Label(SeasonEvent evt) => evt switch
        {
            SeasonEvent.Sprint100 => "100 M",
            SeasonEvent.LongJump => "LONG JUMP",
            SeasonEvent.Run400 => "400 M",
            SeasonEvent.Hurdles => "HURDLES",
            SeasonEvent.Run1500 => "1500 M",
            _ => evt.ToString(),
        };

        /// <summary>Which event a race scene is running, from the event itself and the picker's settings.</summary>
        public static SeasonEvent Classify(RaceEvent race)
        {
            if (race is LongJumpEvent) return SeasonEvent.LongJump;
            if (race is LapEvent lap)
            {
                if (lap.hurdles != null && lap.hurdles.Count > 0) return SeasonEvent.Hurdles;
                if (SessionSettings.Hurdles) return SeasonEvent.Hurdles;
                if (lap.laps >= 10) return SeasonEvent.Run1500;
                if (lap.laps >= 3) return SeasonEvent.Run400;
            }
            return SeasonEvent.Sprint100;
        }

        /// <summary>Formats a performance the way the board would print it.</summary>
        public static string Format(SeasonEvent evt, float performance)
        {
            if (!IsTimed(evt)) return $"{performance:F2} m";
            if (performance < 60f) return $"{performance:F2} s";
            int m = Mathf.FloorToInt(performance / 60f);
            return $"{m}:{performance - m * 60f:00.00}";
        }
    }
}
