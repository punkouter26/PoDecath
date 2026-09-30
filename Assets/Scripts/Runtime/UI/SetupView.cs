using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// The event setup menu: a row of event chips, then one tile per athlete definition, capped at
    /// <see cref="RaceRoster.MaxRunners"/> in total and at least one athlete overall.
    ///
    /// The field is a grid of tiles rather than a list, and the grid is sized to the screen rather than
    /// the screen to the grid: <see cref="FitTiles"/> reads the height left between the events and START
    /// and divides it by the rows the roster needs, so nine athletes, or twelve, fit one portrait screen
    /// without a scroll bar. A menu that has to be scrolled to find START is a menu with a hidden button.
    ///
    /// A tile is the athlete's face (<see cref="AthleteDefinition.portrait"/>), their name and their best
    /// mark in the picked event. A tap on it adds a runner; the minus in its corner takes one off; the "i"
    /// turns it over to show the athlete's record on this device, and a tap on the back turns it round.
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
            [Tooltip("One line under the event chips explaining what the field is about to do.")]
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
            [NonSerialized] public Label countLabel, best;
            [NonSerialized] public Button minus;
            [NonSerialized] public VisualElement row;
            [NonSerialized] public Label card;
        }

        public List<RunnerRow> rows = new List<RunnerRow>();
        [Tooltip("Events on offer, built by PoDecath/Build Race Scenes. The selected one owns the scene "
               + "START loads and the line under the chips.")]
        public List<EventChoice> events = new List<EventChoice>();
        [Tooltip("Tiles across the field grid. Three fits nine athletes on one portrait screen.")]
        public int columns = 3;

        VisualElement _eventHost, _runnerHost;
        Label _hint, _startDetail;
        Button _start, _all, _none;
        Button _season, _seasonReset;
        Label _seasonLabel, _seasonResetLabel;
        bool _confirmEnd;
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
            _startDetail = Find<Label>("start-detail");
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

            BuildSeason();
            SelectEvent(0);
            Refresh();
        }

        // ---------------------------------------------------------------- season and cards

        void BuildSeason()
        {
            _season = Find<Button>("season");
            _seasonLabel = Find<Label>("season-label");
            _seasonReset = Find<Button>("season-reset");
            _seasonResetLabel = Find<Label>("season-reset-label");

            if (_season != null) _season.clicked += OnSeason;
            if (_seasonReset != null) _seasonReset.clicked += OnEndSeason;

            SeasonStore.Load();
            RefreshSeason();
        }

        void Start()
        {
            // A newer season from another device replaces this one's; the button and cards follow it. From
            // Start, not from Build (which runs in OnEnable): see SeasonCloud.Connect.
            SeasonCloud.Pull(adopted => { if (adopted) RefreshSeason(); });
        }

        void RefreshSeason()
        {
            SeasonStore.Season s = SeasonStore.Current.season;
            // One word under an icon: which leg is next reads off the number, and its name is on the hint line.
            SetText(_seasonLabel, SeasonStore.SeasonActive ? $"SEASON {s.next + 1}/{s.legs.Count}" : "SEASON");
            Show(_seasonReset, SeasonStore.SeasonActive);
            SelectEvent(_event);   // re-words the hint, which carries the season and save lines
            FillTileCards();
        }

        /// <summary>The second part of the hint: where the records are kept and, mid-season, what is next.</summary>
        static string SeasonLine()
        {
            string where = SeasonCloud.Linked ? SeasonCloud.StatusLine : "Saved on this device";
            if (!SeasonStore.SeasonActive) return where;
            SeasonStore.Season s = SeasonStore.Current.season;
            return $"Season: next is {s.legs[s.next].label}  ·  {where}";
        }

        /// <summary>
        /// Starts a season with the field as picked, over every event this menu offers, in decathlon order;
        /// or, with one already running, sends its own field to its next event. The picked field is ignored
        /// when continuing: a season is one field from first event to last.
        /// </summary>
        void OnSeason()
        {
            if (SeasonStore.SeasonActive)
            {
                SeasonKeeper.Launch(SeasonStore.NextLeg);
                return;
            }
            var legs = new List<SeasonStore.Leg>();
            foreach (EventChoice e in events)
                legs.Add(new SeasonStore.Leg { label = e.label, scene = e.sceneName, laps = e.laps, hurdles = e.hurdles, evt = Classify(e) });
            // Decathlon order: 100 m, long jump, 400 m on day one; hurdles and 1500 m on day two. The enum
            // is declared in that order, so a stable sort on it is the whole of the ordering.
            legs.Sort((a, b) => a.evt.CompareTo(b.evt));
            if (legs.Count == 0 || Total() < 1) return;
            SeasonStore.StartSeason(legs, BuildGridOrder());
            SeasonKeeper.Launch(legs[0]);
        }

        static SeasonEvent Classify(EventChoice e)
        {
            if (!string.IsNullOrEmpty(e.sceneName) && e.sceneName.Contains("LongJump")) return SeasonEvent.LongJump;
            if (e.hurdles) return SeasonEvent.Hurdles;
            if (e.laps >= 10) return SeasonEvent.Run1500;
            if (e.laps >= 3) return SeasonEvent.Run400;
            return SeasonEvent.Sprint100;
        }

        /// <summary>Asks once before throwing a season away: the second tap on END ends it.</summary>
        void OnEndSeason()
        {
            if (!_confirmEnd)
            {
                _confirmEnd = true;
                SetText(_seasonResetLabel, "SURE?");
                return;
            }
            _confirmEnd = false;
            SetText(_seasonResetLabel, "END");
            SeasonStore.EndSeason();
            RefreshSeason();
        }

        /// <summary>
        /// A tap on a tile adds a runner, or turns a tile that is showing its record back round. A tap on
        /// one of its buttons (the minus, the "i") is that button's, not the tile's.
        /// </summary>
        void OnTileClicked(RunnerRow row, ClickEvent evt)
        {
            for (var e = evt.target as VisualElement; e != null && e != row.row; e = e.parent)
                if (e is Button) return;
            if (row.row.ClassListContains("runner-tile--flipped")) { Flip(row); return; }
            Adjust(row, +1);
        }

        /// <summary>Turns tile <paramref name="index"/> over or back; what the "i" does, callable by the UI captures.</summary>
        public void FlipTile(int index)
        {
            if (index >= 0 && index < rows.Count && rows[index].row != null) Flip(rows[index]);
        }

        void Flip(RunnerRow row)
        {
            bool flip = !row.row.ClassListContains("runner-tile--flipped");
            row.row.EnableInClassList("runner-tile--flipped", flip);
            Show(row.card, flip);
            if (flip) SetText(row.card, CardText(row));
        }

        void FillTileCards()
        {
            foreach (RunnerRow r in rows)
                if (r.card != null && !r.card.ClassListContains("hidden")) SetText(r.card, CardText(r));
            FillBests();
        }

        static SeasonStore.Card CardOf(RunnerRow row)
        {
            string name = row.definition != null ? row.definition.displayName : "";
            foreach (SeasonStore.Card k in SeasonStore.Current.cards)
                if (k.athlete == name) return k;
            return null;
        }

        /// <summary>
        /// The athlete's record on this device in plain numbers, sized for the back of a tile: races, wins
        /// and falls, the fastest it has been measured going, the work its joints have done, and its best
        /// mark per event with the points it was worth.
        /// </summary>
        static string CardText(RunnerRow row)
        {
            SeasonStore.Card c = CardOf(row);
            if (c == null || c.races == 0) return "No races yet.\nEvery race adds to this card.";

            var sb = new System.Text.StringBuilder();
            sb.Append($"{c.races} races · {c.wins} wins\n{c.podiums} podiums · {c.falls} falls\n");
            sb.Append($"top {c.topSpeed:F1} m/s · {c.joules / 1000f:F0} kJ");
            foreach (SeasonStore.Best b in c.bests)
                sb.Append($"\n{ScoringTable.Label(b.evt)} {ScoringTable.Format(b.evt, b.value)} ({b.points})");
            return sb.ToString();
        }

        /// <summary>Each tile's best mark in the event that is picked, which is the number that decides who to send.</summary>
        void FillBests()
        {
            if (events.Count == 0) return;
            SeasonEvent evt = Classify(events[_event]);
            foreach (RunnerRow r in rows)
            {
                if (r.best == null) continue;
                SeasonStore.Best b = CardOf(r)?.BestFor(evt);
                SetText(r.best, b != null ? $"best {ScoringTable.Format(evt, b.value)}" : "no mark yet");
            }
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
            // a tap on a tile is right there for anyone who wants eight Grandmas.
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
                // deck and their row wears in the results.
                tile.style.borderTopColor = row.definition.Tint;

                // The face, with the tile's controls in its corners. The record, when the tile is turned
                // over, covers the face rather than replacing the tile, so the name stays put.
                var face = new VisualElement();
                face.AddToClassList("runner-tile-face");
                if (row.definition.portrait != null) face.style.backgroundImage = new StyleBackground(row.definition.portrait);

                var card = new Label("");
                card.AddToClassList("runner-tile-card");
                card.AddToClassList("hidden");

                RunnerRow captured = row;
                var minus = new Button(() => Adjust(captured, -1)) { text = "−" };
                minus.AddToClassList("runner-tile-chip");
                minus.AddToClassList("runner-tile-minus");

                var count = new Label(row.count.ToString());
                count.AddToClassList("runner-tile-count");

                var info = new Button(() => Flip(captured)) { text = "i" };
                info.AddToClassList("runner-tile-chip");
                info.AddToClassList("runner-tile-info");

                face.Add(card);
                face.Add(minus);
                face.Add(count);
                face.Add(info);

                var name = new Label(row.definition.displayName);
                name.AddToClassList("runner-tile-name");
                var best = new Label("");
                best.AddToClassList("runner-tile-best");

                tile.Add(face);
                tile.Add(name);
                tile.Add(best);
                tile.RegisterCallback<ClickEvent>(e => OnTileClicked(captured, e));
                _runnerHost.Add(tile);

                row.card = card;
                row.countLabel = count;
                row.best = best;
                row.minus = minus;
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

        /// <summary>Picks which scene START loads, re-words the line under the chips, and shows each tile's best in it.</summary>
        void SelectEvent(int index)
        {
            if (events.Count == 0) return;   // no race scene was built; StartRace says so
            _event = Mathf.Clamp(index, 0, events.Count - 1);
            EventChoice chosen = events[_event];
            string hint = string.IsNullOrEmpty(chosen.hint) ? $"Pick 1 to {Max} athletes." : chosen.hint;
            SetText(_hint, SeasonStore.SeasonActive ? $"{hint}  ·  {SeasonLine()}" : hint);
            for (int i = 0; i < _eventButtons.Count; i++)
                _eventButtons[i].EnableInClassList("event-btn--selected", i == _event);
            FillBests();
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
        /// minus is not: it is the start of "clear this and pick two", and START stays greyed out until
        /// somebody has been picked, so an empty grid can never reach a race scene.
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
                r.row?.EnableInClassList("runner-tile--out", r.count == 0);
            }
            SetText(_startDetail, total == 1 ? "1 runner" : $"{total} of {Max} runners");
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
            SessionSettings.SeasonRace = false;   // a race picked here is a free race, season or not
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
