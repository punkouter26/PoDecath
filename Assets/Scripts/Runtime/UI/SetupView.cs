using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// The event setup menu: a row of event chips, then one tile per athlete definition including the
    /// RED heuristic bot, capped at <see cref="RaceRoster.MaxRunners"/> in total and at least one athlete
    /// overall.
    ///
    /// The field is a grid of tiles rather than a list, and the grid is sized to the screen rather than
    /// the screen to the grid: <see cref="FitTiles"/> reads the height left between the events and START
    /// and divides it by the rows the roster needs, so nine athletes, or twelve, fit one portrait screen
    /// without a scroll bar. A menu that has to be scrolled to find START is a menu with a hidden button.
    ///
    /// The grid order interleaves the types round-robin rather than listing each block in turn. The
    /// starting grid is staggered two abreast (the deck is far too narrow to line a full field up in one
    /// row), so a block layout would drop one whole policy into the back rows and make it look beaten from
    /// the gun even though every runner covers the same lap. Interleaving spreads both policies evenly down
    /// the grid; the times in the results are the comparison that counts either way.
    ///
    /// Every chip and tile is built from the definitions the project actually has, so a policy that has
    /// never been trained and an event whose scene was never built simply do not appear.
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
                   + "15 the 1500 m, all the same scene. 0 leaves the scene's own setting alone.")]
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
            [NonSerialized] public VisualElement row;
        }

        public List<RunnerRow> rows = new List<RunnerRow>();
        [Tooltip("Events on offer, built by PoDecath/Build Race Scenes. The selected one owns the scene "
               + "START loads and the hint under the title.")]
        public List<EventChoice> events = new List<EventChoice>();
        [Tooltip("Tiles across the field grid. Three fits nine athletes on one portrait screen.")]
        public int columns = 3;

        VisualElement _eventHost, _runnerHost;
        Label _hint, _total;
        Button _start, _all, _none;
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
            _all = Find<Button>("all");
            _none = Find<Button>("none");

            BuildEvents();
            BuildRunners();
            if (_runnerHost != null) _runnerHost.RegisterCallback<GeometryChangedEvent>(_ => FitTiles());
            if (_start != null) _start.clicked += StartRace;
            // Clearing the field one tap at a time was fine with three athletes on the roster. With the
            // character models on it there are ten, and somebody who wants to watch Trump race the zombie
            // should not have to press minus eight times to say so.
            if (_all != null) _all.clicked += () => SetEveryone(1);
            if (_none != null) _none.clicked += () => SetEveryone(0);

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

            // One of each to start with, which is the field that shows the owner every athlete the project
            // has. It used to be an even split of all sixteen places, and that stopped being a sensible
            // default the moment the roster grew past a handful: the split silently decided that six of
            // somebody were in and made the remainder row, whichever happened to be listed first, into
            // the biggest team in the race. One each is the same answer however long the roster gets, and
            // the steppers are right there for anyone who wants eight Grandmas.
            int used = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                RunnerRow row = rows[i];
                if (row.definition == null) continue;
                row.count = used < Max ? 1 : 0;
                used += row.count;

                var tile = new VisualElement();
                tile.AddToClassList("runner-tile");
                // The stripe along the top is the athlete's own colour: the one their trail wears on the
                // deck and their row wears in the results. It is the only thing that tells the field
                // apart, since the house rule keeps every athlete on the textures their model came with.
                tile.style.borderTopColor = row.definition.Tint;

                var name = new Label(row.definition.displayName);
                name.AddToClassList("runner-tile-name");

                var count = new Label(row.count.ToString());
                count.AddToClassList("runner-tile-count");

                var steps = new VisualElement();
                steps.AddToClassList("runner-tile-steps");
                RunnerRow captured = row;
                var minus = new Button(() => Adjust(captured, -1)) { text = "-" };
                minus.AddToClassList("btn");
                minus.AddToClassList("btn--tile-step");
                var plus = new Button(() => Adjust(captured, +1)) { text = "+" };
                plus.AddToClassList("btn");
                plus.AddToClassList("btn--tile-step");
                steps.Add(minus);
                steps.Add(plus);

                tile.Add(name);
                tile.Add(count);
                tile.Add(steps);
                _runnerHost.Add(tile);

                row.countLabel = count;
                row.minus = minus;
                row.plus = plus;
                row.row = tile;
            }
        }

        /// <summary>
        /// Sizes every tile to the height the grid actually has. Runs on every geometry change of the
        /// grid, which is once at start-up and again on a rotation or a resize; the arithmetic is one
        /// division and the result is the same for every tile.
        /// </summary>
        void FitTiles()
        {
            if (_runnerHost == null) return;
            int tiles = 0;
            foreach (RunnerRow r in rows) if (r.row != null) tiles++;
            int cols = Mathf.Max(1, columns);
            int gridRows = Mathf.CeilToInt(tiles / (float)cols);
            float height = _runnerHost.resolvedStyle.height;
            if (gridRows == 0 || float.IsNaN(height) || height <= 1f) return;
            const float margin = 16f;   // 8 px above and below each tile
            float tile = Mathf.Max(150f, height / gridRows - margin);
            float width = 100f / cols - 2f;   // 1% margin either side
            foreach (RunnerRow r in rows)
            {
                if (r.row == null) continue;
                r.row.style.height = tile;
                r.row.style.width = Length.Percent(width);
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

        /// <summary>
        /// Puts the same number in every tile. NONE is allowed to empty the field completely, which the
        /// steppers are not: it is the start of "clear this and pick two", and START stays greyed out
        /// until somebody has been picked, so an empty grid can never reach a race scene.
        /// </summary>
        void SetEveryone(int count)
        {
            int used = 0;
            foreach (RunnerRow r in rows)
            {
                if (r.definition == null) continue;
                r.count = used + count <= Max ? count : 0;
                used += r.count;
            }
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
                r.row?.EnableInClassList("runner-tile--out", r.count == 0);
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
