using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// The broadcast overlay, in UI Toolkit: clock and stage along the top, the running order down the
    /// left, splits beside it, a lower third that wipes in on every cut, and the countdown over the middle
    /// of the picture.
    ///
    /// None of it is new information — <see cref="DashEvent"/> already knows the order, the gaps and the
    /// clock, and <see cref="BroadcastDirector"/> already knows who is on air. What is new is that it
    /// moves. A running order that silently swaps two rows tells you nothing; one where the row that
    /// gained a place slides up and flashes green tells you a pass just happened, which is the single
    /// thing a viewer most wants to see and the thing a static table cannot say.
    ///
    /// Motion is done with USS transitions rather than coroutines: a class is added, the style system
    /// animates the property, and nothing here has to run a timer.
    ///
    /// Text is rebuilt at 10 Hz, like the HUD, so the string churn stays negligible; the transitions run
    /// at frame rate regardless, because they belong to the style system rather than to this refresh.
    /// </summary>
    [DefaultExecutionOrder(130)]
    public class BroadcastView : UiRoot
    {
        [Header("Wiring")]
        public DashEvent race;
        [Tooltip("Optional. Without it the lower third never appears; everything else still works.")]
        public BroadcastDirector director;

        [Header("Size")]
        [Tooltip("Rows in the running order strip. A deeper field gets a '+n more' line under it.")]
        public int orderRows = 8;
        [Tooltip("Splits kept on the board. The most recent few, oldest at the top.")]
        public int splitRows = 3;

        Label _stage, _lead, _clock, _overflow, _lowerName, _lowerDetail, _countdown;
        VisualElement _lowerThird, _splitsPanel, _orderHost, _splitHost;

        /// <summary>One row of the running order, kept so it can be re-used and animated rather than rebuilt.</summary>
        class Row
        {
            public VisualElement element;
            public Label rank, name, gap;
            public DashEvent.Athlete athlete;
            public int place = -1;
            public float flash;      // seconds left on the gained/lost highlight
            public bool gained;
        }

        readonly List<Row> _rows = new List<Row>();
        readonly List<Label> _splitLabels = new List<Label>();
        readonly List<string> _splits = new List<string>();
        int _splitsTaken;
        int _lastAttempt = -1;
        int _lastBeep = -1;
        float _nextRefresh;

        LapEvent Lap => race as LapEvent;
        LongJumpEvent Jump => race as LongJumpEvent;

        protected override void Build()
        {
            _stage = Find<Label>("stage");
            _lead = Find<Label>("lead");
            _clock = Find<Label>("clock");
            _overflow = Find<Label>("overflow");
            _lowerThird = Find<VisualElement>("lower-third");
            _lowerName = Find<Label>("lower-name");
            _lowerDetail = Find<Label>("lower-detail");
            _countdown = Find<Label>("countdown");
            _splitsPanel = Find<VisualElement>("splits");
            _orderHost = Find<VisualElement>("order-rows");
            _splitHost = Find<VisualElement>("split-rows");

            BuildRows();
            BuildSplits();

            // A single-lap event has no splits to show, so the board is not there rather than empty.
            LapEvent lap = Lap;
            Show(_splitsPanel, lap != null && lap.laps > 1);
            if (_splitsPanel != null && (lap == null || lap.laps <= 1))
            {
                VisualElement order = Find<VisualElement>("order");
                if (order != null) order.style.width = Length.Percent(100f);
            }
            Refresh();
        }

        void BuildRows()
        {
            if (_orderHost == null) return;
            _orderHost.Clear();
            _rows.Clear();
            for (int i = 0; i < orderRows; i++)
            {
                var element = new VisualElement();
                element.AddToClassList("order-row");
                element.AddToClassList("hidden");

                var rank = new Label("-");
                rank.AddToClassList("order-rank");
                var name = new Label("");
                name.AddToClassList("order-name");
                var gap = new Label("");
                gap.AddToClassList("order-gap");

                element.Add(rank);
                element.Add(name);
                element.Add(gap);
                _orderHost.Add(element);
                _rows.Add(new Row { element = element, rank = rank, name = name, gap = gap });
            }
        }

        void BuildSplits()
        {
            if (_splitHost == null) return;
            _splitHost.Clear();
            _splitLabels.Clear();
            for (int i = 0; i < splitRows; i++)
            {
                var label = new Label("");
                label.AddToClassList("split-row");
                _splitHost.Add(label);
                _splitLabels.Add(label);
            }
        }

        protected override void Update()
        {
            base.Update();
            if (race == null) return;

            if (race.Attempt != _lastAttempt)
            {
                _lastAttempt = race.Attempt;
                _splits.Clear();
                _splitsTaken = 0;
                foreach (Row r in _rows) { r.place = -1; r.athlete = null; }
            }

            Countdown();
            LowerThirdVisibility();
            DecayFlashes();

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.1f;
            Refresh();
        }

        // ---------------------------------------------------------------- the picture

        void Refresh()
        {
            if (race == null) return;
            List<DashEvent.Athlete> order = race.LiveOrder();
            DashEvent.Athlete leader = order.Count > 0 ? order[0] : null;

            TopBar(order, leader);
            RecordSplits(leader);
            OrderStrip(order, leader);
            LowerThirdText(order);
        }

        void TopBar(List<DashEvent.Athlete> order, DashEvent.Athlete leader)
        {
            SetText(_stage, Stage(leader));
            SetText(_clock, Clock());
            if (_lead == null) return;

            if (race.Current == DashEvent.Phase.Countdown) { SetText(_lead, "ON YOUR MARKS", Color.white); return; }
            if (leader == null) { SetText(_lead, ""); return; }

            if (Jump != null)
            {
                SetText(_lead, leader.finished ? $"{leader.name} leads with {leader.distance:F2} m" : $"{leader.name} leads", leader.color);
                return;
            }
            DashEvent.Athlete second = order.Count > 1 ? order[1] : null;
            if (second == null) { SetText(_lead, leader.name, leader.color); return; }
            if (leader.finished && second.finished) { SetText(_lead, $"{leader.name} by {second.time - leader.time:F2} s", leader.color); return; }
            float gap = leader.distance - second.distance;
            SetText(_lead, gap < 0.6f ? $"{leader.name} — nothing in it" : $"{leader.name} by {gap:F1} m", leader.color);
        }

        /// <summary>Where the event has got to, in the words the event itself would use.</summary>
        string Stage(DashEvent.Athlete leader)
        {
            LongJumpEvent jump = Jump;
            if (jump != null) return $"ROUND {jump.Round} / {Mathf.Max(1, jump.attemptsEach)}";

            LapEvent lap = Lap;
            if (lap == null || lap.path == null) return $"{race.raceDistance:F0} M";
            if (lap.laps <= 1) return $"{race.raceDistance:F0} M";
            float covered = leader != null ? Mathf.Max(0f, leader.distance) : 0f;
            int onLap = Mathf.Clamp(Mathf.FloorToInt(covered / lap.path.LapLength) + 1, 1, lap.laps);
            return onLap == lap.laps ? $"LAP {onLap} / {lap.laps}  ·  LAST" : $"LAP {onLap} / {lap.laps}";
        }

        string Clock()
        {
            if (race.Current == DashEvent.Phase.Countdown) return Mathf.CeilToInt(Mathf.Max(0f, race.Countdown)).ToString();
            return Fmt(race.RaceTime);
        }

        /// <summary>
        /// A split per completed lap, taken off whoever is leading at the time. Recorded on the way past,
        /// so the board keeps them even after that runner has been caught.
        /// </summary>
        void RecordSplits(DashEvent.Athlete leader)
        {
            LapEvent lap = Lap;
            if (lap == null || lap.path == null || lap.laps <= 1 || leader == null) return;
            if (race.Current != DashEvent.Phase.Running) { PaintSplits(false); return; }

            float lapLength = lap.path.LapLength;
            bool fresh = false;
            while (_splitsTaken < lap.laps && leader.distance >= (_splitsTaken + 1) * lapLength)
            {
                _splitsTaken++;
                _splits.Add($"LAP {_splitsTaken}   {Fmt(race.RaceTime)}");
                fresh = true;
            }
            PaintSplits(fresh);
        }

        void PaintSplits(bool fresh)
        {
            if (_splitLabels.Count == 0) return;
            int shown = Mathf.Min(_splitLabels.Count, _splits.Count);
            int from = _splits.Count - shown;   // the most recent few, oldest at the top
            for (int i = 0; i < _splitLabels.Count; i++)
            {
                bool used = i < shown;
                SetText(_splitLabels[i], used ? _splits[from + i] : "");
                // Only the newest split is highlighted, and only while it is the newest.
                _splitLabels[i].EnableInClassList("split-row--new", used && fresh && i == shown - 1);
            }
        }

        /// <summary>
        /// Paints the running order and animates any change in it. A row that has moved up is translated
        /// from where it used to be and released, so the style system slides it into place; the direction
        /// it moved decides which colour it flashes.
        /// </summary>
        void OrderStrip(List<DashEvent.Athlete> order, DashEvent.Athlete leader)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                bool used = i < order.Count;
                Show(row.element, used);
                if (!used) { row.athlete = null; row.place = -1; continue; }

                DashEvent.Athlete a = order[i];
                int wasPlace = row.athlete == a ? row.place : PlaceOf(a);

                SetText(row.rank, (i + 1).ToString());
                SetText(row.name, a.name, a.color);
                SetText(row.gap, Gap(a, leader, i));
                row.gap.style.color = a.fell ? new Color(1f, 0.5f, 0.42f)
                                    : a.recovering ? new Color(1f, 0.78f, 0.33f)   // amber: in trouble, not out
                                    : a.finished ? Color.white
                                    : new Color(0.72f, 0.76f, 0.82f);
                row.element.EnableInClassList("order-row--lead", i == 0);

                if (wasPlace >= 0 && wasPlace != i)
                {
                    // Start it where it was and let the transition carry it to where it is now. 52 px is
                    // the row height in the stylesheet; a row that moved two places starts two rows away.
                    float from = (wasPlace - i) * 52f;
                    row.element.style.translate = new StyleTranslate(new Translate(0f, from));
                    row.element.schedule.Execute(() => row.element.style.translate = new StyleTranslate(new Translate(0f, 0f))).StartingIn(0);
                    row.gained = i < wasPlace;
                    row.flash = 0.9f;
                    row.element.EnableInClassList("order-row--gained", row.gained);
                    row.element.EnableInClassList("order-row--lost", !row.gained);
                }

                row.athlete = a;
                row.place = i;
            }

            if (_overflow == null) return;
            int hidden = Mathf.Max(0, order.Count - _rows.Count);
            Show(_overflow, hidden > 0);
            if (hidden > 0) SetText(_overflow, $"+{hidden} more");
        }

        int PlaceOf(DashEvent.Athlete a)
        {
            foreach (Row r in _rows) if (r.athlete == a) return r.place;
            return -1;
        }

        void DecayFlashes()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                if (row.flash <= 0f) continue;
                row.flash -= Time.unscaledDeltaTime;
                if (row.flash > 0f) continue;
                row.element.RemoveFromClassList("order-row--gained");
                row.element.RemoveFromClassList("order-row--lost");
            }
        }

        /// <summary>What goes in the right-hand column: a mark, a time, a deficit, or why there is none.</summary>
        string Gap(DashEvent.Athlete a, DashEvent.Athlete leader, int index)
        {
            if (Jump != null) return a.finished ? $"{a.distance:F2} m" : "—";
            // Down but not out. This is the single most interesting line the overlay can carry, so it wins
            // over the gap: a runner on the deck getting back up is what everyone in the stadium is looking
            // at, and a row that just shows a speed of 0.2 m/s does not say that.
            if (a.recovering) return "DOWN";
            if (a.fell) return "DNF";
            if (a.finished) return index == 0 ? Fmt(a.time) : $"+{a.time - leader.time:F2}";
            if (leader == null || a == leader) return race.Current == DashEvent.Phase.Running ? $"{a.speed:F1} m/s" : "";
            return $"+{Mathf.Max(0f, leader.distance - a.distance):F1} m";
        }

        // ---------------------------------------------------------------- lower third

        bool Showing => director != null && director.OnIndividual && director.Featured != null
                        && race.Current != DashEvent.Phase.Idle;

        void LowerThirdVisibility()
        {
            if (_lowerThird == null) return;
            bool show = Showing;
            _lowerThird.EnableInClassList("lower-third--in", show);
            _lowerThird.EnableInClassList("lower-third--out", !show);
        }

        void LowerThirdText(List<DashEvent.Athlete> order)
        {
            if (_lowerName == null || director == null) return;
            DashEvent.Athlete a = director.Featured;
            if (a == null) return;

            SetText(_lowerName, a.name, a.color);
            if (_lowerDetail == null) return;

            if (Jump != null)
            {
                LongJumpEvent jump = Jump;
                SetText(_lowerDetail, a.finished
                    ? $"Round {jump.Round}  ·  best {a.distance:F2} m"
                    : $"Round {jump.Round}  ·  no mark yet");
                return;
            }

            int place = order.IndexOf(a) + 1;
            string where = place > 0 ? Ordinal(place) : "";
            if (a.recovering) SetText(_lowerDetail, $"{where}  ·  down at {a.distance:F0} m  ·  getting up");
            else if (a.fell) SetText(_lowerDetail, $"{where}  ·  down at {a.fellAt:F0} m");
            else if (a.finished) SetText(_lowerDetail, $"{where}  ·  {a.time:F2} s");
            else
            {
                string ups = a.recoveries > 0 ? $"  ·  {a.recoveries} up" : "";
                SetText(_lowerDetail, $"{where}  ·  {a.speed:F1} m/s  ·  {a.distance:F0} / {race.raceDistance:F0} m{ups}");
            }
        }

        // ---------------------------------------------------------------- countdown

        /// <summary>
        /// The number over the middle of the picture. It punches up on each beep and settles back, which
        /// gives the last three seconds before a gun the pulse they need — a static digit changing every
        /// second reads as a clock, not as a countdown.
        /// </summary>
        void Countdown()
        {
            if (_countdown == null) return;
            bool on = race.Current == DashEvent.Phase.Countdown;
            if (!on)
            {
                _countdown.EnableInClassList("countdown--off", true);
                _countdown.EnableInClassList("countdown--beat", false);
                _countdown.EnableInClassList("countdown--rest", false);
                _lastBeep = -1;
                return;
            }

            int tick = Mathf.CeilToInt(Mathf.Max(0f, race.Countdown));
            _countdown.EnableInClassList("countdown--off", false);
            if (tick != _lastBeep)
            {
                _lastBeep = tick;
                SetText(_countdown, tick > 0 ? tick.ToString() : "GO");
                // Punch, then let the transition ease it back down on the next frame.
                _countdown.EnableInClassList("countdown--rest", false);
                _countdown.EnableInClassList("countdown--beat", true);
                _countdown.schedule.Execute(() =>
                {
                    _countdown.EnableInClassList("countdown--beat", false);
                    _countdown.EnableInClassList("countdown--rest", true);
                }).StartingIn(90);
            }
        }

        // ---------------------------------------------------------------- formatting

        static string Fmt(float seconds)
        {
            if (seconds < 60f) return $"{seconds:F2}";
            int m = Mathf.FloorToInt(seconds / 60f);
            return $"{m}:{seconds - m * 60f:00.00}";
        }

        static string Ordinal(int n) => n switch
        {
            1 => "1st", 2 => "2nd", 3 => "3rd", 21 => "21st", 22 => "22nd", 23 => "23rd",
            _ => $"{n}th",
        };
    }
}
