using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// The event setup menu: a row of event buttons, then one counter per athlete definition including the
    /// RED heuristic bot, capped at <see cref="RaceRoster.MaxRunners"/> in total and at least one athlete
    /// overall.
    ///
    /// The grid order interleaves the types round-robin rather than listing each block in turn. The
    /// starting grid is staggered two abreast (the deck is far too narrow to line a full field up in one
    /// row), so a block layout would drop one whole policy into the back rows and make it look beaten from
    /// the gun even though every runner covers the same lap. Interleaving spreads both policies evenly down
    /// the grid; the times in the results are the comparison that counts either way.
    ///
    /// Every button and counter is built from the definitions the project actually has, so a policy that
    /// has never been trained and an event whose scene was never built simply do not appear.
    /// </summary>
    public class SetupView : UiRoot
    {
        /// <summary>One event the menu can send the field to. The first entry is the default.</summary>
        [Serializable]
        public class EventChoice
        {
            public string label;
            public string sceneName;
            [Tooltip("One line under the title explaining what the field is about to do.")]
            public string hint;
            [Tooltip("Laps of the rooftop loop. The lap is 100.1 m, so 1 is the 100 m, 4 the 400 m and "
                   + "15 the 1500 m — all the same scene. 0 leaves the scene's own setting alone.")]
            public int laps = 0;
            [Tooltip("Put hurdles on the straights.")]
            public bool hurdles = false;
        }

        [Serializable]
        public class RunnerRow
        {
            public AthleteDefinition definition;
            [NonSerialized] public int count;
            [NonSerialized] public Label countLabel;
            [NonSerialized] public Button minus, plus;
        }

        public List<RunnerRow> rows = new List<RunnerRow>();
        [Tooltip("Events on offer, built by PoDecath/Build Race Scenes. The selected one owns the scene "
               + "START loads and the hint under the title.")]
        public List<EventChoice> events = new List<EventChoice>();

        VisualElement _eventHost, _runnerHost;
        Label _hint, _total;
        Button _start;
        readonly List<Button> _eventButtons = new List<Button>();
        int _event;

        /// <summary>The scene START loads: whichever event is selected.</summary>
        string SceneName => events.Count > 0 ? events[_event].sceneName : null;

        int Max => RaceRoster.MaxRunners;

        protected override void Build()
        {
            SessionSettings.Load();
            SessionSettings.VisitedMenu = true;
            Time.timeScale = 1f;
            Application.targetFrameRate = 60;

            _eventHost = Find<VisualElement>("events");
            _runnerHost = Find<VisualElement>("runners");
            _hint = Find<Label>("hint");
            _total = Find<Label>("total");
            _start = Find<Button>("start");

            BuildEvents();
            BuildRunners();
            if (_start != null) _start.clicked += StartRace;

            SelectEvent(0);
            Refresh();
        }

        void BuildEvents()
        {
            if (_eventHost == null) return;
            _eventHost.Clear();
            _eventButtons.Clear();
            for (int i = 0; i < events.Count; i++)
            {
                int captured = i;
                var button = new Button(() => SelectEvent(captured)) { text = events[i].label };
                button.AddToClassList("event-btn");
                _eventHost.Add(button);
                _eventButtons.Add(button);
            }
        }

        void BuildRunners()
        {
            if (_runnerHost == null) return;
            _runnerHost.Clear();

            // An even split of the field across whatever definitions this build has, remainder to the first.
            int perRow = rows.Count > 0 ? Max / rows.Count : 0;
            for (int i = 0; i < rows.Count; i++)
            {
                RunnerRow row = rows[i];
                if (row.definition == null) continue;
                row.count = perRow;
                if (i == 0) row.count += Max - perRow * rows.Count;

                var element = new VisualElement();
                element.AddToClassList("runner-row");

                // The swatch is the athlete's own colour — the same one their trail wears on the deck and
                // their row wears in the results. It is the only thing that tells the field apart, since
                // the house rule keeps every athlete on the textures their model was imported with.
                var swatch = new VisualElement();
                swatch.AddToClassList("runner-swatch");
                swatch.style.backgroundColor = row.definition.Tint;

                var name = new Label(row.definition.displayName);
                name.AddToClassList("runner-name");

                RunnerRow captured = row;
                var minus = new Button(() => Adjust(captured, -1)) { text = "–" };
                minus.AddToClassList("btn");
                minus.AddToClassList("btn--step");

                var count = new Label(row.count.ToString());
                count.AddToClassList("runner-count");

                var plus = new Button(() => Adjust(captured, +1)) { text = "+" };
                plus.AddToClassList("btn");
                plus.AddToClassList("btn--step");

                element.Add(swatch);
                element.Add(name);
                element.Add(minus);
                element.Add(count);
                element.Add(plus);
                _runnerHost.Add(element);

                row.countLabel = count;
                row.minus = minus;
                row.plus = plus;
            }
        }

        /// <summary>Picks which scene START loads and re-words the hint for it.</summary>
        void SelectEvent(int index)
        {
            if (events.Count == 0) return;   // no race scene was built; StartRace says so
            _event = Mathf.Clamp(index, 0, events.Count - 1);
            EventChoice chosen = events[_event];
            SetText(_hint, string.IsNullOrEmpty(chosen.hint) ? $"Pick 1 to {Max} athletes." : chosen.hint);
            for (int i = 0; i < _eventButtons.Count; i++)
                _eventButtons[i].EnableInClassList("event-btn--selected", i == _event);
        }

        int Total()
        {
            int t = 0;
            foreach (RunnerRow r in rows) t += r.count;
            return t;
        }

        void Adjust(RunnerRow row, int delta)
        {
            int next = row.count + delta;
            if (next < 0) return;
            if (delta > 0 && Total() >= Max) return;
            if (delta < 0 && Total() <= 1) return;   // never leave an empty field
            row.count = next;
            Refresh();
        }

        void Refresh()
        {
            int total = Total();
            foreach (RunnerRow r in rows)
            {
                if (r.countLabel != null) SetText(r.countLabel, r.count.ToString());
                if (r.minus != null) r.minus.SetEnabled(r.count > 0 && total > 1);
                if (r.plus != null) r.plus.SetEnabled(total < Max);
            }
            SetText(_total, $"Total  {total} / {Max}");
            if (_start != null) _start.SetEnabled(total >= 1);
        }

        public void StartRace()
        {
            if (string.IsNullOrEmpty(SceneName))
            {
                Debug.LogError("[PoDecath] No event scene to load; run PoDecath/Build Race Scenes.", this);
                return;
            }
            RaceRoster.Set(BuildGridOrder());
            // The lap distance and the hurdles travel with the roster: one race scene serves every event on
            // the loop, and this is what tells it which one it is about to be.
            EventChoice chosen = events[_event];
            SessionSettings.SetEvent(chosen.laps, chosen.hurdles);
            SessionSettings.ApplyQuality();
            Time.timeScale = 1f;
            SceneManager.LoadScene(SceneName);
        }

        /// <summary>Round-robin over the types, so each grid row gets a mix rather than one policy per row.</summary>
        List<string> BuildGridOrder()
        {
            var remaining = new List<int>();
            foreach (RunnerRow r in rows) remaining.Add(r.count);

            var order = new List<string>();
            bool placed = true;
            while (placed && order.Count < Max)
            {
                placed = false;
                for (int i = 0; i < rows.Count && order.Count < Max; i++)
                {
                    if (remaining[i] <= 0 || rows[i].definition == null) continue;
                    order.Add(rows[i].definition.displayName);
                    remaining[i]--;
                    placed = true;
                }
            }
            return order;
        }
    }
}
