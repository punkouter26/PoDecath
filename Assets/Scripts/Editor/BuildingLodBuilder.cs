using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Gives the White House its levels of detail.
    ///
    /// The source glb is 833k triangles, and the rooftop scenes were drawing all of it, every frame, on a
    /// budget of 200k for a phone. <c>training/tools/whitehouse_lods.py</c> decimates a copy in Blender
    /// (from the exported glb, never from the owner's .blend) into <c>WhiteHouse_LOD1.glb</c> at a third
    /// of the triangles and <c>WhiteHouse_LOD2.glb</c> at a twelfth, with the small detail (windows, cars,
    /// props) dropped at LOD2. Those files carry material names and no textures: every renderer in them is
    /// pointed here at the LOD0 material of the same name, so the 100 MB of textures ship once.
    ///
    /// One <see cref="LODGroup"/> per part rather than one for the building. The group switches on the
    /// part's own screen height, and from the deck the residence is always enormous while a tree on the
    /// south lawn is a few pixels, which is exactly the distinction a single group could not make.
    ///
    /// On the mobile tier <see cref="Env.RenderTier"/> sets <c>QualitySettings.maximumLODLevel</c> to 1,
    /// so a phone never draws LOD0 at all: its building is the decimated one. That, not the distance
    /// switching, is what brings the model under budget.
    /// </summary>
    public static class BuildingLodBuilder
    {
        public const string Lod1Path = "Assets/Models/WhiteHouse_LOD1.glb";
        public const string Lod2Path = "Assets/Models/WhiteHouse_LOD2.glb";

        // Screen-relative heights below which each level takes over. A part taller than 45% of the screen
        // is drawn in full; under 12% it is the LOD2 shell; under 1% it is culled. A part with only a LOD1
        // (windows, cars) is culled under 2% instead of dropping to a level it does not have.
        const float Lod1Below = 0.45f;
        const float Lod2Below = 0.12f;
        const float CullBelow = 0.01f;
        const float SmallCullBelow = 0.02f;

        /// <summary>
        /// Attaches the decimated copies under <paramref name="building"/> and builds one LODGroup per
        /// part that has a counterpart. Returns how many groups were made; zero, with a warning, when the
        /// LOD files have not been generated.
        /// </summary>
        public static int Attach(GameObject building)
        {
            var lod1Asset = AssetDatabase.LoadAssetAtPath<GameObject>(Lod1Path);
            var lod2Asset = AssetDatabase.LoadAssetAtPath<GameObject>(Lod2Path);
            if (lod1Asset == null)
            {
                Debug.LogWarning($"[PoDecath] {Lod1Path} missing; run training/tools/whitehouse_lods.py in Blender to make the LODs. "
                               + "The building is drawn at full detail on every tier until then.");
                return 0;
            }

            // LOD0 renderers and their materials by name, taken before the LOD children are added.
            var lod0 = new Dictionary<string, MeshRenderer>();
            var materials = new Dictionary<string, Material>();
            foreach (MeshRenderer r in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                lod0[r.gameObject.name] = r;
                foreach (Material m in r.sharedMaterials)
                    if (m != null && !materials.ContainsKey(m.name)) materials[m.name] = m;
            }

            TuneLawn(lod0, materials);
            int grained = ApplyStoneDetail(lod0);
            if (grained > 0) Debug.Log($"[PoDecath] Stone grain pass on {grained} full-detail wall(s).");

            Dictionary<string, MeshRenderer> lod1 = Instantiate(lod1Asset, building.transform, "LOD1", materials);
            Dictionary<string, MeshRenderer> lod2 = lod2Asset != null
                ? Instantiate(lod2Asset, building.transform, "LOD2", materials)
                : new Dictionary<string, MeshRenderer>();
            int baked = ApplyFacadeBakes(lod2);
            if (baked > 0) Debug.Log($"[PoDecath] {baked} LOD2 part(s) carry baked facade textures from {BakeDir}.");

            int groups = 0;
            foreach (KeyValuePair<string, MeshRenderer> kv in lod0)
            {
                lod1.TryGetValue(kv.Key, out MeshRenderer r1);
                lod2.TryGetValue(kv.Key, out MeshRenderer r2);
                if (r1 == null && r2 == null) continue;

                var levels = new List<LOD> { new LOD(Lod1Below, new Renderer[] { kv.Value }) };
                if (r1 != null) levels.Add(new LOD(r2 != null ? Lod2Below : SmallCullBelow, new Renderer[] { r1 }));
                if (r2 != null) levels.Add(new LOD(CullBelow, new Renderer[] { r2 }));

                LODGroup group = kv.Value.gameObject.GetComponent<LODGroup>();
                if (group == null) group = kv.Value.gameObject.AddComponent<LODGroup>();
                group.fadeMode = LODFadeMode.None;
                group.SetLODs(levels.ToArray());
                group.RecalculateBounds();
                groups++;
            }

            // Anything in a LOD file with no LOD0 counterpart would be drawn on top of nothing: hide it.
            foreach (KeyValuePair<string, MeshRenderer> kv in lod1) if (!lod0.ContainsKey(kv.Key)) kv.Value.enabled = false;
            foreach (KeyValuePair<string, MeshRenderer> kv in lod2) if (!lod0.ContainsKey(kv.Key)) kv.Value.enabled = false;
            return groups;
        }

        public const string BakeDir = "Assets/Textures/Building";

        /// <summary>
        /// Multiplier on the lawn's base colour (linear), 1 = as imported.
        ///
        /// The grounds' turf read pale and minty once the rooftop was baked. Measured 2026-09-29 on
        /// RooftopLap from 18 m up at 45 degrees (UiShots.LawnProbe): baked rgb(138,191,112), and the same
        /// with the turf taken off the lightmap and lit by the sky probe instead is identical, so the bake is
        /// not what brightens it; the afternoon sky at ambient 1.15 on a bright texture is. The "deep
        /// green" it was remembered as, rgb(102,135,80), is the turf with no sky light at all. Real turf has
        /// an albedo around 0.1 to 0.25; 0.72 on this texture puts the lit lawn roughly halfway back to that
        /// green without making it look unlit. The owner's glb is not touched: a tuned copy of its material
        /// goes on the turf at every LOD.
        /// </summary>
        const float LawnTone = 0.72f;
        const string LawnMaterialName = "WH_Lawn_Tiled";
        const string LawnTunedPath = BakeDir + "/WH_Lawn_Tiled_Tuned.mat";

        static void TuneLawn(Dictionary<string, MeshRenderer> lod0, Dictionary<string, Material> materials)
        {
            if (!materials.TryGetValue(LawnMaterialName, out Material source) || !source.HasProperty("baseColorFactor")) return;
            var tuned = AssetDatabase.LoadAssetAtPath<Material>(LawnTunedPath);
            if (tuned == null)
            {
                System.IO.Directory.CreateDirectory(BakeDir);
                tuned = new Material(source);
                AssetDatabase.CreateAsset(tuned, LawnTunedPath);
            }
            tuned.CopyPropertiesFromMaterial(source);
            Color f = source.GetColor("baseColorFactor");
            tuned.SetColor("baseColorFactor", new Color(f.r * LawnTone, f.g * LawnTone, f.b * LawnTone, f.a));
            EditorUtility.SetDirty(tuned);

            foreach (MeshRenderer r in lod0.Values)
            {
                Material[] mats = r.sharedMaterials;
                bool hit = false;
                for (int i = 0; i < mats.Length; i++) if (mats[i] == source) { mats[i] = tuned; hit = true; }
                if (hit) r.sharedMaterials = mats;
            }
            materials[LawnMaterialName] = tuned;   // the LOD copies look materials up by the original name
        }

        const string GrainPath = BakeDir + "/StoneGrain.png";
        const string GrainMaterialPath = BakeDir + "/StoneGrain.mat";
        static readonly string[] Shells = { "Residence", "Wings", "SouthPortico", "NorthPortico" };

        /// <summary>
        /// The walls' close-up detail. Their textures are 4096 px atlases over a 150 m building (7 to 13
        /// texels a metre), soft within a few metres of the camera, and the glTF material has no detail slot.
        /// A second material on each full-detail shell (Assets/Shaders/StoneDetail.shader) multiplies a
        /// tileable stone grain onto the wall in world metres and fades it out with distance, leaving the
        /// imported material untouched. LOD0 only, which the phone never draws. Returns how many shells.
        /// </summary>
        static int ApplyStoneDetail(Dictionary<string, MeshRenderer> lod0)
        {
            Shader shader = Shader.Find("PoDecath/StoneDetail");
            if (shader == null) return 0;
            Texture2D grain = EnsureGrain();
            var mat = AssetDatabase.LoadAssetAtPath<Material>(GrainMaterialPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = "StoneGrain" };
                AssetDatabase.CreateAsset(mat, GrainMaterialPath);
            }
            mat.shader = shader;
            mat.SetTexture("_DetailMap", grain);
            EditorUtility.SetDirty(mat);

            int n = 0;
            foreach (string shell in Shells)
            {
                if (!lod0.TryGetValue(shell, out MeshRenderer r)) continue;
                var mats = new List<Material>(r.sharedMaterials);
                if (mats.Contains(mat)) { n++; continue; }
                mats.Add(mat);   // one submesh, so the extra slot draws the whole shell again
                r.sharedMaterials = mats.ToArray();
                n++;
            }
            return n;
        }

        /// <summary>
        /// 512 px of tileable stone grain around 0.5, written once and kept: mottled weathering over about a
        /// quarter of the tile, a finer grain, and a speckle, all periodic so the tile has no seam. Linear
        /// data, so 0.5 is exactly the neutral of the shader's 2x multiply.
        /// </summary>
        static Texture2D EnsureGrain()
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(GrainPath);
            if (tex == null)
            {
                const int size = 512;
                var px = new Color32[size * size];
                var rng = new System.Random(7331);
                float[] speck = new float[size * size];
                for (int i = 0; i < speck.Length; i++) speck[i] = (float)rng.NextDouble() * 2f - 1f;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float v = 0.5f
                                + 0.070f * Periodic(x, y, 128, size, 11)
                                + 0.045f * Periodic(x, y, 32, size, 23)
                                + 0.025f * Periodic(x, y, 8, size, 37)
                                + 0.020f * speck[y * size + x];
                        byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
                        px[y * size + x] = new Color32(b, b, b, 255);
                    }
                var t = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
                t.SetPixels32(px);
                System.IO.Directory.CreateDirectory(BakeDir);
                System.IO.File.WriteAllBytes(GrainPath, t.EncodeToPNG());
                Object.DestroyImmediate(t);
                AssetDatabase.ImportAsset(GrainPath);
            }
            if (AssetImporter.GetAtPath(GrainPath) is TextureImporter imp && (imp.sRGBTexture || imp.wrapMode != TextureWrapMode.Repeat))
            {
                imp.sRGBTexture = false;
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.mipmapEnabled = true;
                imp.anisoLevel = 4;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(GrainPath);
        }

        /// <summary>Smooth value noise in -1..1 on a lattice of <paramref name="cell"/> px that wraps at <paramref name="size"/>.</summary>
        static float Periodic(int x, int y, int cell, int size, int seed)
        {
            int cells = size / cell;
            float fx = (float)x / cell, fy = (float)y / cell;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float a = Hash(x0 % cells, y0 % cells, seed), b = Hash((x0 + 1) % cells, y0 % cells, seed);
            float c = Hash(x0 % cells, (y0 + 1) % cells, seed), d = Hash((x0 + 1) % cells, (y0 + 1) % cells, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 144665);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFF) / 32767.5f - 1f;
            }
        }

        /// <summary>
        /// A LOD2 part whose relief and colour have been baked down from the full-detail mesh
        /// (<c>training/tools/whitehouse_facade_bake.py</c>) gets its own material instead of the LOD0
        /// one by name: the bake's albedo and tangent-space normal on a lit URP surface. This is what
        /// lets a phone draw the 68k-triangle shell and still see columns and window reveals. Parts with
        /// no <c>&lt;name&gt;_bakeD.png</c> under <see cref="BakeDir"/> are left exactly as before, so
        /// this is a no-op until the bake has been run.
        /// </summary>
        static int ApplyFacadeBakes(Dictionary<string, MeshRenderer> lod2)
        {
            int n = 0;
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) return 0;
            foreach (KeyValuePair<string, MeshRenderer> kv in lod2)
            {
                string albedoPath = $"{BakeDir}/{kv.Key}_bakeD.png";
                string normalPath = $"{BakeDir}/{kv.Key}_bakeN.png";
                var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
                if (albedo == null) continue;
                var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
                if (normal != null)
                {
                    // The bake writes a plain PNG; Unity has to be told it is a normal map or it is read
                    // as colour and the relief comes out flat and blue.
                    var imp = AssetImporter.GetAtPath(normalPath) as TextureImporter;
                    if (imp != null && imp.textureType != TextureImporterType.NormalMap)
                    {
                        imp.textureType = TextureImporterType.NormalMap;
                        imp.SaveAndReimport();
                    }
                }

                string matPath = $"{BakeDir}/{kv.Key}_Baked.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    mat = new Material(lit);
                    AssetDatabase.CreateAsset(mat, matPath);
                }
                mat.SetTexture("_BaseMap", albedo);
                if (normal != null)
                {
                    mat.SetTexture("_BumpMap", normal);
                    mat.EnableKeyword("_NORMALMAP");
                }
                EditorUtility.SetDirty(mat);

                Material[] mats = kv.Value.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                kv.Value.sharedMaterials = mats;
                n++;
            }
            return n;
        }

        static Dictionary<string, MeshRenderer> Instantiate(GameObject asset, Transform parent, string name,
                                                            Dictionary<string, Material> materials)
        {
            var go = PrefabUtility.InstantiatePrefab(asset) as GameObject;
            if (go == null) go = Object.Instantiate(asset);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var found = new Dictionary<string, MeshRenderer>();
            foreach (MeshRenderer r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                found[r.gameObject.name] = r;
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && materials.TryGetValue(mats[i].name, out Material m0)) mats[i] = m0;
                r.sharedMaterials = mats;
                r.gameObject.isStatic = true;
                foreach (Collider c in r.GetComponents<Collider>()) Object.DestroyImmediate(c);
            }
            return found;
        }
    }
}
