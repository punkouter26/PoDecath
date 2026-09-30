using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Pictures of the menus, and the one-screen rule checked by measurement rather than by eye.
    ///
    /// The sweep's camera capture and the bridges' camera grabs leave UI Toolkit out, so until now the
    /// only pictures of the menus were the phone's own screenshots. The Game view's composited backbuffer
    /// does carry the panels (<c>unity command capture_game_view --source screen</c>); what it lacked was
    /// the phone's shape. The two size items put the Game view on a real portrait resolution, and the
    /// check walks every visible element of every panel against the rules the owner set:
    ///
    ///   - nothing interactive or legible off the screen, and nothing that would need a scroll to reach
    ///   - no visible ScrollView whose content is taller than its viewport
    ///   - no text smaller than the legibility floor (at the 1080 x 1920 reference)
    ///   - no label whose text is wider than the box it has, unless it wraps
    ///   - the five frame anchors where they belong: TL title, TC FPS, TR MENU, BL DEBUG, BR version
    ///
    /// Everything here reads; nothing lays anything out. Run it in play mode, on any scene, with any
    /// screen up, and it writes one JSON beside the console line: <c>Build/UiShots/layout_*.json</c>.
    /// </summary>
    public static class UiShots
    {
        /// <summary>Text below this many reference pixels is flagged. 26 px at 1080 wide is about 9 pt on a 6" phone.</summary>
        public const float MinFontPx = 26f;

        const string OutDir = "Build/UiShots";

        [MenuItem("PoDecath/UI/Game View 1080x1920 (9:16)")]
        public static void GameViewPortrait() => SetGameViewSize(1080, 1920, "PoDecath 9:16");

        [MenuItem("PoDecath/UI/Game View 1080x2400 (9:20)")]
        public static void GameViewTall() => SetGameViewSize(1080, 2400, "PoDecath 9:20");

        [MenuItem("PoDecath/UI/Check Layout")]
        public static void CheckLayoutMenu()
        {
            Report r = CheckLayout();
            Debug.Log(r.Summary());
        }

        // ---------------------------------------------------------------- capture

        /// <summary>
        /// A picture of what the phone would show, at an exact resolution, whatever shape the Game view
        /// happens to be: the main camera rendered into one texture, every live panel re-targeted into a
        /// second, the two laid over each other. The bridge's own screen grab is scaled to the editor window
        /// (1280 x 720 whatever the Game view is set to) and cannot be trusted for a portrait layout.
        ///
        /// The panels only repaint inside the player loop, so this steps the editor a few frames while
        /// they point at the texture; call it in play mode, paused (<c>EditorApplication.Step</c> is how
        /// the pipeline drives play mode on an unfocused editor anyway). The shared PanelSettings asset is
        /// put back exactly as it was in a finally, because a target texture left on it would be saved.
        /// </summary>
        public static string Capture(string fileName, int width = 1080, int height = 1920)
        {
            var docs = new List<UIDocument>();
            // A panel that already draws into a texture (the gantry clock) is part of the 3D picture, not
            // of the screen's UI: leave it where it is.
            foreach (UIDocument d in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude))
                if (d.isActiveAndEnabled && d.panelSettings != null && d.panelSettings.targetTexture == null) docs.Add(d);

            var settings = new List<PanelSettings>();
            foreach (UIDocument d in docs) if (!settings.Contains(d.panelSettings)) settings.Add(d.panelSettings);

            var saved = new List<(PanelSettings ps, RenderTexture rt, bool clear, Color clearValue)>();
            RenderTexture camRt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture uiRt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D cam = null, ui = null;
            try
            {
                foreach (PanelSettings ps in settings)
                {
                    saved.Add((ps, ps.targetTexture, ps.clearColor, ps.colorClearValue));
                    ps.targetTexture = uiRt;
                    ps.clearColor = settings.IndexOf(ps) == 0;
                    ps.colorClearValue = Color.clear;
                }

                bool wasPaused = EditorApplication.isPaused;
                if (EditorApplication.isPlaying)
                {
                    EditorApplication.isPaused = true;
                    for (int i = 0; i < 3; i++) EditorApplication.Step();
                    EditorApplication.isPaused = wasPaused;
                }

                Camera main = Camera.main;
                RenderTexture prevActive = RenderTexture.active;
                RenderTexture.active = camRt;
                GL.Clear(true, true, new Color(0.04f, 0.05f, 0.07f, 1f));
                RenderTexture.active = prevActive;
                if (main != null)
                {
                    RenderTexture prevTarget = main.targetTexture;
                    main.targetTexture = camRt;
                    main.Render();
                    main.targetTexture = prevTarget;
                }

                cam = Read(camRt);
                ui = Read(uiRt);
                Color32[] c = cam.GetPixels32(), u = ui.GetPixels32();
                for (int i = 0; i < c.Length; i++)
                {
                    // The panel writes premultiplied colour over a cleared (0,0,0,0) target.
                    float a = u[i].a / 255f;
                    c[i] = new Color32(
                        (byte)Mathf.Min(255, u[i].r + c[i].r * (1f - a)),
                        (byte)Mathf.Min(255, u[i].g + c[i].g * (1f - a)),
                        (byte)Mathf.Min(255, u[i].b + c[i].b * (1f - a)),
                        255);
                }
                cam.SetPixels32(c);

                Directory.CreateDirectory(OutDir);
                string path = Path.GetFullPath(Path.Combine(OutDir, fileName.EndsWith(".png") ? fileName : fileName + ".png"));
                File.WriteAllBytes(path, cam.EncodeToPNG());
                return path;
            }
            finally
            {
                foreach (var s in saved)
                {
                    s.ps.targetTexture = s.rt;
                    s.ps.clearColor = s.clear;
                    s.ps.colorClearValue = s.clearValue;
                }
                RenderTexture.ReleaseTemporary(camRt);
                RenderTexture.ReleaseTemporary(uiRt);
                if (cam != null) UnityEngine.Object.DestroyImmediate(cam);
                if (ui != null) UnityEngine.Object.DestroyImmediate(ui);
                if (EditorApplication.isPlaying && EditorApplication.isPaused) EditorApplication.Step();
            }
        }

        /// <summary>
        /// Calls a method, public or not, on the first live component whose type is named <paramref name="type"/>
        /// (short name). How the captures open a sheet or press a button without a pointer.
        /// </summary>
        public static string Call(string type, string method, params object[] args)
        {
            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
            {
                if (mb == null || mb.GetType().Name != type) continue;
                MethodInfo m = mb.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null) return $"{type} has no {method}";
                object r = m.Invoke(m.IsStatic ? null : mb, args);
                return r?.ToString() ?? "ok";
            }
            return $"no live {type}";
        }

        /// <summary>Steps a paused play mode <paramref name="frames"/> frames. Keep batches small: the pipeline aborts main-thread work past 5 s.</summary>
        public static int Step(int frames)
        {
            if (!EditorApplication.isPlaying) return 0;
            EditorApplication.isPaused = true;
            for (int i = 0; i < frames; i++) EditorApplication.Step();
            // With the editor unfocused the Game view is not drawn, and a coroutine waiting on
            // WaitForEndOfFrame (the highlight clip's grabber) never resumes: no clip, no replay, no error.
            // A repaint request draws it once per call, which is enough end-of-frames to record a clip.
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            return Time.frameCount;
        }

        /// <summary>
        /// The game's state in one line, for <c>training/tools/flow_check.sh</c>: scene, event phase and attempt,
        /// whether a results card has reported itself, which screens are up, the time scale, RESTART's label,
        /// whether CHAOS is open, and on the menu the field size and the lit event.
        /// </summary>
        public static string State()
        {
            var s = new StringBuilder(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            var race = UnityEngine.Object.FindAnyObjectByType<Sim.RaceEvent>();
            if (race != null)
                s.Append($" | {race.GetType().Name} {race.Current} att={race.Attempt} shown={race.ResultsShown} n={race.Athletes.Count} t={race.RaceTime:F1}");
            s.Append($" | setup {Vis<UI.SetupView>()} hud {Vis<UI.HudView>()} bcast {Vis<UI.BroadcastView>()} results {Vis<UI.ResultsView>()} frame {Vis<UI.AppFrameView>()}");
            s.Append($" | ts={Time.timeScale}");
            var hud = UnityEngine.Object.FindAnyObjectByType<UI.HudView>();
            VisualElement hr = hud != null ? hud.GetComponent<UIDocument>().rootVisualElement : null;
            if (hr != null)
            {
                VisualElement pop = hr.Q("chaos-pop");
                s.Append($" | restart='{hr.Q<Label>("restart-label")?.text}' chaosOpen={pop != null && !pop.ClassListContains("hidden")}");
            }
            var setup = UnityEngine.Object.FindAnyObjectByType<UI.SetupView>();
            if (setup != null) s.Append($" | field={setup.FieldSize} event={setup.EventIndex}");
            var frame = UnityEngine.Object.FindAnyObjectByType<UI.AppFrameView>();
            VisualElement fr = frame != null ? frame.GetComponent<UIDocument>().rootVisualElement : null;
            s.Append($" | corner='{fr?.Q<Button>("menu")?.text}' title='{fr?.Q<Label>("title")?.text}'");
            s.Append($" | demo={Sim.DemoMode.Active} {Sim.DemoMode.Index + 1}/{Sim.DemoMode.Count} laps={Sim.SessionSettings.Laps} hurdles={Sim.SessionSettings.Hurdles} loading={UI.SceneLoader.Busy}");
            return s.ToString();
        }

        static string Vis<T>() where T : UI.UiRoot
        {
            T o = UnityEngine.Object.FindAnyObjectByType<T>();
            return o == null ? "-" : o.ScreenVisible ? "ON" : "off";
        }

        /// <summary>
        /// Pretends the screen has a notch <paramref name="top"/> and a home bar <paramref name="bottom"/> screen
        /// pixels deep (0, 0 puts the real safe area back). Every UiRoot re-pads on its next Update.
        /// </summary>
        public static string Notch(int top, int bottom)
        {
            UI.UiRoot.SafeAreaOverride = top <= 0 && bottom <= 0 ? (Rect?)null
                : new Rect(0f, bottom, Screen.width, Screen.height - top - bottom);
            return UI.UiRoot.SafeAreaOverride?.ToString() ?? "real safe area";
        }

        /// <summary>
        /// What would be hidden on a phone with a notch and a home bar, and what the frame's rows sit on top of:
        /// every visible button (and the results modal) checked against the frame rows and against the
        /// simulated safe band. Reference pixels, same as the layout check.
        /// </summary>
        public static string NotchCheck()
        {
            var sb = new StringBuilder();
            Rect screen = default, top = default, bottom = default;
            Rect safe = UI.UiRoot.SafeAreaOverride ?? Screen.safeArea;
            var docs = new List<UIDocument>();
            foreach (UIDocument d in UnityEngine.Object.FindObjectsByType<UIDocument>())
            {
                if (!d.isActiveAndEnabled || d.rootVisualElement?.panel == null || (d.panelSettings != null && d.panelSettings.targetTexture != null)) continue;
                docs.Add(d);
                screen = d.rootVisualElement.panel.visualTree.worldBound;
                if (d.rootVisualElement.Q("frame-top") is VisualElement ft && ft.resolvedStyle.display != DisplayStyle.None) top = ft.worldBound;
                if (d.rootVisualElement.Q("frame-bottom") is VisualElement fb && fb.resolvedStyle.display != DisplayStyle.None) bottom = fb.worldBound;
            }
            float k = Screen.height > 0 ? screen.height / Screen.height : 1f;
            float bandTop = (Screen.height - safe.yMax) * k, bandBottom = screen.height - safe.yMin * k;
            int hits = 0;
            sb.Append($"screen {screen.width:0}x{screen.height:0}, safe band y {bandTop:0}-{bandBottom:0}, frame rows 0-{top.yMax:0} / {bottom.yMin:0}-{screen.height:0}");
            foreach (UIDocument d in docs)
            {
                string doc = d.visualTreeAsset != null ? d.visualTreeAsset.name : d.name;
                if (d.rootVisualElement.Q("modal") is VisualElement modal && Shown(modal))
                {
                    Rect m = modal.worldBound;
                    sb.Append($"\n  {doc} modal y {m.yMin:0}-{m.yMax:0}");
                    if (m.yMin < bandTop || m.yMax > bandBottom) { hits++; sb.Append("  OUTSIDE SAFE BAND"); }
                    if (m.Overlaps(top) || m.Overlaps(bottom)) { hits++; sb.Append("  UNDER FRAME ROW"); }
                }
                if (doc == "AppFrame")
                {
                    // The frame's own five: never under their own rows, but they must clear the notch too.
                    foreach (string n in new[] { "title", "fps", "menu", "debug", "version" })
                    {
                        VisualElement e = d.rootVisualElement.Q(n);
                        if (e == null || !Shown(e)) continue;
                        Rect w = e.worldBound;
                        if (w.yMin < bandTop - 1f || w.yMax > bandBottom + 1f) { hits++; sb.Append($"\n  outside safe band: AppFrame/{n} y {w.yMin:0}-{w.yMax:0}"); }
                    }
                    continue;
                }
                d.rootVisualElement.Query<Button>().ForEach(b =>
                {
                    if (!Shown(b) || b.worldBound.height < 2f) return;
                    Rect w = b.worldBound;
                    string name = string.IsNullOrEmpty(b.name) ? b.text : b.name;
                    if (w.yMin < bandTop - 1f || w.yMax > bandBottom + 1f) { hits++; sb.Append($"\n  outside safe band: {doc}/{name} y {w.yMin:0}-{w.yMax:0}"); }
                    else if (w.Overlaps(top) || w.Overlaps(bottom)) { hits++; sb.Append($"\n  under frame row: {doc}/{name} y {w.yMin:0}-{w.yMax:0}"); }
                });
            }
            return $"notch-check {hits} problem(s): {sb}";
        }

        static bool Shown(VisualElement e)
        {
            for (VisualElement p = e; p != null; p = p.parent)
                if (p.resolvedStyle.display == DisplayStyle.None || p.ClassListContains("hidden") || p.resolvedStyle.opacity < 0.01f) return false;
            return true;
        }

        /// <summary>
        /// Render load over <paramref name="frames"/> stepped frames, from the profiler's own per-frame
        /// counters (the ones the diagnostics FRAME page shows): mean and peak triangles, draw calls and
        /// batches. <c>UnityStats</c> cannot be used here: in a stepped editor it reports whichever view drew
        /// last, which is as often the Scene view, or a capture, as the game.
        /// </summary>
        /// <remarks>
        /// Two calls with the stepping in between (<see cref="RenderLoadBegin"/>, Step, <see cref="RenderLoadEnd"/>):
        /// a stepped frame runs after the call that asked for it returns, so recorders started and read in
        /// one call see no frames at all.
        /// </remarks>
        public static string RenderLoadBegin(int frames)
        {
            RenderLoadEnd();
            _tris = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Triangles Count", frames);
            _draws = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Draw Calls Count", frames);
            _batches = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Batches Count", frames);
            return "recording";
        }

        public static string RenderLoadEnd()
        {
            if (!_tris.Valid) return "not recording";
            string s = $"tris mean {Mean(_tris):0} peak {Peak(_tris):0} | draws mean {Mean(_draws):0} peak {Peak(_draws):0} | batches mean {Mean(_batches):0} "
                     + $"({_tris.Count} frames, tier {Env.RenderTier.Current}, LOD ceiling {QualitySettings.maximumLODLevel})";
            _tris.Dispose(); _draws.Dispose(); _batches.Dispose();
            return s;
        }

        static Unity.Profiling.ProfilerRecorder _tris, _draws, _batches;

        /// <summary>
        /// Triangles the main camera would draw this instant if the LOD ceiling were each of 0, 1 and 2,
        /// counted from the scene rather than from the renderer: every enabled renderer whose bounds are in
        /// the frustum, with each LODGroup resolved the way Unity resolves it (relative screen height times
        /// the LOD bias, never finer than the ceiling). Camera pass only; shadow casters are not counted.
        ///
        /// The editor's render counters read zero in a stepped play mode and UnityStats reports whichever
        /// view drew last, so this is the number to compare one building against another. Athletes are
        /// counted apart, as their skins now and as the phone skins their definitions carry.
        /// </summary>
        public static string VisibleTriangles()
        {
            Camera cam = Camera.main;
            if (cam == null) return "no main camera";
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            var sb = new StringBuilder();
            for (int ceiling = 0; ceiling <= 2; ceiling++)
            {
                var skip = new HashSet<Renderer>();
                foreach (LODGroup g in UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Exclude))
                {
                    LOD[] lods = g.GetLODs();
                    int chosen = ActiveLod(g, lods, cam, ceiling);
                    for (int i = 0; i < lods.Length; i++)
                        if (i != chosen) foreach (Renderer r in lods[i].renderers) if (r != null) skip.Add(r);
                }
                long world = 0, bodies = 0;
                foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
                {
                    if (!r.enabled || skip.Contains(r) || !GeometryUtility.TestPlanesAABB(planes, r.bounds)) continue;
                    if ((cam.cullingMask & (1 << r.gameObject.layer)) == 0) continue;
                    Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                           : r.TryGetComponent(out MeshFilter mf) ? mf.sharedMesh : null;
                    if (m == null) continue;
                    long t = Tris(r, m);
                    if (r is SkinnedMeshRenderer) bodies += t; else world += t;
                }
                sb.Append($"ceiling {ceiling}: world {world / 1000}k + athletes {bodies / 1000}k = {(world + bodies) / 1000}k   ");
            }

            long pc = 0, phone = 0;
            foreach (Sim.AthleteDefinition d in Resources.FindObjectsOfTypeAll<Sim.AthleteDefinition>())
            {
                pc += SkinTris(d.skinOverride);
                phone += SkinTris(d.skinOverrideMobile != null ? d.skinOverrideMobile : d.skinOverride);
            }
            sb.Append($"| one of each athlete: full skins {pc / 1000}k, phone skins {phone / 1000}k");
            return sb.ToString();
        }

        /// <summary>The <paramref name="n"/> biggest visible renderers at a LOD ceiling, by triangles, with their hierarchy path.</summary>
        public static string TopRenderers(int ceiling, int n = 12)
        {
            Camera cam = Camera.main;
            if (cam == null) return "no main camera";
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            var skip = new HashSet<Renderer>();
            var inGroup = new HashSet<Renderer>();
            foreach (LODGroup g in UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Exclude))
            {
                LOD[] lods = g.GetLODs();
                int chosen = ActiveLod(g, lods, cam, ceiling);
                for (int i = 0; i < lods.Length; i++)
                    foreach (Renderer r in lods[i].renderers)
                    {
                        if (r == null) continue;
                        inGroup.Add(r);
                        if (i != chosen) skip.Add(r);
                    }
            }
            var list = new List<(long t, string path)>();
            foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
            {
                if (!r.enabled || skip.Contains(r) || !GeometryUtility.TestPlanesAABB(planes, r.bounds)) continue;
                Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.TryGetComponent(out MeshFilter mf) ? mf.sharedMesh : null;
                if (m == null) continue;
                long t = Tris(r, m);
                string p = r.name;
                for (Transform x = r.transform.parent; x != null; x = x.parent) p = x.name + "/" + p;
                list.Add((t, (inGroup.Contains(r) ? "" : "[no LOD] ") + p));
            }
            list.Sort((a, b) => b.t.CompareTo(a.t));
            var sb = new StringBuilder();
            for (int i = 0; i < Mathf.Min(n, list.Count); i++) sb.Append($"{list[i].t / 1000}k {list[i].path}\n");
            return sb.ToString();
        }

        /// <summary>
        /// A renderer's own triangles. Under static batching (play mode) every part of the building points
        /// at one combined mesh and draws only its own range of submeshes, from subMeshStartIndex, one per
        /// material; counting the whole mesh per part counted the building a hundred times over.
        /// </summary>
        static long Tris(Renderer r, Mesh m)
        {
            int first = 0, count = m.subMeshCount;
            if (r.isPartOfStaticBatch)
            {
                // The range is not public API; the serialised batch info carries it.
                var so = new SerializedObject(r);
                first = so.FindProperty("m_StaticBatchInfo.firstSubMesh")?.intValue ?? 0;
                count = so.FindProperty("m_StaticBatchInfo.subMeshCount")?.intValue ?? r.sharedMaterials.Length;
            }
            long t = 0;
            for (int s = first; s < Mathf.Min(m.subMeshCount, first + count); s++)
                if (m.GetTopology(s) == MeshTopology.Triangles) t += (long)m.GetIndexCount(s) / 3;
            return t;
        }

        static int ActiveLod(LODGroup g, LOD[] lods, Camera cam, int ceiling)
        {
            Transform t = g.transform;
            Vector3 centre = t.TransformPoint(g.localReferencePoint);
            Vector3 s = t.lossyScale;
            float size = g.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            float distance = Vector3.Distance(cam.transform.position, centre);
            float height = size / Mathf.Max(0.001f, 2f * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) * QualitySettings.lodBias;
            for (int i = 0; i < lods.Length; i++)
                if (height >= lods[i].screenRelativeTransitionHeight) return Mathf.Min(Mathf.Max(i, ceiling), lods.Length - 1);
            return -1;   // culled
        }

        static long SkinTris(GameObject skin)
        {
            if (skin == null) return 0;
            long t = 0;
            foreach (SkinnedMeshRenderer r in skin.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r.sharedMesh != null) t += r.sharedMesh.triangles.Length / 3;
            return t;
        }

        static double Mean(Unity.Profiling.ProfilerRecorder r)
        {
            if (r.Count == 0) return 0;
            double s = 0;
            for (int i = 0; i < r.Count; i++) s += r.GetSample(i).Value;
            return s / r.Count;
        }

        static long Peak(Unity.Profiling.ProfilerRecorder r)
        {
            long m = 0;
            for (int i = 0; i < r.Count; i++) m = Math.Max(m, r.GetSample(i).Value);
            return m;
        }

        /// <summary>
        /// Mean colour of the lawn, seen from above at 45 degrees by a temporary camera, with the scene's
        /// lightmaps on and then with them taken away (restored afterwards). The difference is what the bake
        /// does to the grass; both pictures are written beside the other captures.
        /// </summary>
        public static string LawnProbe(string label)
        {
            Renderer lawn = null;
            foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
                foreach (Material m in r.sharedMaterials)
                    if (m != null && m.name.Contains("Lawn") && (lawn == null || r.bounds.size.sqrMagnitude > lawn.bounds.size.sqrMagnitude)) lawn = r;
            if (lawn == null) return "no lawn renderer";

            var go = new GameObject("LawnProbeCam");
            var cam = go.AddComponent<Camera>();
            Vector3 c = lawn.bounds.center + new Vector3(lawn.bounds.extents.x * 0.35f, 0f, -lawn.bounds.extents.z * 0.35f);
            go.transform.position = c + new Vector3(0f, 18f, -18f);
            go.transform.LookAt(c);
            cam.fieldOfView = 30f;
            var rt = RenderTexture.GetTemporary(512, 512, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            LightmapData[] maps = LightmapSettings.lightmaps;
            try
            {
                Color withMaps = Shoot(cam, rt, $"lawn_{label}_baked");
                // As it was before the bake existed: the lawn off the lightmap, lit by the ambient probe.
                int index = lawn.lightmapIndex;
                lawn.lightmapIndex = -1;
                Color ambient = Shoot(cam, rt, $"lawn_{label}_ambient");
                lawn.lightmapIndex = index;
                LightmapSettings.lightmaps = new LightmapData[0];
                Color without = Shoot(cam, rt, $"lawn_{label}_nomaps");
                return $"lawn '{lawn.name}' ({maps.Length} lightmaps): baked {Hex(withMaps)}, ambient-lit (pre-bake) {Hex(ambient)}, no indirect {Hex(without)}";
            }
            finally
            {
                LightmapSettings.lightmaps = maps;
                cam.targetTexture = null;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        static Color Shoot(Camera cam, RenderTexture rt, string name)
        {
            cam.Render();
            Texture2D t = Read(rt);
            Color32[] px = t.GetPixels32();
            double r = 0, g = 0, b = 0;
            int n = 0;
            for (int y = 128; y < 384; y++)
                for (int x = 128; x < 384; x++) { Color32 p = px[y * 512 + x]; r += p.r; g += p.g; b += p.b; n++; }
            Directory.CreateDirectory(OutDir);
            File.WriteAllBytes(Path.Combine(OutDir, name + ".png"), t.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(t);
            return new Color((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255));
        }

        static string Hex(Color c) => $"rgb({c.r * 255:0},{c.g * 255:0},{c.b * 255:0})";

        static Texture2D Read(RenderTexture rt)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            return t;
        }

        // ---------------------------------------------------------------- game view size

        /// <summary>
        /// Selects (adding it first if need be) a fixed-resolution size in the Game view. GameViewSizes is
        /// internal, so this is reflection; it fails with a warning rather than an exception if the editor
        /// has moved things around.
        /// </summary>
        public static bool SetGameViewSize(int width, int height, string label)
        {
            try
            {
                Assembly asm = typeof(UnityEditor.Editor).Assembly;
                Type sizesType = asm.GetType("UnityEditor.GameViewSizes");
                Type singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
                object sizes = singleton.GetProperty("instance").GetValue(null);
                object groupType = sizesType.GetProperty("currentGroupType").GetValue(sizes);
                object group = sizesType.GetMethod("GetGroup").Invoke(sizes, new[] { groupType });
                Type groupT = group.GetType();

                int total = (int)groupT.GetMethod("GetTotalCount").Invoke(group, null);
                int index = -1;
                for (int i = 0; i < total; i++)
                {
                    object s = groupT.GetMethod("GetGameViewSize").Invoke(group, new object[] { i });
                    Type st = s.GetType();
                    if ((int)st.GetProperty("width").GetValue(s) == width &&
                        (int)st.GetProperty("height").GetValue(s) == height &&
                        st.GetProperty("sizeType").GetValue(s).ToString() == "FixedResolution")
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    Type sizeT = asm.GetType("UnityEditor.GameViewSize");
                    Type kindT = asm.GetType("UnityEditor.GameViewSizeType");
                    object kind = Enum.Parse(kindT, "FixedResolution");
                    object size = Activator.CreateInstance(sizeT, kind, width, height, label);
                    groupT.GetMethod("AddCustomSize").Invoke(group, new[] { size });
                    index = (int)groupT.GetMethod("GetTotalCount").Invoke(group, null) - 1;
                }

                Type gvT = asm.GetType("UnityEditor.GameView");
                EditorWindow gv = EditorWindow.GetWindow(gvT);
                MethodInfo select = gvT.GetMethod("SizeSelectionCallback",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (select != null) select.Invoke(gv, new object[] { index, null });
                else gvT.GetProperty("selectedSizeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.SetValue(gv, index);
                gv.Repaint();
                Debug.Log($"[UiShots] Game view set to {width}x{height} (size index {index}).");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UiShots] could not set the Game view size: {e.GetBaseException().Message}");
                return false;
            }
        }

        // ---------------------------------------------------------------- layout check

        public class Finding
        {
            public string kind, document, element, detail;
            public override string ToString() => $"{kind,-9} {document}/{element}: {detail}";
        }

        public class Report
        {
            public string scene;
            public int width, height, elementsChecked;
            public readonly List<Finding> findings = new List<Finding>();
            public readonly Dictionary<string, string> anchors = new Dictionary<string, string>();

            public int Count(string kind) { int n = 0; foreach (var f in findings) if (f.kind == kind) n++; return n; }

            public string Summary()
            {
                var sb = new StringBuilder();
                sb.Append($"[UiShots] {scene} at {width}x{height}: {elementsChecked} elements, ")
                  .Append($"offscreen {Count("offscreen")}, scroll {Count("scroll")}, tiny-text {Count("tiny-text")}, ")
                  .Append($"clipped {Count("clipped")}, anchor {Count("anchor")}");
                foreach (var f in findings) sb.Append("\n  ").Append(f);
                return sb.ToString();
            }
        }

        /// <summary>Walks every visible element of every live panel. Play mode or edit mode, whatever is on screen.</summary>
        public static Report CheckLayout()
        {
            var r = new Report
            {
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                width = Screen.width,
                height = Screen.height,
            };

            foreach (UIDocument doc in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude))
            {
                if (!doc.isActiveAndEnabled || (doc.panelSettings != null && doc.panelSettings.targetTexture != null) || doc.rootVisualElement == null || doc.rootVisualElement.panel == null) continue;
                VisualElement top = doc.rootVisualElement.panel.visualTree;
                Rect screen = top.worldBound;
                string docName = doc.visualTreeAsset != null ? doc.visualTreeAsset.name : doc.name;
                Walk(doc.rootVisualElement, screen, docName, r);
                if (docName == "AppFrame") CheckAnchors(doc.rootVisualElement, screen, r);
            }

            try
            {
                Directory.CreateDirectory(OutDir);
                string path = Path.Combine(OutDir, $"layout_{r.scene}_{r.width}x{r.height}.json");
                File.WriteAllText(path, ToJson(r));
            }
            catch (Exception e) { Debug.LogWarning($"[UiShots] could not write the report: {e.Message}"); }
            return r;
        }

        static void Walk(VisualElement e, Rect screen, string doc, Report r)
        {
            IResolvedStyle s = e.resolvedStyle;
            if (s.display == DisplayStyle.None || s.visibility == Visibility.Hidden || s.opacity <= 0.01f) return;
            if (e.ClassListContains("hidden")) return;
            r.elementsChecked++;

            Rect b = e.worldBound;
            bool sized = b.width > 1f && b.height > 1f && !float.IsNaN(b.width);
            string id = Describe(e);

            if (sized && (e is Button || (e is TextElement te0 && !string.IsNullOrEmpty(te0.text))))
            {
                float over = Mathf.Max(screen.xMin - b.xMin, b.xMax - screen.xMax, screen.yMin - b.yMin, b.yMax - screen.yMax);
                if (over > 2f) r.findings.Add(new Finding { kind = "offscreen", document = doc, element = id, detail = $"{over:0} px past the edge" });
            }

            if (e is ScrollView sv)
            {
                float content = sv.contentContainer.layout.height, view = sv.contentViewport.layout.height;
                if (content > view + 2f)
                    r.findings.Add(new Finding { kind = "scroll", document = doc, element = id, detail = $"content {content:0} px in a {view:0} px viewport" });
            }

            if (e is TextElement te && !string.IsNullOrEmpty(te.text) && sized)
            {
                // Reference pixels: the panel scales to 1080 wide, so resolved font size is already in them.
                if (s.fontSize < MinFontPx - 0.5f)
                    r.findings.Add(new Finding { kind = "tiny-text", document = doc, element = id, detail = $"{s.fontSize:0} px < {MinFontPx:0} (\"{Trim(te.text)}\")" });

                if (s.whiteSpace == WhiteSpace.NoWrap)
                {
                    Vector2 want = te.MeasureTextSize(te.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);
                    float have = e.contentRect.width;
                    if (want.x > have + 3f)
                        r.findings.Add(new Finding { kind = "clipped", document = doc, element = id, detail = $"text wants {want.x:0} px, has {have:0} (\"{Trim(te.text)}\")" });
                }
            }

            for (int i = 0; i < e.hierarchy.childCount; i++) Walk(e.hierarchy[i], screen, doc, r);
        }

        /// <summary>The five frame anchors, each by the centre of its box against the screen's thirds and its top/bottom tenth.</summary>
        static void CheckAnchors(VisualElement root, Rect screen, Report r)
        {
            (string name, int col, bool top)[] want =
            {
                ("title", 0, true), ("fps", 1, true), ("menu", 2, true), ("debug", 0, false), ("version", 2, false),
            };
            foreach (var w in want)
            {
                VisualElement e = root.Q(w.name);
                if (e == null || e.resolvedStyle.display == DisplayStyle.None)
                {
                    r.anchors[w.name] = "missing";
                    r.findings.Add(new Finding { kind = "anchor", document = "AppFrame", element = w.name, detail = "not on screen" });
                    continue;
                }
                Rect b = e.worldBound;
                int col = Mathf.Clamp((int)((b.center.x - screen.xMin) / (screen.width / 3f)), 0, 2);
                bool top = b.center.y < screen.yMin + screen.height * 0.12f;
                bool bottom = b.center.y > screen.yMax - screen.height * 0.12f;
                bool ok = col == w.col && (w.top ? top : bottom);
                r.anchors[w.name] = $"{(ok ? "ok" : "WRONG")} at ({b.center.x:0},{b.center.y:0})";
                if (!ok) r.findings.Add(new Finding { kind = "anchor", document = "AppFrame", element = w.name, detail = $"centre ({b.center.x:0},{b.center.y:0}) is not {(w.top ? "top" : "bottom")} {(w.col == 0 ? "left" : w.col == 1 ? "centre" : "right")}" });
            }
        }

        static string Describe(VisualElement e)
        {
            if (!string.IsNullOrEmpty(e.name)) return e.name;
            string cls = null;
            foreach (string c in e.GetClasses()) { cls = c; break; }
            string parent = e.parent != null && !string.IsNullOrEmpty(e.parent.name) ? e.parent.name + ">" : "";
            return parent + (cls != null ? "." + cls : e.GetType().Name);
        }

        static string Trim(string t) => t.Length > 24 ? t.Substring(0, 24) + "..." : t.Replace("\n", " ");

        static string ToJson(Report r)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"scene\": \"{Esc(r.scene)}\",\n  \"width\": {r.width},\n  \"height\": {r.height},\n");
            sb.Append($"  \"elements_checked\": {r.elementsChecked},\n");
            sb.Append($"  \"counts\": {{\"offscreen\": {r.Count("offscreen")}, \"scroll\": {r.Count("scroll")}, \"tiny_text\": {r.Count("tiny-text")}, \"clipped\": {r.Count("clipped")}, \"anchor\": {r.Count("anchor")}}},\n");
            sb.Append("  \"anchors\": {");
            bool first = true;
            foreach (var kv in r.anchors) { sb.Append(first ? "" : ", ").Append($"\"{kv.Key}\": \"{Esc(kv.Value)}\""); first = false; }
            sb.Append("},\n  \"findings\": [\n");
            for (int i = 0; i < r.findings.Count; i++)
            {
                Finding f = r.findings[i];
                sb.Append($"    {{\"kind\": \"{f.kind}\", \"document\": \"{Esc(f.document)}\", \"element\": \"{Esc(f.element)}\", \"detail\": \"{Esc(f.detail)}\"}}");
                sb.Append(i < r.findings.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
