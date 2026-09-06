using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;

namespace PoDecath.Sim
{
    [Serializable]
    public class PolicyEntry
    {
        public string displayName;
        public ModelAsset model;
        public PolicyConfig config;
    }

    /// <summary>
    /// Registry of checkpoints found under Assets/Policies. The editor post-processor
    /// (PolicyLibraryPostprocessor) keeps it in sync when .onnx files are dropped in.
    /// Lives in Assets/Policies/Resources so it is addressable at runtime on mobile.
    /// </summary>
    [CreateAssetMenu(menuName = "PoDecath/Policy Library", fileName = "PolicyLibrary")]
    public class PolicyLibrary : ScriptableObject
    {
        public const string ResourceName = "PolicyLibrary";

        public PolicyConfig defaultConfig;
        public List<PolicyEntry> entries = new List<PolicyEntry>();

        public static PolicyLibrary Load() => Resources.Load<PolicyLibrary>(ResourceName);

        public PolicyEntry Get(int index)
        {
            if (entries == null || entries.Count == 0) return null;
            index = Mathf.Clamp(index, 0, entries.Count - 1);
            return entries[index];
        }
    }
}
