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
        [Tooltip("Optional. The photo finish shown under the heading. Found in the scene if left empty.")]
        public PhotoFinish photoFinish;

        VisualElement _rootEl, _modal, _rows, _seasonPanel, _seasonRows, _clipLine, _replay, _scrim;
        Label _title, _subtitle, _seasonTitle, _clipText, _nextLabel;
        Button _again, _change, _next, _watch;

        // The replay: the clip ring decoded one frame at a time into one texture, at the clip's own rate.
        Texture2D _replayTex;
        Color32[] _replayPx;
        int _replayFrame;
        float _nextReplayFrame;
        bool _watching;

        VisualElement _photo, _photoStrip;
        Label _photoCaption;
        readonly List<PhotoFinish.Crossing> _photoMarks = new List<PhotoFinish.Crossing>();
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
            _replay = Find<VisualElement>("replay");
            _scrim = Find<VisualElement>("scrim");
            _watch = Find<Button>("watch");
            _nextLabel = Find<Label>("next-label");
            _photo = Find<VisualElement>("photo");
            _photoStrip = Find<VisualElement>("photo-strip");
            _photoCaption = Find<Label>("photo-caption");
            if (_photoStrip != null) _photoStrip.generateVisualContent += DrawPhoto;

            if (_again != null) _again.clicked += RaceAgain;
            if (_change != null) _change.clicked += ChangeRunners;
            if (_next != null) _next.clicked += NextEvent;
            if (_watch != null) _watch.clicked += () => Watch(true);
            // A tap anywhere on the picture while watching brings the card back.
            _replay?.RegisterCallback<PointerDownEvent>(_ => { if (_watching) Watch(false); });
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
            if (clip == null || !ScreenVisible) return;
            TickReplay();
            // The clip is encoded on a worker thread after the card is already up, so its line is polled
            // rather than set once: "Saving clip..." turns into the file name when the write finishes.
            if (_clipLine == null) return;
            string status = clip.Status;
            Show(_clipLine, !string.IsNullOrEmpty(status));
            SetText(_clipText, status);
        }

        void OnDestroy()
        {
            if (_replayTex != null) Destroy(_replayTex);
        }

        /// <summary>
        /// Plays the race's highlight clip behind the card: the last few seconds up to the first finisher,
        /// straight out of the capture ring, looping. Results and replay on one screen, with no second page
        /// and no video decoder: the ring is palette indices, so a frame is one lookup per pixel into the
        /// GIF palette, 130k pixels twelve times a second.
        /// </summary>
        void TickReplay()
        {
            if (_replay == null) return;
            IReadOnlyList<byte[]> frames = clip.LastFrames;
            bool have = frames != null && frames.Count >= 2;
            Show(_replay, have);
            Show(_watch, have);
            _scrim?.EnableInClassList("modal-scrim--replay", have);
            if (!have || Time.unscaledTime < _nextReplayFrame) return;
            _nextReplayFrame = Time.unscaledTime + 1f / Mathf.Max(1, clip.fps);

            int w = clip.FrameWidth, h = clip.FrameHeight;
            if (w < 2 || h < 2) return;
            if (_replayTex == null || _replayTex.width != w || _replayTex.height != h)
            {
                if (_replayTex != null) Destroy(_replayTex);
                _replayTex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "ResultsReplay", wrapMode = TextureWrapMode.Clamp };
                _replayPx = new Color32[w * h];
                _replay.style.backgroundImage = new StyleBackground(_replayTex);
            }

            byte[] f = frames[_replayFrame++ % frames.Count];
            if (f == null || f.Length < w * h) return;
            Color32[] palette = Fx.GifWriter.Palette;
            // The ring is top row first; a texture's row 0 is the bottom.
            for (int y = 0; y < h; y++)
            {
                int src = y * w, dst = (h - 1 - y) * w;
                for (int x = 0; x < w; x++) _replayPx[dst + x] = palette[f[src + x]];
            }
            _replayTex.SetPixels32(_replayPx);
            _replayTex.Apply(false);
        }

        /// <summary>
        /// The photo finish under the heading, when the line camera caught one: the strip, a tick where each
        /// of the first three finishers crossed, and a caption with the margin that the picture settles. Left
        /// off a sixteen-row board, which has no height to spare for it.
        /// </summary>
        void PhotoStrip(List<RaceEvent.RaceResult> results, bool compact)
        {
            if (_photo == null) return;
            if (photoFinish == null) photoFinish = FindFirstObjectByType<PhotoFinish>();
            _photoMarks.Clear();
            if (photoFinish != null)
                foreach (PhotoFinish.Crossing c in photoFinish.Crossings)
                    if (_photoMarks.Count < 3) _photoMarks.Add(c);

            bool show = !compact && photoFinish != null && photoFinish.Strip != null && _photoMarks.Count > 0;
            Show(_photo, show);
            if (!show) return;

            // The camera is on the painted line, and the grid is staggered forward of it, so this is the order
            // at the line rather than the result: the caption says which it is.
            string caption = $"LINE CAMERA  ·  first across {_photoMarks[0].name}";
            if (_photoMarks.Count > 1) caption += $", then {_photoMarks[1].name} +{_photoMarks[1].time - _photoMarks[0].time:F2} s";
            SetText(_photoCaption, caption);
            _photoStrip?.MarkDirtyRepaint();
        }

        /// <summary>
        /// The strip as a textured quad cropped to the columns actually recorded, then the finishers' ticks
        /// over it. A background image would stretch the whole strip, blank tail included.
        /// </summary>
        void DrawPhoto(MeshGenerationContext ctx)
        {
            PhotoFinish p = photoFinish;
            Rect r = ctx.visualElement.contentRect;
            if (p == null || p.Strip == null || r.width < 2f || r.height < 2f) return;

            float u = Mathf.Clamp01(p.Columns / (float)Mathf.Max(1, p.Strip.width));
            MeshWriteData mesh = ctx.Allocate(4, 6, p.Strip);
            // UI space runs y down; texture v runs up.
            mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMin, r.yMin, Vertex.nearZ), tint = Color.white, uv = new Vector2(0f, 1f) });
            mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMax, r.yMin, Vertex.nearZ), tint = Color.white, uv = new Vector2(u, 1f) });
            mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMax, r.yMax, Vertex.nearZ), tint = Color.white, uv = new Vector2(u, 0f) });
            mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMin, r.yMax, Vertex.nearZ), tint = Color.white, uv = new Vector2(0f, 0f) });
            mesh.SetNextIndex(0); mesh.SetNextIndex(1); mesh.SetNextIndex(2);
            mesh.SetNextIndex(0); mesh.SetNextIndex(2); mesh.SetNextIndex(3);

            float span = Mathf.Max(0.01f, p.EndTime - p.StartTime);
            Painter2D painter = ctx.painter2D;
            painter.lineWidth = 3f;
            foreach (PhotoFinish.Crossing m in _photoMarks)
            {
                float x = r.xMin + Mathf.Clamp01((m.time - p.StartTime) / span) * r.width;
                painter.strokeColor = m.color;
                painter.BeginPath();
                painter.MoveTo(new Vector2(x, r.yMin));
                painter.LineTo(new Vector2(x, r.yMax));
                painter.Stroke();
            }
        }

        /// <summary>Takes the card off the replay, or puts it back.</summary>
        void Watch(bool watching)
        {
            _watching = watching;
            _scrim?.EnableInClassList("modal-scrim--watch", watching);
            _replay?.EnableInClassList("replay--watch", watching);
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

            PhotoStrip(results, compact);
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
            if (hasNext) SetText(_nextLabel, next);
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
