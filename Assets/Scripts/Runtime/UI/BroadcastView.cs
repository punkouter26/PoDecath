using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using PoDecath.Audio;
using PoDecath.Cam;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// The broadcast overlay, in UI Toolkit: stage, leader and clock on one line along the top, a short
    /// running order down the left, the map and splits beside it, one card along the bottom for the caption
    /// and whoever is on camera, and the countdown over the middle of the picture.
    ///
    /// None of it is new information — <see cref="RaceEvent"/> already knows the order, the gaps and the
    /// clock, and <see cref="BroadcastDirector"/> already knows who is on air. What is new is that it
    /// moves. A running order that silently swaps two rows tells you nothing; one where the row that
    /// gained a place slides up and flashes green tells you a pass just happened, which is the single
    /// thing a viewer most wants to see and the thing a static table cannot say.
    ///
    /// Two of its pieces are controls as well as readings. A name in the running order, or the card at the
    /// bottom, locks the cameras onto that athlete (<see cref="BroadcastDirector.Pin"/>); the card again lets
    /// the director choose. And the "+n more" under the order opens the whole field and closes it again.
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
        [Tooltip("Optional. Without it the caption line stays off the card and nothing else changes.")]
        public Commentary commentary;
        [Tooltip("The HUD whose controls the card sits on, and whose STATS button adds the developer line to "
               + "it. Found in the scene if left empty.")]
        public HudView hud;

        [Header("Strain")]
        [Tooltip("Reading at which the strain bar is fully red. 1 would mean every joint pinned at its "
               + "torque limit at once, which never happens, so the bar would never leave the green.")]
        [Range(0.2f, 1f)] public float strainFullScale = 0.55f;

        [Header("Size")]
        [Tooltip("Rows in the running order when it is opened out. A deeper field gets a '+n more' line under it.")]
        public int orderRows = 8;
        [Tooltip("Rows in the running order as it normally stands: the leaders, and the last row for whoever "
               + "is on camera when they are further back.")]
        public int orderRowsShort = 4;
        [Tooltip("Splits kept on the board. The most recent few, oldest at the top.")]
        public int splitRows = 3;

        Label _stage, _lead, _clock, _overflow, _lowerName, _lowerDetail, _lowerPin, _lowerDev, _countdown;
        VisualElement _lowerThird, _lowerAthlete, _splitsPanel, _orderHost, _splitHost;
        VisualElement _strain, _strainFill;
        VisualElement _map;
        Label _captionText, _strainValue;
        string _shownCaption = "";
        bool _expanded;
        bool _statsWere;

        /// <summary>One row of the running order, kept so it can be re-used and animated rather than rebuilt.</summary>
        class Row
        {
            public VisualElement element;
            public Label rank, name, gap;
            public RaceEvent.Athlete athlete;
            public float flash;      // seconds left on the gained/lost highlight
            public bool gained;
        }

        /// <summary>One running-order row plus its gap (.order-row in Theme.uss: 52 + 2), in reference pixels.</summary>
        const float RowPitch = 54f;

        readonly List<Row> _rows = new List<Row>();
        readonly List<RaceEvent.Athlete> _shown = new List<RaceEvent.Athlete>();
        readonly Dictionary<RaceEvent.Athlete, int> _lastPlace = new Dictionary<RaceEvent.Athlete, int>();
        readonly Dictionary<RaceEvent.Athlete, int> _lastSlot = new Dictionary<RaceEvent.Athlete, int>();
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
            _lowerAthlete = Find<VisualElement>("lower-athlete");
            _lowerName = Find<Label>("lower-name");
            _lowerDetail = Find<Label>("lower-detail");
            _lowerPin = Find<Label>("lower-pin");
            _lowerDev = Find<Label>("lower-dev");
            _countdown = Find<Label>("countdown");
            _splitsPanel = Find<VisualElement>("splits");
            _orderHost = Find<VisualElement>("order-rows");
            _splitHost = Find<VisualElement>("split-rows");
            _captionText = Find<Label>("caption-text");
            _strain = Find<VisualElement>("strain");
            _strainFill = Find<VisualElement>("strain-fill");
            _strainValue = Find<Label>("strain-value");
            _map = Find<VisualElement>("track-map");
            if (_map != null) _map.generateVisualContent += DrawMap;

            _overflow?.RegisterCallback<ClickEvent>(_ => { _expanded = !_expanded; _nextRefresh = 0f; });
            _lowerThird?.RegisterCallback<ClickEvent>(_ => OnCardTapped());

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
                var row = new Row { element = element, rank = rank, name = name, gap = gap };
                // A name is a way to follow that runner: the cameras lock on, and the card says so.
                element.RegisterCallback<ClickEvent>(_ => { if (row.athlete != null) Pin(row.athlete); });
                _rows.Add(row);
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
                _lastPlace.Clear();
                _lastSlot.Clear();
                foreach (Row r in _rows) r.athlete = null;
            }

            if (hud == null) hud = FindFirstObjectByType<HudView>();
            // STATS answers on the frame it is pressed, not on the next tenth of a second.
            if (StatsOn != _statsWere) { _statsWere = StatsOn; _nextRefresh = 0f; }
            Countdown();
            SitOnControls();
            Card();
            DecayFlashes();

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.1f;
            Refresh();
        }

        /// <summary>
        /// Rests the card on top of the HUD's controls, measured, rather than on a margin guessed from what
        /// the control bar used to be. Every document shares one PanelSettings, so the HUD's world rect is in
        /// this panel's coordinates. Falls back to the stylesheet's margin when there is no HUD or it has
        /// not been laid out yet.
        /// </summary>
        void SitOnControls()
        {
            if (_lowerThird == null) return;
            float top = hud != null ? hud.ControlsTop : float.NaN;
            VisualElement safe = Root?.Q<VisualElement>("body");
            if (float.IsNaN(top) || safe == null) { _lowerThird.style.marginBottom = StyleKeyword.Null; return; }
            float contentBottom = safe.worldBound.yMax - safe.resolvedStyle.paddingBottom;
            float margin = Mathf.Max(0f, contentBottom - top + 8f);
            if (Mathf.Abs(_lowerThird.resolvedStyle.marginBottom - margin) > 1f) _lowerThird.style.marginBottom = margin;
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
        /// The strain bar on the card: how hard the athlete on camera is pulling, as a fraction of what its
        /// drives are configured to allow.
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
            RaceEvent.Athlete a = OnCard;
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
        /// One line along the top: the stage, who leads and by how much, and the clock. The lead line wraps
        /// onto a second small line rather than being cut off, so a long name never pushes the clock over.
        /// </summary>
        void TopBar(List<RaceEvent.Athlete> order, RaceEvent.Athlete leader)
        {
            SetText(_stage, Stage(leader));
            SetText(_clock, Clock());
            if (_lead == null) return;

            if (race.Current == RaceEvent.Phase.Countdown) { SetText(_lead, "ON YOUR MARKS", Color.white); return; }
            if (leader == null) { SetText(_lead, ""); return; }

            if (Jump != null)
            {
                SetText(_lead, leader.finished ? $"{leader.name} leads, {leader.distance:F2} m" : $"{leader.name} leads", leader.color);
                return;
            }
            RaceEvent.Athlete second = order.Count > 1 ? order[1] : null;
            if (second == null) { SetText(_lead, leader.name, leader.color); return; }
            if (leader.finished && second.finished) { SetText(_lead, $"{leader.name} by {second.time - leader.time:F2} s", leader.color); return; }
            float gap = leader.distance - second.distance;
            SetText(_lead, gap < 0.6f ? $"{leader.name}, nothing in it" : $"{leader.name} by {gap:F1} m", leader.color);
        }

        /// <summary>Where the event has got to, in the words the event itself would use.</summary>
        string Stage(RaceEvent.Athlete leader)
        {
            LongJumpEvent jump = Jump;
            if (jump != null) return $"ROUND {jump.Round}/{Mathf.Max(1, jump.attemptsEach)}";

            LapEvent lap = Lap;
            if (lap == null || lap.path == null) return $"{race.raceDistance:F0} M";
            if (lap.laps <= 1) return $"{race.raceDistance:F0} M";
            float covered = leader != null ? Mathf.Max(0f, leader.distance) : 0f;
            int onLap = Mathf.Clamp(Mathf.FloorToInt(covered / lap.path.LapLength) + 1, 1, lap.laps);
            return onLap == lap.laps ? "LAST LAP" : $"LAP {onLap}/{lap.laps}";
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
        /// Who gets a row. Opened out, the top of the order. Normally the leaders and, on the last row,
        /// whoever is on camera when they are further back than that: the row a viewer looks for is the one
        /// for the athlete they are watching, and the map beside it already shows where everyone else is.
        /// </summary>
        void PickShown(List<RaceEvent.Athlete> order)
        {
            _shown.Clear();
            int rows = Mathf.Min(_rows.Count, _expanded ? orderRows : orderRowsShort);
            if (order.Count <= rows) { _shown.AddRange(order); return; }
            RaceEvent.Athlete onCamera = OnCard;
            bool extra = !_expanded && onCamera != null && order.IndexOf(onCamera) >= rows - 1;
            int top = extra ? rows - 1 : rows;
            for (int i = 0; i < top; i++) _shown.Add(order[i]);
            if (extra) _shown.Add(onCamera);
        }

        /// <summary>
        /// Paints the running order and animates any change in it. A row whose athlete has moved to another
        /// row is translated from where it used to be and released, so the style system slides it into
        /// place; a change of place, up or down, decides which colour it flashes.
        /// </summary>
        void OrderStrip(List<RaceEvent.Athlete> order, RaceEvent.Athlete leader)
        {
            PickShown(order);
            RaceEvent.Athlete pinned = director != null ? director.Pinned : null;

            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                bool used = i < _shown.Count;
                Show(row.element, used);
                if (!used) { row.athlete = null; continue; }

                RaceEvent.Athlete a = _shown[i];
                int place = order.IndexOf(a);

                SetText(row.rank, (place + 1).ToString());
                SetText(row.name, a.name, a.color);
                SetText(row.gap, Gap(a, leader, place));
                row.gap.style.color = a.fell ? new Color(1f, 0.5f, 0.42f)
                                    : a.recovering ? new Color(1f, 0.78f, 0.33f)   // amber: in trouble, not out
                                    : a.finished ? Color.white
                                    : new Color(0.72f, 0.76f, 0.82f);
                row.element.EnableInClassList("order-row--lead", place == 0);
                row.element.EnableInClassList("order-row--pinned", a == pinned);
                // The row after a gap in the order (the athlete on camera, further back) is set apart.
                row.element.EnableInClassList("order-row--apart", i > 0 && place != order.IndexOf(_shown[i - 1]) + 1);

                if (_lastSlot.TryGetValue(a, out int wasSlot) && wasSlot != i)
                {
                    // Start it where it was and let the transition carry it to where it is now.
                    row.element.style.translate = new StyleTranslate(new Translate(0f, (wasSlot - i) * RowPitch));
                    row.element.schedule.Execute(() => row.element.style.translate = new StyleTranslate(new Translate(0f, 0f))).StartingIn(0);
                }
                if (_lastPlace.TryGetValue(a, out int wasPlace) && wasPlace != place)
                {
                    row.gained = place < wasPlace;
                    row.flash = 0.9f;
                    row.element.EnableInClassList("order-row--gained", row.gained);
                    row.element.EnableInClassList("order-row--lost", !row.gained);
                }
                row.athlete = a;
            }

            _lastSlot.Clear();
            for (int i = 0; i < _shown.Count; i++) _lastSlot[_shown[i]] = i;
            _lastPlace.Clear();
            for (int i = 0; i < order.Count; i++) _lastPlace[order[i]] = i;

            if (_overflow == null) return;
            int hidden = order.Count - _shown.Count;
            bool canOpen = order.Count > orderRowsShort;
            Show(_overflow, canOpen);
            if (canOpen) SetText(_overflow, _expanded ? "show fewer" : $"+{hidden} more");
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

        // ---------------------------------------------------------------- the card

        /// <summary>The athlete the card is about: whoever the gallery is on, or the reference when there is no director.</summary>
        RaceEvent.Athlete OnCard => director != null ? director.Featured : race != null ? race.Reference : null;

        bool StatsOn => hud != null && hud.StatsOn;

        /// <summary>
        /// The athlete half of the card is up while the gallery is on one athlete, while the viewer has
        /// locked the cameras onto one, and while STATS is on (the developer line needs somebody to be
        /// about). The card itself is up whenever either half has something to say.
        /// </summary>
        bool AthleteShowing => OnCard != null && race.Current != RaceEvent.Phase.Idle
                               && (director == null || director.OnIndividual || director.Pinned != null || StatsOn);

        /// <summary>
        /// The caption line and the athlete on camera, one card. The caption is driven off
        /// <see cref="Commentary.Caption"/> rather than off its event, so a line said while this screen was
        /// hidden behind the results card does not leave the card stuck on when it comes back.
        /// </summary>
        void Card()
        {
            if (_lowerThird == null) return;
            string line = commentary != null ? commentary.Caption : "";
            bool caption = !string.IsNullOrEmpty(line);
            if (caption && line != _shownCaption) { _shownCaption = line; SetText(_captionText, line); }
            else if (!caption) _shownCaption = "";
            Show(_captionText, caption);

            bool athlete = AthleteShowing;
            Show(_lowerAthlete, athlete);
            bool show = caption || athlete;
            _lowerThird.EnableInClassList("lower-third--in", show);
            _lowerThird.EnableInClassList("lower-third--out", !show);
            _lowerThird.EnableInClassList("lower-third--caption-only", caption && !athlete);
        }

        /// <summary>A tap on the card locks the cameras onto its athlete; a tap while locked hands them back.</summary>
        void OnCardTapped()
        {
            if (director == null) return;
            if (director.Pinned != null) { director.Pin(null); _nextRefresh = 0f; return; }
            if (OnCard != null) Pin(OnCard);
        }

        /// <summary>
        /// Locks the cameras onto whoever is <paramref name="place"/> in the order (0 is the leader, -1 hands
        /// them back) and opens the order out or not; what a tap on a name and on "+n more" do, callable by
        /// the UI captures.
        /// </summary>
        public void PinPlace(int place, bool expanded)
        {
            _expanded = expanded;
            List<RaceEvent.Athlete> order = race != null ? race.LiveOrder() : null;
            if (director != null) director.Pin(order != null && place >= 0 && place < order.Count ? order[place] : null);
            _nextRefresh = 0f;
        }

        void Pin(RaceEvent.Athlete a)
        {
            if (director == null) return;
            director.Pin(director.Pinned == a ? null : a);
            _nextRefresh = 0f;
        }

        void LowerThirdText(List<RaceEvent.Athlete> order)
        {
            RaceEvent.Athlete a = OnCard;
            if (_lowerName == null || a == null) return;

            SetText(_lowerName, a.name, a.color);
            Show(_lowerPin, director != null && director.Pinned == a);
            DevLine(a);
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

        /// <summary>
        /// STATS on the card: which policy is driving this body, what the event is doing, how upright the
        /// body is now (the same 0 to 100% the fall detector grades against) and the attempt. These were a
        /// second panel above the controls for the reference athlete only, while the card above it was
        /// about somebody else.
        /// </summary>
        void DevLine(RaceEvent.Athlete a)
        {
            bool on = StatsOn;
            Show(_lowerDev, on);
            if (!on || _lowerDev == null) return;
            string model = a.runner != null ? a.runner.ModelName : "no policy";
            string phase = a.fell ? "FELL" : a.recovering ? "GETTING UP" : race.Status;
            string up = a.IsRL
                ? $"{Mathf.Clamp01((a.rig.UprightDot - race.fallUprightDot) / (1f - race.fallUprightDot)) * 100f:F0}% up"
                : "-";
            SetText(_lowerDev, $"{model}  ·  {phase}  ·  {up}  ·  #{race.Attempt + 1}");
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
