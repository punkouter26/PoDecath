using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;
using UnityEngine.UIElements;
using Unity.Cinemachine;
using Unity.Mathematics;
using PoDecath.Cam;
using PoDecath.Fx;
using PoDecath.Sim;
using PoDecath.UI;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// The show layer added on 2026-09-29, built into the race scenes by the same builders as everything
    /// else: the viewer's chaos buttons, the three moving shots (drone and trackside rail on splines, the
    /// head cam), the season keeper and the highlight clip, and the assets they need — three effect
    /// materials made from Kenney's CC0 particle sprites, and the scoring table.
    ///
    /// The splines are computed from the track rather than hand-placed, because the track itself is solved
    /// from the roof; but they are ordinary <see cref="SplineContainer"/>s in the scene, so either one can be
    /// opened in the Scene view and its knots dragged, and the camera follows the edited shape.
    /// </summary>
    public static class ShowBuilder
    {
        const string KenneyDir = "Assets/Textures/Kenney";
        const string ScoringPath = "Assets/Resources/ScoringTable.asset";

        // ---------------------------------------------------------------- assets

        [MenuItem("PoDecath/Bake Kenney Effects", priority = 10)]
        public static void BakeMenu()
        {
            Bake();
            EnsureScoringTable();
            AssetDatabase.SaveAssets();
            Debug.Log("[PoDecath] Kenney effects baked: Fx_Gust, Fx_Shove, Fx_Slick. Scoring table at " + ScoringPath);
        }

        public struct Materials { public Material gust, shove, slick; }

        /// <summary>The three chaos materials, rebuilt from the Kenney sprites every call (cheap and deterministic).</summary>
        public static Materials Bake()
        {
            Texture2D trace = Sprite("trace_01");
            Texture2D star = Sprite("star_06");
            Texture2D blob = Sprite("smoke_04");
            return new Materials
            {
                gust = VfxBakery.Particle("Fx_Gust", trace, additive: false, softParticles: false),
                shove = VfxBakery.Particle("Fx_Shove", star, additive: true, softParticles: false),
                slick = Slick(blob),
            };
        }

        /// <summary>Kenney's sprites as particle textures: clamped, alpha from the file, no mips on a 512 px sprite.</summary>
        static Texture2D Sprite(string name)
        {
            string path = $"{KenneyDir}/{name}.png";
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                bool dirty = ti.wrapMode != TextureWrapMode.Clamp || !ti.alphaIsTransparency || ti.maxTextureSize != 256;
                if (dirty)
                {
                    ti.wrapMode = TextureWrapMode.Clamp;
                    ti.alphaIsTransparency = true;
                    ti.alphaSource = TextureImporterAlphaSource.FromInput;
                    ti.maxTextureSize = 256;   // a particle is a few dozen pixels across on a phone
                    ti.SaveAndReimport();
                }
            }
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) Debug.LogWarning($"[PoDecath] {path} missing; the chaos effect that uses it will be plain.");
            return tex;
        }

        /// <summary>
        /// The wet patch: URP Lit, transparent, nearly black and nearly mirror-smooth, so what it shows is
        /// the sky reflected off water rather than a colour painted on the deck. The Kenney smoke sprite is
        /// its alpha, which gives it a ragged puddle edge instead of a rectangle.
        /// </summary>
        static Material Slick(Texture2D blob)
        {
            const string path = "Assets/Materials/Fx_Slick.mat";
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                if (shader == null) return null;
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            if (shader != null && m.shader != shader) m.shader = shader;
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Smoothness", 0.96f);
            m.SetFloat("_Metallic", 0f);
            m.SetColor("_BaseColor", new Color(0.05f, 0.07f, 0.1f, 0.72f));
            if (blob != null) m.SetTexture("_BaseMap", blob);
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 40;
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>The scoring table in Resources, created with the defaults if it is not there. Never overwritten.</summary>
        public static ScoringTable EnsureScoringTable()
        {
            var table = AssetDatabase.LoadAssetAtPath<ScoringTable>(ScoringPath);
            if (table != null) return table;
            PolicyLibraryTools.EnsureFolder("Assets/Resources");
            table = ScriptableObject.CreateInstance<ScoringTable>();
            table.entries = ScoringTable.Defaults();
            AssetDatabase.CreateAsset(table, ScoringPath);
            return table;
        }

        // ---------------------------------------------------------------- cameras

        /// <summary>
        /// The drone, the trackside rail and the head cam, added to a gallery that already has its ten fixed
        /// jobs. <paramref name="shotCam"/> is the gallery's own camera factory, so the new shots get the same
        /// portrait lens, composer and impulse listener as the rest.
        /// </summary>
        public static void AddMovingShots(BroadcastDirector dir, TrackPath path, System.Func<string, float, CinemachineCamera> shotCam)
        {
            if (dir == null || path == null) return;
            float edge = path.deckWidth * 0.5f;

            // Rail: just outside the barrier at shoulder height, leading the subject by a couple of metres so
            // the runner is framed coming towards the lens, not going away from it.
            SplineContainer rail = Ring("Spline Rail", path, edge + 2.4f, 2.8f, 32);
            dir.trackRailCam = SplineShot(shotCam("CM Shot TrackRail", 50f), rail, lead: 3f, damping: 0.6f);

            // Drone: high and wide, well outside the roof, a long way ahead. It is the shot that shows where
            // the race is, so it looks down on the whole deck rather than at one runner.
            SplineContainer drone = Ring("Spline Drone", path, edge + 16f, 13f, 24);
            dir.droneCam = SplineShot(shotCam("CM Shot Drone", 62f), drone, lead: 12f, damping: 1.4f);

            // Head: placed every frame by the director just in front of the featured athlete's face. Wide,
            // and a near plane far enough out to clip the runner's own head away: at 0.03 m the first test
            // filmed the inside of the skin.
            CinemachineCamera head = shotCam("CM Shot HeadCam", 84f);
            head.Lens.NearClipPlane = 0.2f;
            var comp = head.GetComponent<CinemachineRotationComposer>();
            if (comp != null) comp.Damping = new Vector2(0.12f, 0.12f);
            dir.headCam = head;
        }

        /// <summary>A closed loop following the track at a fixed offset outside it and height above the deck.</summary>
        static SplineContainer Ring(string name, TrackPath path, float lateral, float height, int knots)
        {
            var go = new GameObject(name);
            var container = go.AddComponent<SplineContainer>();
            Spline spline = container.Spline;
            spline.Clear();
            for (int i = 0; i < knots; i++)
            {
                float s = path.LapLength * i / knots;
                Vector3 p = path.Position(s, lateral) + Vector3.up * height;
                spline.Add(new BezierKnot(new float3(p.x, p.y, p.z)), TangentMode.AutoSmooth);
            }
            spline.Closed = true;
            return container;
        }

        static CinemachineCamera SplineShot(CinemachineCamera cam, SplineContainer spline, float lead, float damping)
        {
            var dolly = cam.gameObject.AddComponent<CinemachineSplineDolly>();
            dolly.Spline = spline;
            dolly.PositionUnits = PathIndexUnit.Distance;
            dolly.CameraRotation = CinemachineSplineDolly.RotationMode.Default;   // the composer aims it
            dolly.AutomaticDolly.Enabled = true;
            dolly.AutomaticDolly.Method = new SplineAutoDolly.NearestPointToTarget { PositionOffset = lead };
            dolly.Damping.Enabled = true;
            dolly.Damping.Position = new Vector3(damping, damping, damping);
            return cam;
        }

        // ---------------------------------------------------------------- chaos, season, clip

        /// <summary>
        /// The viewer's buttons and what they push on. <paramref name="loop"/> is null for the long jump, so the
        /// wet patch goes on the runway instead of the loop.
        /// </summary>
        public static ViewerChaos AddViewerChaos(RaceEvent race, TrackPath loop, LongJumpPit pit, HudView hud, BroadcastDirector dir)
        {
            Materials mats = Bake();
            var chaos = new GameObject("ViewerChaos").AddComponent<ViewerChaos>();
            chaos.race = race;
            chaos.path = loop;
            chaos.pit = pit;
            chaos.gustMaterial = mats.gust;
            chaos.shoveMaterial = mats.shove;
            chaos.slickMaterial = mats.slick;
            if (hud != null)
            {
                hud.chaos = chaos;
                hud.director = dir;
            }
            return chaos;
        }

        const float GantryPastLine = 1.2f;   // metres beyond the finish line, in the running direction
        const string GantryModelPath = "Assets/Models/FinishGantry.glb";
        const string GantryPrefabPath = "Assets/Prefabs/FinishGantry.prefab";
        const string ThemePath = "Assets/UI/PoDecath.tss";

        /// <summary>
        /// The finish-line gantry (<c>training/tools/finish_gantry.py</c>), as a prefab instance standing on
        /// the line with its legs on the two barriers: the live screen, the timing clock. The prefab is made
        /// once from the model with the screen and clock renderers wired; each scene's instance is given its
        /// race and director. Nothing happens without the model, so a checkout that has not run the Blender
        /// script builds as before.
        /// </summary>
        public static FinishGantry AddFinishGantry(RaceEvent race, TrackPath loop, BroadcastDirector dir)
        {
            GameObject prefab = EnsureGantryPrefab();
            if (prefab == null || race == null) return null;
            if (!FinishLine(race, loop, out Vector3 at, out Vector3 along)) return null;

            // Just past the line, not on it: the photo-finish camera looks along the line from the side, and a
            // leg standing on the line fills its whole slit (which is where real timing gantries stand too).
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.transform.SetPositionAndRotation(at + along * GantryPastLine, Quaternion.LookRotation(along, Vector3.up));
            var gantry = go.GetComponent<FinishGantry>();
            gantry.race = race;
            gantry.director = dir;
            return gantry;
        }

        static GameObject EnsureGantryPrefab()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(GantryModelPath);
            if (model == null)
            {
                Debug.Log($"[PoDecath] No {GantryModelPath}; run training/tools/finish_gantry.py in Blender for the finish gantry.");
                return null;
            }
            PolicyLibraryTools.EnsureFolder("Assets/Prefabs");
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(GantryPrefabPath);
            if (existing != null && existing.GetComponent<FinishGantry>() != null) return existing;

            var root = new GameObject("FinishGantry");
            var body = (GameObject)PrefabUtility.InstantiatePrefab(model);
            body.transform.SetParent(root.transform, false);
            var gantry = root.AddComponent<FinishGantry>();
            gantry.theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            foreach (MeshRenderer r in body.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.gameObject.name.StartsWith("Gantry_Screen")) gantry.screen = r;
                else if (r.gameObject.name.StartsWith("Gantry_Clock")) gantry.clockFace = r;
                // Steel and housing are lit by the probes like the athletes, not baked: the gantry is
                // rebuilt with the scene, and a prop that needs a rebake after every nudge would not be nudged.
                r.receiveGI = ReceiveGI.LightProbes;
            }
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, GantryPrefabPath);
            Object.DestroyImmediate(root);
            return saved;
        }

        /// <summary>The photo-finish camera on the line. Not for the long jump, which has no line.</summary>
        public static PhotoFinish AddPhotoFinish(RaceEvent race, TrackPath loop, ResultsView results)
        {
            var photo = new GameObject("PhotoFinish").AddComponent<PhotoFinish>();
            photo.race = race;
            photo.path = loop;
            if (results != null) results.photoFinish = photo;
            return photo;
        }

        /// <summary>The finish line's centre on the deck and the running direction through it, as FinishTape finds it.</summary>
        static bool FinishLine(RaceEvent race, TrackPath loop, out Vector3 at, out Vector3 along)
        {
            at = along = Vector3.zero;
            if (race is LapEvent lap && loop != null)
            {
                at = loop.Position(lap.startS, 0f);
                along = loop.Tangent(lap.startS);
                return true;
            }
            if (race.direction.sqrMagnitude < 1e-4f) return false;
            along = race.direction.normalized;
            at = race.startLine + along * race.raceDistance;
            return true;
        }

        /// <summary>Points, cards and the season on the results card, and the highlight clip under it.</summary>
        public static void AddSeasonAndClip(RaceEvent race, ResultsView results)
        {
            EnsureScoringTable();
            var go = new GameObject("Season");
            var keeper = go.AddComponent<SeasonKeeper>();
            keeper.race = race;
            var clip = go.AddComponent<HighlightClip>();
            clip.race = race;
            if (results != null)
            {
                results.season = keeper;
                results.clip = clip;
            }
        }
    }
}
