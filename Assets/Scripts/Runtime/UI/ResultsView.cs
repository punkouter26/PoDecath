using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using PoDecath.Fx;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// The results card, shown once every runner has finished or fallen.
    ///
    /// Rows are built from <see cref="RaceEvent.Results"/> when the card opens rather than pre-made one per
    /// grid slot, so a field of two does not carry fourteen hidden rows around with it. Each is tinted with
    /// its athlete's own colour, which — with the house rule that athletes keep the textures they were
    /// imported with — is the same colour their trail was wearing on the deck a moment earlier.
    ///
    /// The card scales up as it fades in. It is the one moment in a race where a piece of UI is the whole
    /// screen, and arriving instantly at full size reads as a glitch.
    /// </summary>
    [DefaultExecutionOrder(135)]
    public class ResultsView : UiRoot
    {
        [Header("Wiring")]
        public RaceEvent race;
        [Tooltip("Put away while the results are up; the live HUD reads through the dimmed backdrop otherwise.")]
        public HudView hud;
        [Tooltip("Also put away: the broadcast overlay, which is a live picture over a finished race.")]
        public BroadcastView overlay;
        public string setupSceneName = "MAIN";
        [Tooltip("Optional. Points, personal bests and the season table come from here.")]
        public SeasonKeeper season;
        [Tooltip("Optional. Says where the highlight clip of this race was saved.")]
        public HighlightClip clip;

        VisualElement _rootEl, _modal, _rows, _seasonPanel, _seasonRows, _clipLine;
        Label _title, _subtitle, _seasonTitle, _clipText;
        Button _again, _change, _next;
        bool _hidStats, _hidHud, _hidOverlay;

        /// <summary>The parts of a row the season decoration fills in once the points are known.</summary>
        struct RowParts { public VisualElement row; public Label points; }
        readonly Dictionary<string, RowParts> _parts = new Dictionary<string, RowParts>();

        protected override void Build()
        {
            _rootEl = Find<VisualElement>("root");
            _modal = Find<VisualElement>("modal");
            _rows = Find<VisualElement>("rows");
            _title = Find<Label>("title");
            _subtitle = Find<Label>("subtitle");
            _again = Find<Button>("again");
            _change = Find<Button>("change");
            _next = Find<Button>("next");
            _seasonPanel = Find<VisualElement>("season");
            _seasonRows = Find<VisualElement>("season-rows");
            _seasonTitle = Find<Label>("season-title");
            _clipLine = Find<VisualElement>("clip");
            _clipText = Find<Label>("clip-text");

            if (_again != null) _again.clicked += RaceAgain;
            if (_change != null) _change.clicked += ChangeRunners;
            if (_next != null) _next.clicked += NextEvent;
            if (race != null) race.RaceComplete += Show;
            if (season != null) season.Scored += Decorate;
            Hide();
        }

        void OnDisable()
        {
            if (race != null) race.RaceComplete -= Show;
            if (season != null) season.Scored -= Decorate;
        }

        protected override void Update()
        {
            base.Update();
            // The clip is encoded on a worker thread after the card is already up, so its line is polled
            // rather than set once: "Saving clip..." turns into the file name when the write finishes.
            if (_clipLine == null || clip == null || !ScreenVisible) return;
            string status = clip.Status;
            Show(_clipLine, !string.IsNullOrEmpty(status));
            SetText(_clipText, status);
        }

        public void Show(List<RaceEvent.RaceResult> results)
        {
            // Nothing to draw into means this document did not load, and the one thing that must not
            // happen then is telling the event that a card is up. Leaving it unacknowledged is what lets
            // RaceEvent.TickFinished notice the race has no exit and restart it anyway.
            if (_rows == null)
            {
                Debug.LogError("[ResultsView] the Results document has no content, so no results card can "
                             + "be shown. Check Assets/UI/Results.uxml imported (an XML error there makes "
                             + "it load as an empty asset).", this);
                return;
            }
            _rows.Clear();
            _parts.Clear();
            // Past eight rows the card would need to scroll on a phone; compact rows keep sixteen on one screen.
            bool compact = results.Count > 8;

            for (int i = 0; i < results.Count; i++)
            {
                RaceEvent.RaceResult r = results[i];
                var row = new VisualElement();
                row.AddToClassList("result-row");
                if (compact) row.AddToClassList("result-row--compact");
                if (r.finished && r.rank <= 3) row.AddToClassList("result-row--podium");

                // A runner who did not finish is dimmed rather than removed: the board has to show that
                // they were in the race and what happened to them.
                Color c = r.finished ? r.color : new Color(r.color.r, r.color.g, r.color.b, 0.55f);

                var rank = new Label(r.finished ? r.rank.ToString() : "-");
                rank.AddToClassList("result-rank");
                rank.style.color = c;

                var name = new Label(r.name);
                name.AddToClassList("result-name");
                name.style.color = c;

                var time = new Label(r.Status);
                time.AddToClassList("result-time");
                time.style.color = r.finished ? Color.white : new Color(1f, 0.55f, 0.45f);

                // Decathlon points, filled in by Decorate once the season keeper has scored the race.
                var points = new Label("");
                points.AddToClassList("result-points");

                row.Add(rank);
                row.Add(name);
                row.Add(time);
                row.Add(points);
                _rows.Add(row);
                _parts[r.name] = new RowParts { row = row, points = points };

                // Rows arrive one after another rather than all at once, fastest at the top: the eye reads
                // a podium in order, and this puts the order into the animation instead of only the layout.
                row.style.opacity = 0f;
                int delay = 40 + i * 45;
                row.schedule.Execute(() => row.style.opacity = 1f).StartingIn(delay);
            }

            SetText(_title, "RESULTS");
            // The event words its own sub-heading: a lap race counts finishers, the long jump counts marks.
            if (race != null) SetText(_subtitle, race.ResultsSubtitle(results));

            Show(_seasonPanel, false);
            Show(_next, false);
            Show(_again, true);
            if (season != null && race != null && season.ScoredAttempt == race.Attempt) Decorate();

            Backdrop(false);
            Show(_rootEl, true);

            // The card is up and owns the exit from here. This is what stops the event restarting the
            // race underneath it -- which RooftopLap did, because it kept autoRestart on -- and equally
            // what stops the dead-end guard firing while somebody is reading the board. Announced before
            // the entrance animation, not after: a card that came up without its scale-in is still a card.
            if (race != null) race.NotifyResultsShown();

            if (_modal == null) return;
            _modal.EnableInClassList("modal--out", true);
            _modal.EnableInClassList("modal--in", false);
            _modal.schedule.Execute(() =>
            {
                _modal.EnableInClassList("modal--out", false);
                _modal.EnableInClassList("modal--in", true);
            }).StartingIn(16);
        }

        /// <summary>
        /// Points and personal bests on each row, then the season table and the NEXT EVENT button when this
        /// race was a season leg. Runs when the keeper reports in, which may be before or after the card
        /// was built: both subscribe to the same event, and nothing decides which hears it first.
        /// </summary>
        void Decorate()
        {
            if (season == null) return;
            foreach (KeyValuePair<string, SeasonKeeper.Award> kv in season.Awards)
            {
                if (!_parts.TryGetValue(kv.Key, out RowParts parts)) continue;
                SetText(parts.points, $"{kv.Value.points} pts");
                if (kv.Value.personalBest && parts.row.Q(className: "pb-badge") == null)
                {
                    var badge = new VisualElement { tooltip = kv.Value.firstMark ? "first mark" : "personal best" };
                    badge.AddToClassList("pb-badge");
                    // After the name, so the star sits against it rather than drifting to the time column.
                    parts.row.Insert(2, badge);
                }
            }
            SeasonTable();
        }

        void SeasonTable()
        {
            if (_seasonPanel == null || season == null || !season.ScoredForSeason) return;
            SeasonStore.Season s = SeasonStore.Current.season;
            List<SeasonStore.Standing> table = SeasonStore.Table();
            bool over = s.Finished;
            string next = !over && s.next < s.legs.Count ? s.legs[s.next].label : "";
            SetText(_seasonTitle, over
                ? $"SEASON OVER  ·  champion {s.champion}"
                : $"SEASON  ·  {s.next} of {s.legs.Count} done  ·  next {next}");

            _seasonRows?.Clear();
            // Top five, or three when the board above is already in compact rows.
            int shown = Mathf.Min(table.Count, _parts.Count > 8 ? 3 : 5);
            for (int i = 0; i < shown; i++)
            {
                SeasonStore.Standing st = table[i];
                var line = new VisualElement();
                line.AddToClassList("season-line");
                var rank = new Label((i + 1).ToString());
                rank.AddToClassList("season-rank");
                var who = new Label(st.name);
                who.AddToClassList("season-name");
                who.style.color = st.Colour;
                var total = new Label($"{st.total} pts");
                total.AddToClassList("season-total");
                line.Add(rank); line.Add(who); line.Add(total);
                _seasonRows?.Add(line);
            }
            Show(_seasonPanel, true);

            // During a season the next event is the point of the card, so NEXT takes the place of RACE AGAIN.
            // A race run again after the season has moved on is a free race and would not be scored anyway.
            bool hasNext = SeasonStore.NextLeg != null;
            Show(_next, hasNext);
            Show(_again, !hasNext);
            if (_next != null && hasNext) _next.text = $"NEXT: {next}";
        }

        void NextEvent()
        {
            Hide();
            SeasonKeeper.NextLeg();
        }

        public void Hide()
        {
            Show(_rootEl, false);
            if (_modal != null)
            {
                _modal.EnableInClassList("modal--in", false);
                _modal.EnableInClassList("modal--out", true);
            }
            Backdrop(true);
        }

        /// <summary>
        /// Puts the live HUD and overlay away behind the results and back afterwards. What goes back is
        /// what was up when the card appeared, not everything: the developer stats card is hidden by
        /// default in the broadcast scenes, and dismissing a results card is no reason to hand it to the
        /// viewer.
        /// </summary>
        void Backdrop(bool visible)
        {
            if (!visible)
            {
                _hidStats = hud != null && hud.StatsOpen;
                _hidHud = hud != null && hud.ScreenVisible;
                _hidOverlay = overlay != null && overlay.ScreenVisible;
                // The whole HUD, not just the stats card. A Restart button sitting under a modal scrim is
                // dimmed but still plainly there, and a control you can see and cannot press reads as a bug.
                if (hud != null) hud.SetScreenVisible(false);
                if (overlay != null) overlay.SetScreenVisible(false);
                return;
            }
            if (hud != null && _hidHud) hud.SetScreenVisible(true);
            if (hud != null && _hidStats) hud.SetStatsVisible(true);
            if (overlay != null && _hidOverlay) overlay.SetScreenVisible(true);
        }

        void RaceAgain()
        {
            Hide();
            if (race != null) race.RestartNow();
        }

        void ChangeRunners()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(setupSceneName);
        }
    }
}
