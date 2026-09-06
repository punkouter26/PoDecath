using System;
using UnityEngine;

namespace PoDecath.Env
{
    /// <summary>Which of the two quality levels in the project the game is running at.</summary>
    public enum Tier
    {
        /// <summary>Quality level 0, <c>Mobile_RPAsset</c>: 0.8 render scale, one cascade, no soft shadows.</summary>
        Mobile = 0,
        /// <summary>Quality level 1, <c>PC_RPAsset</c>: full shadows, SSAO, depth of field, MSAA.</summary>
        PC = 1,
    }

    /// <summary>
    /// The one place that answers "how much can this machine afford?". Every visual and audio feature
    /// added on top of the base game asks here rather than testing <see cref="Application.isMobilePlatform"/>
    /// for itself, so a single switch moves the whole look between the two URP assets the project already
    /// ships instead of ten features each deciding separately and disagreeing.
    ///
    /// The tier is a quality level index, so setting it swaps the render pipeline asset with it:
    /// <c>QualitySettings</c> level 0 is <c>Mobile_RPAsset</c> and level 1 is <c>PC_RPAsset</c>. Anything
    /// that cannot be expressed as a URP asset setting — particle counts, trail lengths, how many crowd
    /// emitters get placed — reads the budgets below.
    /// </summary>
    public static class RenderTier
    {
        const string Key = "podecath.tier";

        static Tier _current;
        static bool _resolved;

        /// <summary>Raised after <see cref="Set"/> changes the tier, so live scenes can re-budget.</summary>
        public static event Action<Tier> Changed;

        /// <summary>
        /// The tier in force. Resolved once from the saved preference, or from the device the first time
        /// the game runs on it: a phone or a machine with little VRAM starts on Mobile, everything else on PC.
        /// </summary>
        public static Tier Current
        {
            get
            {
                if (!_resolved) Resolve();
                return _current;
            }
        }

        public static bool IsMobile => Current == Tier.Mobile;

        static void Resolve()
        {
            _resolved = true;
            if (PlayerPrefs.HasKey(Key))
            {
                _current = (Tier)Mathf.Clamp(PlayerPrefs.GetInt(Key), 0, 1);
                return;
            }
            bool small = Application.isMobilePlatform
                      || SystemInfo.graphicsMemorySize > 0 && SystemInfo.graphicsMemorySize < 2048
                      || SystemInfo.processorCount <= 4;
            _current = small ? Tier.Mobile : Tier.PC;
        }

        /// <summary>
        /// Switches tier, applies the quality level (and with it the render pipeline asset) and tells
        /// everything listening to re-budget. Saved, so the choice survives the next launch.
        /// </summary>
        public static void Set(Tier tier)
        {
            _resolved = true;
            _current = tier;
            PlayerPrefs.SetInt(Key, (int)tier);
            PlayerPrefs.Save();
            Apply();
            Changed?.Invoke(tier);
        }

        /// <summary>
        /// Pushes the current tier into <see cref="QualitySettings"/>. Called on every scene load rather
        /// than only on a change, because the editor's own quality level is whatever it was left on.
        /// </summary>
        public static void Apply()
        {
            int level = (int)Current;
            if (QualitySettings.GetQualityLevel() != level) QualitySettings.SetQualityLevel(level, true);
        }

        // ------------------------------------------------------------------ budgets
        //
        // Everything below is a number a feature would otherwise have hard-coded. They are deliberately
        // blunt: two values, one per tier, chosen so the mobile column can be justified against a 60 FPS
        // phone budget and the PC column against what the broadcast look wants.

        /// <summary>Particles a single one-shot burst may emit (sand, dust, sparks).</summary>
        public static int BurstParticles => IsMobile ? 24 : 90;

        /// <summary>Athletes that get a full trail ribbon; the rest get none. Sorted by race position.</summary>
        public static int TrailedAthletes => IsMobile ? 3 : 16;

        /// <summary>Seconds of ribbon behind a trailed athlete.</summary>
        public static float TrailSeconds => IsMobile ? 0.35f : 0.7f;

        /// <summary>Depth of field is a full-screen blur pass; the mobile asset cannot afford it.</summary>
        public static bool DepthOfField => !IsMobile;

        /// <summary>Screen-space ambient occlusion, the pass that plants feet on the deck.</summary>
        public static bool AmbientOcclusion => !IsMobile;

        /// <summary>Real-time shadows for athletes. Mobile gets a blob projector instead.</summary>
        public static bool AthleteShadows => !IsMobile;

        /// <summary>Crowd emitters placed round the deck. One is a mono bed; eight pans properly.</summary>
        public static int CrowdEmitters => IsMobile ? 4 : 8;

        /// <summary>Footfall sources allowed to be audible at once, cheapest-first culled by distance.</summary>
        public static int FootstepVoices => IsMobile ? 6 : 16;

        /// <summary>Heat haze, wet-deck reflections and the other second-order surface tricks.</summary>
        public static bool SurfaceExtras => !IsMobile;
    }
}
