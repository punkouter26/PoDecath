using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using PoDecath.Audio;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// The broadcast overlay, in UI Toolkit: clock and stage along the top, the running order down the
    /// left, splits beside it, a lower third that wipes in on every cut, and the countdown over the middle
    /// of the picture.
    ///
    /// None of it is new information — <see cref="RaceEvent"/> already knows the order, the gaps and the
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
        public RaceEvent race;
        [Tooltip("Optional. Without it the lower third never appears; everything else still works.")]
        public BroadcastDirector director;
        [Tooltip("Optional. Without it the caption band stays off the picture and nothing else changes.")]
        public Commentary commentary;

        [Header("Strain")]
        [Tooltip("Reading at which the strain bar is fully red. 1 would mean every joint pinned at its "
               + "torque limit at once, which never happens, so the bar would never leave the green.")]
        [Range(0.2f, 1f)] public float strainFullScale = 0.55f;

        [Header("Size")]
        [Tooltip("Rows in the running order strip. A deeper field gets a '+n more' line under it.")]
        public int orderRows = 8;
        [Tooltip("Splits kept on the board. The most recent few, oldest at the top.")]
        public int splitRows = 3;

        Label _stage, _lead, _clock, _overflow, _lowerName, _lowerDetail, _countdown;
        VisualElement _lowerThird, _splitsPanel, _orderHost, _splitHost;
        VisualElement _caption, _strain, _strainFill;
        VisualElement _map;
        Label _captionText, _strainValue;
        string _shownCaption = "";

        /// <summary>One row of the running order, kept so it can be re-used and animated rather than rebuilt.</summary>
        class Row
        {
            public VisualElement element;
            public Label rank, name, gap;
            public RaceEvent.Athlete athlete;
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
            _caption = Find<VisualElement>("caption");
            _captionText = Find<Label>("caption-text");
            _strain = Find<VisualElement>("strain");
            _strainFill = Find<VisualElement>("strain-fill");
            _strainValue = Find<Label>("strain-value");
            _map = Find<VisualElement>("track-map");
            if (_map != null) _map.generateVisualContent += DrawMap;

            BuildRows();
            BuildSplits();

            // A single-lap event has no splits to show, so the board is not there rather than empty.
            LapEvent lap = Lap;
            Show(_splitsPanel, lap != null && lap.laps > 1);
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
            CaptionBand();
            DecayFlashes();

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.1f;
            Refresh();
        }

        // ---------------------------------------------------------------- the picture

        void Refresh()
        {
            if (race == null) return;
            List<RaceEvent.Athlete> order = race.LiveOrder();
            RaceEvent.Athlete leader = order.Count > 0 ? order[0] : null;

            TopBar(order, leader);
            RecordSplits(leader);
            OrderStrip(order, leader);
            LowerThirdText(order);
            Strain();
            _map?.MarkDirtyRepaint();
        }

        /// <summary>
        /// The strain bar under the lower third: how hard the athlete on camera is pulling, as a fraction
        /// of what its drives are configured to allow.
        ///
        /// Scaled against <see cref="strainFullScale"/> rather than against 1, because a mean saturation
        /// of 1 would mean every joint in the body pinned at its torque limit simultaneously — which does
        /// not happen, and a bar that can only ever fill a third of the way is a bar nobody reads. The
        /// number printed beside it is the raw reading, so the scaling is presentation and the figure is
        /// still the measurement.
        /// </summary>
        void Strain()
        {
            if (_strain == null || _strainFill == null) return;
            RaceEvent.Athlete a = director != null ? director.Featured : race.Reference;
            EffortMeter meter = a != null ? a.effort : null;

            Show(_strain, meter != null);
            if (meter == null) return;

            float shown = Mathf.Clamp01(meter.Effort / Mathf.Max(0.01f, strainFullScale));
            _strainFill.style.width = Length.Percent(shown * 100f);
            _strainFill.style.backgroundColor = Color.Lerp(
                new Color(0.18f, 0.75f, 0.55f), new Color(1f, 0.32f, 0.2f), shown);

            // Watts, not the normalised figure: it is the one number here with a unit, and a viewer can do
            // something with "620 watts" that they cannot do with "0.41".
            SetText(_strainValue, meter.Fatigue > 0.35f
                ? $"{meter.Watts:F0} W  ·  tiring"
                : $"{meter.Watts:F0} W");
        }

        /// <summary>
        /// The commentary caption. Driven off <see cref="Commentary.Caption"/> rather than off its event,
        /// so a line said while this screen was hidden behind the results card does not leave the band
        /// stuck on when it comes back.
        /// </summary>
        void CaptionBand()
        {
            if (_caption == null) return;
            string line = commentary != null ? commentary.Caption : "";
            bool show = !string.IsNullOrEmpty(line);

            if (show && line != _shownCaption)
            {
                _shownCaption = line;
                SetText(_captionText, line);
            }
            else if (!show) _shownCaption = "";

            _caption.EnableInClassList("caption--in", show);
            _caption.EnableInClassList("caption--out", !show);
        }

        void TopBar(List<RaceEvent.Athlete> order, RaceEvent.Athlete leader)
        {
            SetText(_stage, Stage(leader));
            SetText(_clock, Clock());
            if (_lead == null) return;

            if (race.Current == RaceEvent.Phase.Countdown) { SetText(_lead, "ON YOUR MARKS", Color.white); return; }
            if (leader == null) { SetText(_lead, ""); return; }

            if (Jump != null)
            {
                SetText(_lead, leader.finished ? $"{leader.name} leads with {leader.distance:F2} m" : $"{leader.name} leads", leader.color);
                return;
            }
            RaceEvent.Athlete second = order.Count > 1 ? order[1] : null;
            if (second == null) { SetText(_lead, leader.name, leader.color); return; }
            if (leader.finished && second.finished) { SetText(_lead, $"{leader.name} by {second.time - leader.time:F2} s", leader.color); return; }
            float gap = leader.distance - second.distance;
            SetText(_lead, gap < 0.6f ? $"{leader.name} — nothing in it" : $"{leader.name} by {gap:F1} m", leader.color);
        }

        /// <summary>Where the event has got to, in the words the event itself would use.</summary>
        string Stage(RaceEvent.Athlete leader)
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
            if (race.Current == RaceEvent.Phase.Countdown) return Mathf.CeilToInt(Mathf.Max(0f, race.Countdown)).ToString();
            return Fmt(race.RaceTime);
        }

        /// <summary>
        /// A split per completed lap, taken off whoever is leading at the time. Recorded on the way past,
        /// so the board keeps them even after that runner has been caught.
        /// </summary>
        void RecordSplits(RaceEvent.Athlete leader)
        {
            LapEvent lap = Lap;
            if (lap == null || lap.path == null || lap.laps <= 1 || leader == null) return;
            if (race.Current != RaceEvent.Phase.Running) { PaintSplits(false); return; }

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
        void OrderStrip(List<RaceEvent.Athlete> order, RaceEvent.Athlete leader)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                bool used = i < order.Count;
                Show(row.element, used);
                if (!used) { row.athlete = null; row.place = -1; continue; }

                RaceEvent.Athlete a = order[i];
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

        int PlaceOf(RaceEvent.Athlete a)
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
        string Gap(RaceEvent.Athlete a, RaceEvent.Athlete leader, int index)
        {
            if (Jump != null) return a.finished ? $"{a.distance:F2} m" : "—";
            // Down but not out. This is the single most interesting line the overlay can carry, so it wins
            // over the gap: a runner on the deck getting back up is what everyone in the stadium is looking
            // at, and a row that just shows a speed of 0.2 m/s does not say that.
            if (a.recovering) return "DOWN";
            if (a.fell) return "DNF";
            if (a.finished) return index == 0 ? Fmt(a.time) : $"+{a.time - leader.time:F2}";
            if (leader == null || a == leader) return race.Current == RaceEvent.Phase.Running ? $"{a.speed:F1} m/s" : "";
            return $"+{Mathf.Max(0f, leader.distance - a.distance):F1} m";
        }

        // ---------------------------------------------------------------- track map

        /// <summary>
        /// The loop from above with a dot per athlete, painted with the mesh API on every refresh. It is
        /// the one piece of the overlay that answers "where is everyone" in a glance, which a running
        /// order cannot, and it costs a hundred line segments ten times a second.
        /// </summary>
        void DrawMap(MeshGenerationContext ctx)
        {
            if (race == null) return;
            Rect r = ctx.visualElement.contentRect;
            if (r.width < 8f || r.height < 8f) return;
            Painter2D painter = ctx.painter2D;
            LongJumpEvent jump = Jump;
            if (jump != null) { DrawRunway(painter, r, jump); return; }
            LapEvent lap = Lap;
            if (lap == null || lap.path == null) return;
            TrackPath path = lap.path;

            const float pad = 14f;
            float w = 2f * (path.halfLength + path.radius), h = 2f * path.radius;
            float scale = Mathf.Min((r.width - 2f * pad) / w, (r.height - 2f * pad) / h);
            Vector2 centre = new Vector2(r.x + r.width * 0.5f, r.y + r.height * 0.5f);
            Vector2 Map(float s)
            {
                path.Local(s, out Vector2 p, out _);
                return centre + new Vector2(p.x, -p.y) * scale;
            }

            painter.lineWidth = 3f;
            painter.strokeColor = new Color(1f, 1f, 1f, 0.55f);
            painter.BeginPath();
            painter.MoveTo(Map(0f));
            const int steps = 96;
            for (int i = 1; i <= steps; i++) painter.LineTo(Map(path.LapLength * i / steps));
            painter.ClosePath();
            painter.Stroke();

            // The line, as a tick across the deck.
            path.Local(lap.startS, out Vector2 lp, out Vector2 lt);
            Vector2 lc = centre + new Vector2(lp.x, -lp.y) * scale;
            Vector2 ln = new Vector2(-lt.y, -lt.x) * (path.deckWidth * 0.5f * scale + 4f);
            painter.lineWidth = 2f;
            painter.strokeColor = new Color(1f, 1f, 1f, 0.9f);
            painter.BeginPath();
            painter.MoveTo(lc - ln);
            painter.LineTo(lc + ln);
            painter.Stroke();

            List<RaceEvent.Athlete> order = race.LiveOrder();
            for (int i = order.Count - 1; i >= 0; i--)
            {
                RaceEvent.Athlete a = order[i];
                Vector2 c = Map(ArcOf(a, path));
                bool lead = i == 0;
                float radius = lead ? 8f : 6f;
                Color col = a.color;
                if (a.fell) { col.a = 0.45f; radius = 5f; }
                painter.fillColor = col;
                painter.BeginPath();
                painter.Arc(c, radius, 0f, 360f);
                painter.Fill();
                if (!lead) continue;
                painter.strokeColor = Color.white;
                painter.lineWidth = 2f;
                painter.BeginPath();
                painter.Arc(c, radius + 2f, 0f, 360f);
                painter.Stroke();
            }
        }

        static float ArcOf(RaceEvent.Athlete a, TrackPath path)
        {
            if (a.follower != null) return a.follower.S;
            if (a.heuristic != null && a.heuristic.path == path) return a.heuristic.S;
            Vector3 p = a.IsRL ? a.rig.BasePosition : (a.go != null ? a.go.transform.position : Vector3.zero);
            return path.ProjectGlobal(p);
        }

        /// <summary>The long jump's map: the runway, the board and the pit as a bar, the jumper as a dot on it.</summary>
        void DrawRunway(Painter2D painter, Rect r, LongJumpEvent jump)
        {
            LongJumpPit pit = jump.pit;
            if (pit == null) return;
            const float pad = 14f;
            float x0 = pit.runwayStartX, x1 = pit.pitFarX;
            float scale = (r.width - 2f * pad) / Mathf.Max(1f, x1 - x0);
            float y = r.y + r.height * 0.5f;
            float X(float x) => r.x + pad + (x - x0) * scale;

            painter.lineWidth = 6f;
            painter.strokeColor = new Color(1f, 1f, 1f, 0.45f);
            painter.BeginPath();
            painter.MoveTo(new Vector2(X(x0), y));
            painter.LineTo(new Vector2(X(pit.takeoffX), y));
            painter.Stroke();
            painter.strokeColor = new Color(0.9f, 0.8f, 0.55f, 0.7f);
            painter.BeginPath();
            painter.MoveTo(new Vector2(X(pit.pitNearX), y));
            painter.LineTo(new Vector2(X(x1), y));
            painter.Stroke();
            painter.lineWidth = 2f;
            painter.strokeColor = Color.white;
            painter.BeginPath();
            painter.MoveTo(new Vector2(X(pit.takeoffX), y - 10f));
            painter.LineTo(new Vector2(X(pit.takeoffX), y + 10f));
            painter.Stroke();

            RaceEvent.Athlete who = jump.Competitor;
            if (who == null) return;
            Vector3 p = who.IsRL ? who.rig.BasePosition : (who.go != null ? who.go.transform.position : Vector3.zero);
            float x = Mathf.Clamp(pit.Along(p), x0, x1);
            painter.fillColor = who.color;
            painter.BeginPath();
            painter.Arc(new Vector2(X(x), y), 7f, 0f, 360f);
            painter.Fill();
        }

        // ---------------------------------------------------------------- lower third

        bool Showing => director != null && director.OnIndividual && director.Featured != null
                        && race.Current != RaceEvent.Phase.Idle;

        void LowerThirdVisibility()
        {
            if (_lowerThird == null) return;
            bool show = Showing;
            _lowerThird.EnableInClassList("lower-third--in", show);
            _lowerThird.EnableInClassList("lower-third--out", !show);
        }

        void LowerThirdText(List<RaceEvent.Athlete> order)
        {
            if (_lowerName == null || director == null) return;
            RaceEvent.Athlete a = director.Featured;
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
            bool on = race.Current == RaceEvent.Phase.Countdown;
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
