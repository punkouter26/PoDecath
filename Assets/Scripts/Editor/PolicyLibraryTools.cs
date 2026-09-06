using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Unity.InferenceEngine;
using PoDecath.Sim;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// Keeps Assets/Policies/Resources/PolicyLibrary.asset in sync with the .onnx checkpoints
    /// under Assets/Policies. Runs automatically on import and from the PoDecath menu.
    /// </summary>
    public static class PolicyLibraryTools
    {
        public const string PoliciesDir = "Assets/Policies";
        public const string ResourcesDir = "Assets/Policies/Resources";
        public const string LibraryPath = ResourcesDir + "/PolicyLibrary.asset";
        public const string DefaultConfigPath = PoliciesDir + "/Athlete_PolicyConfig.asset";

        [MenuItem("PoDecath/Refresh Policy Library")]
        public static void Refresh()
        {
            PolicyLibrary lib = GetOrCreateLibrary();
            PolicyConfig cfg = GetOrCreateDefaultConfig();
            if (lib.defaultConfig == null) lib.defaultConfig = cfg;

            var existing = new Dictionary<ModelAsset, PolicyEntry>();
            foreach (var e in lib.entries) if (e != null && e.model != null && !existing.ContainsKey(e.model)) existing[e.model] = e;

            var fresh = new List<PolicyEntry>();
            foreach (string guid in AssetDatabase.FindAssets("t:ModelAsset", new[] { PoliciesDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(path);
                if (model == null) continue;
                if (!existing.TryGetValue(model, out PolicyEntry entry))
                    entry = new PolicyEntry { displayName = Path.GetFileNameWithoutExtension(path), model = model, config = cfg };
                if (entry.config == null) entry.config = cfg;
                if (string.IsNullOrEmpty(entry.displayName)) entry.displayName = model.name;
                fresh.Add(entry);
            }
            fresh.Sort((a, b) => string.CompareOrdinal(a.displayName, b.displayName));
            lib.entries = fresh;
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PoDecath] Policy library refreshed: {fresh.Count} checkpoint(s).");
        }

        public static PolicyLibrary GetOrCreateLibrary()
        {
            EnsureFolder(PoliciesDir);
            EnsureFolder(ResourcesDir);
            var lib = AssetDatabase.LoadAssetAtPath<PolicyLibrary>(LibraryPath);
            if (lib == null)
            {
                lib = ScriptableObject.CreateInstance<PolicyLibrary>();
                AssetDatabase.CreateAsset(lib, LibraryPath);
            }
            return lib;
        }

        public static PolicyConfig GetOrCreateDefaultConfig()
        {
            EnsureFolder(PoliciesDir);
            var cfg = AssetDatabase.LoadAssetAtPath<PolicyConfig>(DefaultConfigPath);
            if (cfg == null)
            {
                cfg = ScriptableObject.CreateInstance<PolicyConfig>();
                cfg.ApplyGo2Defaults();
                AssetDatabase.CreateAsset(cfg, DefaultConfigPath);
            }
            return cfg;
        }

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }

    class PolicyLibraryPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (Touches(imported) || Touches(deleted) || Touches(moved) || Touches(movedFrom))
                EditorApplication.delayCall += PolicyLibraryTools.Refresh;
        }

        static bool Touches(string[] paths)
        {
            foreach (string p in paths)
                if (p.StartsWith(PolicyLibraryTools.PoliciesDir) && p.EndsWith(".onnx", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
