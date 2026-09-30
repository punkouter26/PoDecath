using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using PoDecath.Sim;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// A head-and-shoulders picture of every athlete, for the tiles on the setup menu.
    ///
    /// The house rule keeps every athlete on the textures their model came with, so the colour stripe is the
    /// only other thing that tells a field apart, and a name is slow to find in a grid of nine. A face is
    /// the fastest way there is. Rendered from the athlete's own skin in the editor, not drawn: the picture
    /// is the model the race will show, at its bind pose, lit by two fixed lights so every tile matches.
    ///
    /// Framing is by proportion rather than by a head bone, because the bone maps stop at the torso (the
    /// physics rig has no head body) and the roster mixes Mixamo, AccuRig and numbered skeletons. The top
    /// 30% of the posed body, centred 13% down from the crown over the head, is a bust on every model on the roster,
    /// Trump's 1.15 m included. The skin is turned by the definition's own skinRootEuler, which is what
    /// faces it down the rig's +X, so the camera stands on +X and looks back at it.
    ///
    /// Writes Assets/UI/Portraits/&lt;displayName&gt;.png and points AthleteDefinition.portrait at it. Deterministic,
    /// so a re-bake does not churn the repository. Run by PoDecath/Rebuild Athlete Roster as well.
    /// </summary>
    public static class PortraitBakery
    {
        const string Folder = "Assets/UI/Portraits";
        const string DefaultSkin = "Assets/Models/Athlete_Matt.glb";
        const int Size = 256;
        const float FieldOfView = 18f;

        /// <summary>The tile's own ground (--surface-2 in Theme.uss), so the picture sits in it without an edge.</summary>
        static readonly Color Ground = new Color(24f / 255f, 29f / 255f, 40f / 255f, 1f);

        [MenuItem("PoDecath/Bake Athlete Portraits", priority = 7)]
        public static void BakeAll()
        {
            Directory.CreateDirectory(Folder);
            var baked = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:AthleteDefinition", new[] { "Assets/Athletes" }))
            {
                var def = AssetDatabase.LoadAssetAtPath<AthleteDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (def == null) continue;
                string path = Bake(def);
                if (path != null) baked.Add($"{def.displayName} -> {path}");
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[PortraitBakery] {baked.Count} portrait(s):\n  " + string.Join("\n  ", baked));
        }

        static string Bake(AthleteDefinition def)
        {
            GameObject skin = def.skinOverride != null ? def.skinOverride : AssetDatabase.LoadAssetAtPath<GameObject>(DefaultSkin);
            if (skin == null) { Debug.LogWarning($"[PortraitBakery] {def.displayName}: no skin to render."); return null; }

            var pru = new PreviewRenderUtility();
            try
            {
                GameObject go = pru.InstantiatePrefabInScene(skin);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(def.skinRootEuler));
                // The same stale bounds decide what the camera culls, so a body framed correctly from its
                // vertices was still not drawn. Bounds from the pose, every frame, for this one render.
                foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>()) smr.updateWhenOffscreen = true;

                List<Vector3> points = WorldVertices(go);
                if (points.Count == 0) { Debug.LogWarning($"[PortraitBakery] {def.displayName}: the skin has no vertices."); return null; }

                // Framed from the posed vertices, not from Renderer.bounds: a skinned renderer's bounds are
                // whatever the importer stored, and on half the roster they are nowhere near the body (two
                // portraits came out empty and two had the head in a corner). The head is the top 13% of
                // the body; the camera centres on the middle of the vertices in that band.
                float top = float.MinValue, bottom = float.MaxValue;
                foreach (Vector3 v in points) { top = Mathf.Max(top, v.y); bottom = Mathf.Min(bottom, v.y); }
                float h = top - bottom;
                Vector3 sum = Vector3.zero;
                int n = 0;
                foreach (Vector3 v in points)
                {
                    if (v.y < top - 0.13f * h) continue;
                    sum += v;
                    n++;
                }
                Vector3 head = sum / Mathf.Max(1, n);
                // 30% of the body, but never under 0.46 m: on the 1 m models a third of the height is a
                // face and no shoulders.
                float frame = Mathf.Max(0.30f * h, 0.46f);
                var centre = new Vector3(head.x, top - 0.43f * frame, head.z);
                float distance = frame * 0.5f / Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);

                Camera cam = pru.camera;
                cam.fieldOfView = FieldOfView;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = distance + 10f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Ground;
                // A touch above eye level and a little off-axis, the way a trading card is shot.
                Vector3 eye = centre + Quaternion.Euler(-6f, 18f, 0f) * (Vector3.right * distance);
                cam.transform.position = eye;
                cam.transform.LookAt(centre);

                pru.lights[0].intensity = 1.25f;
                pru.lights[0].transform.rotation = Quaternion.LookRotation(centre - (eye + Vector3.up * distance * 0.8f + Vector3.forward * distance * 0.6f));
                pru.lights[1].intensity = 0.55f;
                pru.lights[1].transform.rotation = Quaternion.LookRotation(centre - (eye - Vector3.forward * distance));
                pru.ambientColor = new Color(0.42f, 0.44f, 0.5f);

                pru.BeginStaticPreview(new Rect(0, 0, Size, Size));
                pru.Render(true);
                Texture2D tex = pru.EndStaticPreview();

                string path = $"{Folder}/{Sanitise(def.displayName)}.png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                if (AssetImporter.GetAtPath(path) is TextureImporter ti)
                {
                    bool dirty = ti.mipmapEnabled || ti.textureType != TextureImporterType.Default || ti.maxTextureSize != Size;
                    ti.textureType = TextureImporterType.Default;
                    ti.mipmapEnabled = false;
                    ti.maxTextureSize = Size;
                    ti.wrapMode = TextureWrapMode.Clamp;
                    if (dirty) ti.SaveAndReimport();
                }

                var tex2 = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (def.portrait != tex2)
                {
                    def.portrait = tex2;
                    EditorUtility.SetDirty(def);
                }
                return path;
            }
            finally
            {
                pru.Cleanup();
            }
        }

        /// <summary>Every vertex of the skin where it actually is: skinned meshes baked in their bind pose, plain meshes as placed.</summary>
        static List<Vector3> WorldVertices(GameObject go)
        {
            var points = new List<Vector3>();
            var baked = new Mesh();
            foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.sharedMesh == null) continue;
                // Baked without the renderer's scale, then moved and turned, not scaled. The bones already put
                // the vertices in metres: Grandma, Grandpa and Nick carry a 0.01 scale on the renderer, and
                // baking "with scale" divided by it (170 m tall), while the full local-to-world applied it a
                // second time (1.7 cm).
                smr.BakeMesh(baked, false);
                Matrix4x4 m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                foreach (Vector3 v in baked.vertices) points.Add(m.MultiplyPoint3x4(v));
            }
            Object.DestroyImmediate(baked);
            foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 m = mf.transform.localToWorldMatrix;
                foreach (Vector3 v in mf.sharedMesh.vertices) points.Add(m.MultiplyPoint3x4(v));
            }
            return points;
        }

        static string Sanitise(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }
    }
}
