using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.ProBuilder;
using UnityEditor.ProBuilder;
using PoDecath.Sim;
using Debug = UnityEngine.Debug;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Baked light for the rooftop: lightmaps on the building and the track, a ring of light probes over
    /// the deck for everything that moves, and one settings asset so every scene bakes the same way.
    ///
    /// Nothing was baked before. The sun was real-time, the ambient came from the sky probe, and every
    /// shadow the building cast on itself was a cascade lookup at runtime, on a phone, over 833k triangles.
    /// Mixed lighting with a shadowmask keeps the sun's direct light live (so the time-of-day presets still
    /// swing it) and bakes the bounce, the ambient occlusion and the distant shadows into textures, which
    /// is where the white stone gets its depth and where a phone gets its shadow budget back.
    ///
    /// <see cref="Prepare"/> is cheap and runs in every scene build: flags, UV2 on the track, the probes,
    /// the settings. <see cref="BakeAll"/> is the bake itself, minutes of GPU time, run on demand from
    /// the menu; a scene that has never been baked simply has no lightmaps and looks as it did.
    /// </summary>
    public static class LightingBakery
    {
        const string SettingsPath = "Assets/Settings/Rooftop_Lighting.lighting";
        const string WhiteHousePath = "Assets/Models/WhiteHouse.glb";
        static readonly string[] Scenes =
        {
            "Assets/Scenes/RooftopRace.unity",
            "Assets/Scenes/RooftopLongJump.unity",
            "Assets/Scenes/RooftopLap.unity",
            "Assets/Scenes/Rooftop.unity",
        };

        /// <summary>
        /// Everything a scene needs to be bakeable, done at build time. Static flags on the building (the
        /// LOD copies and the trees are lit by probes, not lightmapped), lightmap UVs on the ProBuilder
        /// track, a probe ring round the loop, and the shared settings asset assigned to the scene.
        /// </summary>
        public static void Prepare(GameObject building, Light sun, TrackPath path, Transform trackRoot)
        {
            if (sun != null) sun.lightmapBakeType = LightmapBakeType.Mixed;
            LightingSettings settings = EnsureSettings();
            Lightmapping.lightingSettings = settings;
            if (building != null) Flag(building);
            // ProBuilder rebuilds its Unity mesh from its own arrays whenever a scene opens, so a UV2 written
            // onto the MeshFilter's mesh is gone by bake time (the bake then warns of 208 meshes without
            // one and unwraps nothing). Optimize(true) writes the lightmap UVs into ProBuilder's own
            // arrays, where they survive. Every ProBuilder mesh in the scene: the track, the infield, the
            // runway and the pit.
            int unwrapped = UnwrapProBuilder();
            if (unwrapped > 0) Debug.Log($"[PoDecath] Lightmap UVs generated for {unwrapped} ProBuilder mesh(es).");
            if (trackRoot != null)
                foreach (MeshFilter mf in trackRoot.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.GetComponent<ProBuilderMesh>() == null) EnsureUv2(mf.sharedMesh);
            if (path != null) BuildProbes(path);
        }

        /// <summary>Lightmap UVs onto every ProBuilder mesh in the open scene. Returns how many were unwrapped.</summary>
        static int UnwrapProBuilder()
        {
            int n = 0;
            foreach (ProBuilderMesh pb in UnityEngine.Object.FindObjectsByType<ProBuilderMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pb == null) continue;   // Optimize returns quietly on an empty mesh
                pb.Optimize(true);
                n++;
            }
            return n;
        }

        static void Flag(GameObject building)
        {
            foreach (MeshRenderer r in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                bool lod = UnderLod(r.transform, building.transform);
                string n = r.gameObject.name;
                bool tree = n.StartsWith("Tree");
                bool shell = n == "Wings" || n == "Residence" || n.Contains("Portico") || n.Contains("Ground");
                bool lightmapped = !lod && !tree;
                var flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic
                          | StaticEditorFlags.OccluderStatic | StaticEditorFlags.ReflectionProbeStatic;
                if (lightmapped) flags |= StaticEditorFlags.ContributeGI;
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, flags);
                r.receiveGI = lightmapped ? ReceiveGI.Lightmaps : ReceiveGI.LightProbes;
                // The shells get the full texel density; the window segments, cars and props are small
                // and many, and at full density they would take most of the atlas for none of the picture.
                r.scaleInLightmap = lightmapped ? (shell ? 1f : 0.35f) : 0f;
            }
        }

        static bool UnderLod(Transform t, Transform root)
        {
            for (Transform p = t; p != null && p != root; p = p.parent)
                if (p.name == "LOD1" || p.name == "LOD2") return true;
            return false;
        }

        static LightingSettings EnsureSettings()
        {
            PolicyLibraryTools.EnsureFolder("Assets/Settings");
            var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(SettingsPath);
            if (ls == null)
            {
                ls = new LightingSettings { name = "Rooftop_Lighting" };
                AssetDatabase.CreateAsset(ls, SettingsPath);
            }
            ls.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
            ls.mixedBakeMode = MixedLightingMode.Shadowmask;
            ls.realtimeGI = false;
            ls.bakedGI = true;
            // 2.5 texels per metre on a 150 m building is a few 1024 maps, not a few 4096 ones. The stone
            // is white and the bounce is soft; there is no detail at a higher density to capture.
            ls.lightmapResolution = 2.5f;
            ls.lightmapMaxSize = 1024;
            ls.lightmapPadding = 2;
            ls.directionalityMode = LightmapsMode.NonDirectional;
            ls.ao = true;
            ls.aoMaxDistance = 1.6f;
            ls.aoExponentDirect = 0f;
            ls.aoExponentIndirect = 1f;
            ls.directSampleCount = 16;
            ls.indirectSampleCount = 128;
            ls.environmentSampleCount = 128;
            ls.maxBounces = 2;
            ls.lightmapCompression = LightmapCompression.NormalQuality;
            ls.filteringMode = LightingSettings.FilterMode.Auto;
            ls.prioritizeView = false;
            UnityEditor.EditorUtility.SetDirty(ls);   // qualified: ProBuilder has an EditorUtility of its own
            return ls;
        }

        /// <summary>
        /// ProBuilder meshes have no lightmap UVs unless asked. Generated in place on the scene mesh; the
        /// deck is rebuilt by the builder anyway, so there is nothing to keep in step.
        /// </summary>
        static void EnsureUv2(Mesh m)
        {
            if (m == null) return;
            Vector2[] uv2 = m.uv2;
            if (uv2 != null && uv2.Length == m.vertexCount) return;
            try { Unwrapping.GenerateSecondaryUVSet(m); }
            catch (Exception e) { Debug.LogWarning($"[PoDecath] lightmap UVs not generated for {m.name}: {e.Message}"); }
        }

        /// <summary>
        /// Probes over the deck, in three layers, and a coarser ring outside the rail for the stands and
        /// the cameras. Athletes, the crowd and every LOD copy of the building read their ambient from
        /// these instead of from one sky probe for the whole roof.
        /// </summary>
        static void BuildProbes(TrackPath path)
        {
            GameObject existing = GameObject.Find("LightProbes");
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
            var go = new GameObject("LightProbes");
            var group = go.AddComponent<LightProbeGroup>();
            var pts = new List<Vector3>();
            float lap = path.LapLength;
            float half = path.deckWidth * 0.5f;
            float[] lateral = { -half - 1.5f, -half + 0.4f, 0f, half - 0.4f, half + 1.5f };
            float[] heights = { 0.3f, 1.4f, 3.2f };
            for (float s = 0f; s < lap; s += 2.5f)
                foreach (float l in lateral)
                    foreach (float h in heights)
                        pts.Add(path.Position(s, l) + Vector3.up * h);
            float[] far = { half + 5f, half + 10f };
            float[] farH = { -3f, 1f, 6f };
            for (float s = 0f; s < lap; s += 6f)
                foreach (float l in far)
                    foreach (float h in farH)
                        pts.Add(path.Position(s, l) + Vector3.up * h);
            group.probePositions = pts.ToArray();   // the group sits at the origin, so local is world
        }

        // ---------------------------------------------------------------- the bake

        [MenuItem("PoDecath/Bake Lighting (lightmaps + probes)", priority = 11)]
        public static void BakeAll()
        {
            var log = new StringBuilder();
            var total = Stopwatch.StartNew();
            EnsureBuildingLightmapUVs(log);
            string wasOpen = SceneManager.GetActiveScene().path;

            foreach (string scenePath in Scenes)
            {
                if (!File.Exists(scenePath)) continue;
                var sw = Stopwatch.StartNew();
                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                Lightmapping.lightingSettings = EnsureSettings();
                // ProBuilder keeps no lightmap UVs of its own: HasArrays(Lightmap) reads the Unity mesh, and
                // opening a scene rebuilds that mesh with uv2 set to null. So the unwrap has to happen here,
                // after the open and before the bake, and the scene is saved afterwards with the uv2 the
                // lightmaps were baked against. Done at build time too (Prepare), which is what the
                // player build sees if nobody bakes.
                int unwrapped = UnwrapProBuilder();
                if (unwrapped > 0) Debug.Log($"[PoDecath] {Path.GetFileNameWithoutExtension(scenePath)}: lightmap UVs on {unwrapped} ProBuilder mesh(es).");
                Lightmapping.Clear();
                bool ok = Lightmapping.Bake();
                EditorSceneManager.SaveScene(scene);
                int maps = LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0;
                string line = $"{Path.GetFileNameWithoutExtension(scenePath)}: {(ok ? "baked" : "FAILED")} in {sw.Elapsed.TotalSeconds:F0} s, {maps} lightmap(s)";
                log.AppendLine(line);
                Debug.Log("[PoDecath] " + line);
            }

            log.AppendLine($"total {total.Elapsed.TotalMinutes:F1} min");
            string logDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "training", "logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, "lighting_bake.log"), log.ToString());
            if (!string.IsNullOrEmpty(wasOpen) && File.Exists(wasOpen)) EditorSceneManager.OpenScene(wasOpen, OpenSceneMode.Single);
            AssetDatabase.SaveAssets();
            Debug.Log("[PoDecath] Lighting bake complete:\n" + log);
        }

        /// <summary>
        /// The building needs a second UV set to be lightmapped, and the glTFast importer only makes one
        /// when told to. Turning that on reimports the 122 MB glb once, which is why this is here and not
        /// in every scene build.
        /// </summary>
        static void EnsureBuildingLightmapUVs(StringBuilder log)
        {
            AssetImporter importer = AssetImporter.GetAtPath(WhiteHousePath);
            if (importer == null) { log.AppendLine("WhiteHouse.glb importer not found"); return; }
            var so = new SerializedObject(importer);
            SerializedProperty p = so.FindProperty("editorImportSettings.generateSecondaryUVSet");
            if (p == null) { log.AppendLine("glTFast importer has no generateSecondaryUVSet setting; building not lightmapped"); return; }
            if (p.boolValue) return;
            var sw = Stopwatch.StartNew();
            p.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();
            log.AppendLine($"WhiteHouse.glb reimported with lightmap UVs in {sw.Elapsed.TotalSeconds:F0} s");
        }
    }
}
