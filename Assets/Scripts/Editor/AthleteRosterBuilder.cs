using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Unity.InferenceEngine;
using PoDecath.Sim;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Turns every rigged character in <c>Assets/Models/Characters</c> into a roster entry, so adding an
    /// athlete to the game is a matter of dropping a <c>.glb</c> or <c>.fbx</c> in that folder and running
    /// the menu item.
    ///
    /// The bodies are all the same: one MJCF rig, one trained policy. What differs is the skin, the
    /// skeleton it is bound through and the height the artist happened to author it at -- 1.84 m for the
    /// reference Matt, 1.15 m for the Trump model. <see cref="SkeletonMapper"/> works out the bone map and
    /// which way the model faces; <see cref="SkinBinder"/> sizes it to the rig at bind time. Both results
    /// are written into the <see cref="AthleteDefinition"/> so they can be read and corrected by hand.
    ///
    /// House rule 5 applies throughout: the skins keep the textures they were imported with, and the
    /// colour on the definition is only ever used by the UI -- the menu swatch, the results row and the
    /// trail ribbon that tells a field of look-alikes apart on the deck.
    /// </summary>
    public static class AthleteRosterBuilder
    {
        public const string CharactersDir = "Assets/Models/Characters";
        const string AthletesDir = "Assets/Athletes";

        /// <summary>Lane colours for the UI. Deliberately far apart in hue; nothing here touches a skin.</summary>
        static readonly Color[] Palette =
        {
            new Color(0.20f, 0.55f, 0.95f, 1f),   // blue
            new Color(0.95f, 0.45f, 0.10f, 1f),   // orange
            new Color(0.70f, 0.35f, 0.90f, 1f),   // violet
            new Color(0.10f, 0.80f, 0.80f, 1f),   // cyan
            new Color(0.95f, 0.25f, 0.55f, 1f),   // pink
            new Color(0.60f, 0.75f, 0.20f, 1f),   // lime
            new Color(0.85f, 0.70f, 0.20f, 1f),   // gold
            new Color(0.45f, 0.50f, 0.95f, 1f),   // periwinkle
        };

        [MenuItem("PoDecath/Rebuild Athlete Roster", priority = 6)]
        public static void RebuildMenu()
        {
            var policy = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Policies/athlete_track.onnx")
                      ?? AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Policies/athlete_run.onnx");
            var report = new StringBuilder();
            List<AthleteDefinition> made = BuildCharacters(policy, report);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoDecath] Athlete roster: {made.Count} character(s) from {CharactersDir}.\n{report}");
        }

        /// <summary>
        /// One <see cref="AthleteDefinition"/> per model in the characters folder, created on first sight
        /// and refreshed afterwards. Anything the owner has changed by hand on an existing definition --
        /// its name, its colour, a bone map they corrected -- is left alone; only an empty bone map is
        /// filled in, so re-running this is safe.
        /// </summary>
        public static List<AthleteDefinition> BuildCharacters(ModelAsset policy, StringBuilder report = null)
        {
            var made = new List<AthleteDefinition>();
            if (!AssetDatabase.IsValidFolder(CharactersDir))
            {
                report?.AppendLine($"  {CharactersDir} does not exist; no custom athletes.");
                return made;
            }

            string[] guids = AssetDatabase.FindAssets("t:GameObject", new[] { CharactersDir });
            var paths = new List<string>();
            foreach (string g in guids) paths.Add(AssetDatabase.GUIDToAssetPath(g));
            paths.Sort(System.StringComparer.OrdinalIgnoreCase);

            PolicyLibraryTools.EnsureFolder(AthletesDir);
            int index = 0;
            foreach (string path in paths)
            {
                var skin = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (skin == null) continue;
                if (skin.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                {
                    report?.AppendLine($"  {System.IO.Path.GetFileName(path)}: no skinned mesh, skipped.");
                    continue;
                }

                if (DressFbx(path, report))
                    skin = AssetDatabase.LoadAssetAtPath<GameObject>(path);   // reimported; the old handle is stale

                AthleteDefinition def = Ensure(path, skin, policy, index++, report);
                if (def != null) made.Add(def);
            }
            return made;
        }

        static AthleteDefinition Ensure(string modelPath, GameObject skin, ModelAsset policy, int index, StringBuilder report)
        {
            string file = System.IO.Path.GetFileNameWithoutExtension(modelPath);
            string assetPath = $"{AthletesDir}/Char_{file}.asset";
            var def = AssetDatabase.LoadAssetAtPath<AthleteDefinition>(assetPath);
            bool fresh = def == null;
            if (fresh)
            {
                def = ScriptableObject.CreateInstance<AthleteDefinition>();
                def.displayName = DisplayName(file);
                def.kind = AthleteKind.CustomRL;
                def.customTint = Palette[index % Palette.Length];
                AssetDatabase.CreateAsset(def, assetPath);
            }

            def.skinOverride = skin;
            if (policy != null) def.model = policy;

            // The bone map is only ever inferred once. After that it is the owner's, because correcting a
            // rig the geometry got wrong is exactly the edit this must not undo on the next run.
            if (def.boneMap == null || def.boneMap.Count == 0)
            {
                SkeletonMapper.Result r = SkeletonMapper.Infer(skin);
                if (r.Complete)
                {
                    def.boneMap = r.map;
                    def.skinRootEuler = r.rootEuler;
                    report?.AppendLine($"  {def.displayName}: mapped 12 bones, faces {r.rootEuler.y:F0} deg, "
                                     + $"hips {r.hipHeight:F2} m{Notes(r)}");
                }
                else
                {
                    report?.AppendLine($"  {def.displayName}: COULD NOT MAP THIS SKELETON{Notes(r)}. It will "
                                     + "race on the default Mixamo names, which may bind nothing. Fill in "
                                     + $"boneMap on {assetPath} by hand.");
                }
            }
            else report?.AppendLine($"  {def.displayName}: kept the existing {def.boneMap.Count}-bone map.");

            EditorUtility.SetDirty(def);
            return def;
        }

        /// <summary>
        /// Gets an FBX character's own textures onto its own skin.
        ///
        /// House rule 5 says an athlete keeps the textures it was imported with, and for an FBX that does
        /// not happen by itself. The zombie carries its diffuse and normal maps inside the file; Unity
        /// imported the mesh, built a material, and bound neither, so the athlete arrived on the deck a
        /// flat untextured grey with nothing in the import log to say why. Pulling the images out is only
        /// half the fix -- the material embedded in the FBX stays empty afterwards -- so the maps are hung
        /// on a real material asset and the importer is told to use that one instead. That is also the
        /// version the owner can open and adjust, which a material sealed inside an FBX is not.
        ///
        /// glTF has none of these problems: glTFast unpacks images and wires them up as a matter of course,
        /// which is why this runs only for the FBX branch, and only when the materials are actually bare.
        ///
        /// Returns true if the model was reimported, which invalidates every handle to it.
        /// </summary>
        static bool DressFbx(string modelPath, StringBuilder report)
        {
            var imp = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            if (imp == null) return false;   // a glTF import; glTFast has already done this

            var bare = new List<Material>();
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(modelPath))
                if (o is Material m && BaseMap(m) == null) bare.Add(m);
            if (bare.Count == 0) return false;

            string file = System.IO.Path.GetFileNameWithoutExtension(modelPath);
            string dir = System.IO.Path.GetDirectoryName(modelPath).Replace('\\', '/');
            string texFolder = $"{dir}/{file}_Textures";
            PolicyLibraryTools.EnsureFolder(texFolder);
            imp.ExtractTextures(texFolder);
            AssetDatabase.Refresh();

            var maps = new List<Texture2D>();
            foreach (string g in AssetDatabase.FindAssets("t:Texture2D", new[] { texFolder }))
            {
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(g));
                if (t != null) maps.Add(t);
            }
            if (maps.Count == 0)
            {
                report?.AppendLine($"  {file}: no embedded textures to extract; this athlete will race "
                                 + "untextured until maps are supplied.");
                return false;
            }

            string matFolder = $"{dir}/{file}_Materials";
            PolicyLibraryTools.EnsureFolder(matFolder);
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            int dressed = 0;
            foreach (Material src in bare)
            {
                Texture2D albedo = Pick(maps, src.name, "diffuse", "albedo", "basecolor", "base_color");
                Texture2D normal = Pick(maps, src.name, "normal", "nrm");
                if (albedo == null) continue;

                string matPath = $"{matFolder}/{src.name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    mat = new Material(lit != null ? lit : src.shader);
                    AssetDatabase.CreateAsset(mat, matPath);
                }
                mat.SetTexture("_BaseMap", albedo);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", albedo);
                if (normal != null && MarkAsNormalMap(normal))
                {
                    mat.SetTexture("_BumpMap", normal);
                    mat.EnableKeyword("_NORMALMAP");
                }
                EditorUtility.SetDirty(mat);
                imp.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), src.name), mat);
                dressed++;
            }
            if (dressed == 0) return false;

            AssetDatabase.SaveAssets();
            imp.SaveAndReimport();
            report?.AppendLine($"  {file}: extracted {maps.Count} embedded texture(s) and bound {dressed} "
                             + $"material(s) in {matFolder}.");
            return true;
        }

        /// <summary>The texture whose name carries this material's name and one of these words.</summary>
        static Texture2D Pick(List<Texture2D> maps, string materialName, params string[] words)
        {
            Texture2D loose = null;
            foreach (Texture2D t in maps)
            {
                string n = t.name.ToLowerInvariant();
                bool wanted = false;
                foreach (string w in words) if (n.Contains(w)) { wanted = true; break; }
                if (!wanted) continue;
                if (n.StartsWith(materialName.ToLowerInvariant())) return t;   // this material's own map
                loose ??= t;
            }
            return loose;
        }

        /// <summary>A normal map imported as a colour texture renders as noise; say what it is.</summary>
        static bool MarkAsNormalMap(Texture2D t)
        {
            var ti = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) as TextureImporter;
            if (ti == null) return false;
            if (ti.textureType == TextureImporterType.NormalMap) return true;
            ti.textureType = TextureImporterType.NormalMap;
            ti.SaveAndReimport();
            return true;
        }

        static Texture BaseMap(Material m)
        {
            foreach (string prop in new[] { "_BaseMap", "_MainTex", "baseColorTexture" })
                if (m.HasProperty(prop) && m.GetTexture(prop) != null) return m.GetTexture(prop);
            return null;
        }

        static string Notes(SkeletonMapper.Result r)
            => r.notes.Count == 0 ? "" : " (" + string.Join("; ", r.notes) + ")";

        /// <summary>
        /// A readable name from a file name: <c>RIGGED_Grandma</c> becomes "Grandma" and
        /// <c>MATT_RiggedAvaturn</c> becomes "Matt Avaturn". The word "Rigged" carries no information
        /// about the athlete, so it goes; whatever is left of the token does not, so it stays.
        /// </summary>
        internal static string DisplayName(string file)
        {
            var words = new List<string>();
            foreach (string raw in file.Split('_', ' ', '-'))
            {
                string w = System.Text.RegularExpressions.Regex.Replace(raw, "(?i)rigged", "");
                if (w.Length == 0) continue;
                words.Add(char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant());
            }
            return words.Count > 0 ? string.Join(" ", words) : file;
        }
    }
}
