using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
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
        public string setupSceneName = "RaceSetup";

        VisualElement _rootEl, _modal, _rows;
        Label _title, _subtitle;
        Button _again, _change;
        bool _hidStats, _hidHud, _hidOverlay;

        protected override void Build()
        {
            _rootEl = Find<VisualElement>("root");
            _modal = Find<VisualElement>("modal");
            _rows = Find<VisualElement>("rows");
            _title = Find<Label>("title");
            _subtitle = Find<Label>("subtitle");
            _again = Find<Button>("again");
            _change = Find<Button>("change");

            if (_again != null) _again.clicked += RaceAgain;
            if (_change != null) _change.clicked += ChangeRunners;
            if (race != null) race.RaceComplete += Show;
            Hide();
        }

        void OnDisable()
        {
            if (race != null) race.RaceComplete -= Show;
        }

        public void Show(List<RaceEvent.RaceResult> results)
        {
            if (_rows == null) return;
            _rows.Clear();

            for (int i = 0; i < results.Count; i++)
            {
                RaceEvent.RaceResult r = results[i];
                var row = new VisualElement();
                row.AddToClassList("result-row");
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

                row.Add(rank);
                row.Add(name);
                row.Add(time);
                _rows.Add(row);

                // Rows arrive one after another rather than all at once, fastest at the top: the eye reads
                // a podium in order, and this puts the order into the animation instead of only the layout.
                row.style.opacity = 0f;
                int delay = 40 + i * 45;
                row.schedule.Execute(() => row.style.opacity = 1f).StartingIn(delay);
            }

            SetText(_title, "RESULTS");
            // The event words its own sub-heading: a lap race counts finishers, the long jump counts marks.
            if (race != null) SetText(_subtitle, race.ResultsSubtitle(results));

            Backdrop(false);
            Show(_rootEl, true);
            if (_modal == null) return;
            _modal.EnableInClassList("modal--out", true);
            _modal.EnableInClassList("modal--in", false);
            _modal.schedule.Execute(() =>
            {
                _modal.EnableInClassList("modal--out", false);
                _modal.EnableInClassList("modal--in", true);
            }).StartingIn(16);
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
