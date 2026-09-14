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
