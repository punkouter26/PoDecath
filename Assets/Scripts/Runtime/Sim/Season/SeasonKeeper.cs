using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoDecath.Sim
{
    /// <summary>
    /// Turns a finished race into points, personal bests, athlete cards and — when the race is a leg of a
    /// season — a line in the season table. One per race scene, listening to
    /// <see cref="RaceEvent.RaceComplete"/>; the results card reads <see cref="Awards"/> to print the
    /// points column and the PB badges, and calls <see cref="NextLeg"/> for the season's next event.
    ///
    /// Reads the race; writes only its own file. Nothing here touches a body.
    /// </summary>
    [DefaultExecutionOrder(120)]
    public class SeasonKeeper : MonoBehaviour
    {
        public RaceEvent race;

        /// <summary>What one runner got out of the race just finished.</summary>
        public struct Award
        {
            public int points;
            public bool personalBest;
            public bool firstMark;        // a PB that is also its first ever mark in this event
            public float humanEquivalent; // the performance the table scored, after conversion
        }

        public SeasonEvent Event { get; private set; }
        public Dictionary<string, Award> Awards { get; } = new Dictionary<string, Award>();

        /// <summary>True when the race just scored was a season leg (not a free race while one is paused).</summary>
        public bool ScoredForSeason { get; private set; }

        /// <summary>The race attempt the awards belong to, so a results card can tell fresh awards from stale ones.</summary>
        public int ScoredAttempt { get; private set; } = -1;

        /// <summary>Raised once the awards and the file are up to date, for the results card.</summary>
        public event Action Scored;

        readonly Dictionary<RaceEvent.Athlete, float> _topSpeed = new Dictionary<RaceEvent.Athlete, float>();
        int _lastAttempt = -1;

        void OnEnable()
        {
            if (race != null) race.RaceComplete += OnComplete;
            SeasonStore.Load();
        }

        void Start()
        {
            // The cloud copy wins if it is newer, which is what lets a season started on one phone carry on
            // on another. Quiet if the project is not linked. From Start, not OnEnable: see SeasonCloud.Connect.
            SeasonCloud.Pull();
        }

        void OnDisable()
        {
            if (race != null) race.RaceComplete -= OnComplete;
        }

        void FixedUpdate()
        {
            if (race == null) return;
            if (race.Attempt != _lastAttempt && race.Current == RaceEvent.Phase.Countdown)
            {
                _lastAttempt = race.Attempt;
                _topSpeed.Clear();
            }
            if (race.Current != RaceEvent.Phase.Running) return;
            foreach (RaceEvent.Athlete a in race.Athletes)
            {
                if (a == null || a.finished || a.fell) continue;
                _topSpeed.TryGetValue(a, out float best);
                if (a.speed > best) _topSpeed[a] = a.speed;
            }
        }

        void OnComplete(List<RaceEvent.RaceResult> results)
        {
            Event = ScoringTable.Classify(race);
            ScoringTable table = ScoringTable.Current;
            Awards.Clear();
            string when = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            // Results carry the rank and the performance; the athlete carries the rest of the story, so the
            // two are joined on the numbered name, which is unique within a field.
            var byName = new Dictionary<string, RaceEvent.Athlete>();
            foreach (RaceEvent.Athlete a in race.Athletes) if (a != null && !byName.ContainsKey(a.name)) byName[a.name] = a;

            RaceEvent.RaceResult? winner = null;
            int winnerPoints = 0;
            foreach (RaceEvent.RaceResult r in results)
            {
                float perf = Performance(r);
                int pts = table.Points(Event, perf);
                byName.TryGetValue(r.name, out RaceEvent.Athlete a);
                string athlete = a != null && !string.IsNullOrEmpty(a.definitionName) ? a.definitionName : r.name;

                SeasonStore.Card card = SeasonStore.CardFor(athlete, r.color);
                card.races++;
                if (r.finished) card.finishes++;
                if (r.finished && r.rank == 1) card.wins++;
                if (r.finished && r.rank <= 3) card.podiums++;
                if (a != null)
                {
                    if (a.fell) card.falls++;
                    card.recoveries += a.recoveries;
                    if (a.effort != null) card.joules += a.effort.Joules;
                    if (_topSpeed.TryGetValue(a, out float top)) card.topSpeed = Mathf.Max(card.topSpeed, top);
                }
                card.bestPoints = Mathf.Max(card.bestPoints, pts);

                var award = new Award { points = pts, humanEquivalent = perf > 0f ? table.HumanEquivalent(Event, perf) : 0f };
                if (perf > 0f)
                {
                    SeasonStore.Best best = card.BestFor(Event);
                    bool better = best == null || (ScoringTable.IsTimed(Event) ? perf < best.value : perf > best.value);
                    if (better)
                    {
                        award.personalBest = true;
                        award.firstMark = best == null;
                        if (best == null) { best = new SeasonStore.Best { evt = Event }; card.bests.Add(best); }
                        best.value = perf; best.points = pts; best.when = when;
                    }
                }
                Awards[r.name] = award;

                if (r.rank == 1 && r.finished) { winner = r; winnerPoints = pts; }
            }

            ScoredForSeason = RecordSeasonLeg(results);
            ScoredAttempt = race.Attempt;
            SeasonStore.Save();

            if (winner.HasValue)
                SeasonCloud.Submit(SeasonCloud.BoardId(Event), winnerPoints, winner.Value.name);
            Scored?.Invoke();
        }

        /// <summary>Seconds for a race, metres for the jump; 0 when there is nothing to score.</summary>
        float Performance(RaceEvent.RaceResult r)
        {
            if (!r.finished) return 0f;
            return ScoringTable.IsTimed(Event) ? r.time : r.distance;
        }

        /// <summary>
        /// Writes this race into the season, once, if it was launched as the season's next leg. A second
        /// run of the same event (RACE AGAIN) is a free race: the leg has been scored and the season has
        /// moved on, so it is not scored twice.
        /// </summary>
        bool RecordSeasonLeg(List<RaceEvent.RaceResult> results)
        {
            if (!SessionSettings.SeasonRace || !SeasonStore.SeasonActive) return false;
            SeasonStore.Season s = SeasonStore.Current.season;
            SeasonStore.Leg leg = s.legs[s.next];
            if (leg.evt != Event)
            {
                Debug.LogWarning($"[SeasonKeeper] season expected {leg.label}, this race is {ScoringTable.Label(Event)}; not scored for the season.", this);
                return false;
            }
            SessionSettings.SeasonRace = false;

            foreach (RaceEvent.RaceResult r in results)
            {
                RaceEvent.Athlete a = race.Athletes.Find(x => x != null && x.name == r.name);
                string athlete = a != null && !string.IsNullOrEmpty(a.definitionName) ? a.definitionName : r.name;
                SeasonStore.Standing st = SeasonStore.StandingFor(r.name, athlete, r.color);
                while (st.points.Count < s.legs.Count) st.points.Add(-1);
                st.points[s.next] = Awards.TryGetValue(r.name, out Award aw) ? aw.points : 0;
                st.total = 0;
                foreach (int p in st.points) if (p > 0) st.total += p;
            }
            s.next++;

            if (s.Finished)
            {
                List<SeasonStore.Standing> table = SeasonStore.Table();
                if (table.Count > 0)
                {
                    s.champion = table[0].name;
                    SeasonCloud.Submit(SeasonCloud.SeasonBoard, table[0].total, table[0].name);
                }
                s.active = false;
            }
            return true;
        }

        // ---------------------------------------------------------------- moving the season on

        /// <summary>Sends the season's field to its next event. False when there is no next event.</summary>
        public static bool NextLeg()
        {
            SeasonStore.Leg leg = SeasonStore.NextLeg;
            if (leg == null) return false;
            Launch(leg);
            return true;
        }

        /// <summary>Loads a leg with the season's field, flagged so the keeper there scores it for the season.</summary>
        public static void Launch(SeasonStore.Leg leg)
        {
            RaceRoster.Set(SeasonStore.Current.season.field);
            SessionSettings.SetEvent(leg.laps, leg.hurdles);
            SessionSettings.SeasonRace = true;
            SessionSettings.ApplyQuality();
            Time.timeScale = 1f;
            SceneManager.LoadScene(leg.scene);
        }
    }
}
