using UnityEngine;
using Unity.InferenceEngine;

namespace PoDecath.Sim
{
    public enum AthleteKind
    {
        /// <summary>Heuristic-coded bot. Always RED.</summary>
        Heuristic,
        /// <summary>Standard RL policy on the reference rig. Always GREEN.</summary>
        ReferenceRL,
        /// <summary>RL variation with an owner-supplied texture and/or skinned mesh.</summary>
        CustomRL,
    }

    /// <summary>One roster entry. House rules: heuristic = red, reference RL = green, custom = own texture.</summary>
    [CreateAssetMenu(menuName = "PoDecath/Athlete Definition", fileName = "Athlete")]
    public class AthleteDefinition : ScriptableObject
    {
        public string displayName = "Athlete";
        public AthleteKind kind = AthleteKind.ReferenceRL;

        [Header("RL")]
        public ModelAsset model;
        [Tooltip("MJCF used to build the physics rig. Leave empty to use the spawner default.")]
        public TextAsset mjcfOverride;
        public float actionScale = 0.5f;

        [Header("Heuristic")]
        public float topSpeed = 9.0f;
        public float accelSeconds = 3.0f;

        [Header("Look")]
        [Tooltip("Rigged glb for this athlete (drop the .glb in Assets/Models and drag it here). Leave empty to use the spawner default (Matt).")]
        public GameObject skinOverride;
        [Tooltip("Physics body -> bone name pairs for this glb's skeleton. Leave empty for Mixamo names (Hips, Spine, LeftUpLeg, ...).")]
        public System.Collections.Generic.List<SkinBinder.BoneMap> boneMap = new System.Collections.Generic.List<SkinBinder.BoneMap>();
        [Tooltip("Rotation applied to the glb root so its rest pose faces the rig's forward axis (+X). Mixamo/glTF characters need (0, 90, 0).")]
        public Vector3 skinRootEuler = new Vector3(0f, 90f, 0f);
        [Tooltip("Custom texture for CustomRL athletes.")]
        public Texture2D customTexture;
        [Tooltip("Tint override. Ignored for Heuristic (red) and ReferenceRL (green).")]
        public Color customTint = Color.white;

        public static readonly Color HeuristicRed = new Color(0.9f, 0.12f, 0.1f, 1f);
        public static readonly Color ReferenceGreen = new Color(0.1f, 0.85f, 0.25f, 1f);

        public Color Tint => kind switch
        {
            AthleteKind.Heuristic => HeuristicRed,
            AthleteKind.ReferenceRL => ReferenceGreen,
            _ => customTint,
        };
    }
}
