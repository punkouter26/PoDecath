using UnityEngine;

namespace PoDecath.Fx
{
    /// <summary>
    /// Every material the effects layer draws with, in one asset — the same arrangement
    /// <c>AudioBank</c> uses for sound, and for the same reason: a scene builder wires one field instead
    /// of eight, and any of them can be replaced by dropping a different material on it.
    ///
    /// The sprites behind these are generated, not painted: <c>PoDecath/Bake Effects</c> writes a soft
    /// radial dot, a lumpier smoke puff, a streak and a shadow blob into <c>Assets/Textures/</c> and
    /// builds the materials around them.
    /// </summary>
    [CreateAssetMenu(menuName = "PoDecath/VFX Bank", fileName = "VfxBank")]
    public class VfxBank : ScriptableObject
    {
        [Header("Ground contact")]
        [Tooltip("Footfall puffs and the dust a fall throws up. Alpha blended, lit by nothing.")]
        public Material dust;
        [Tooltip("Sand out of the pit: heavier, warmer, and it falls back down.")]
        public Material sand;

        [Header("Impacts")]
        [Tooltip("Additive streaks off a hurdle frame scraping the deck.")]
        public Material spark;
        [Tooltip("Starter's pistol smoke, and the haze a fall leaves hanging.")]
        public Material smoke;
        [Tooltip("Finish-line confetti. Untextured quads; the colour comes from the particle system.")]
        public Material confetti;

        [Header("Athletes")]
        [Tooltip("The ribbon behind a trailed athlete, tinted per lane by the trail component.")]
        public Material trail;
        [Tooltip("The soft dark disc under an athlete on the mobile tier, where there are no real shadows.")]
        public Material blobShadow;
        [Tooltip("Additive glow on a joint that is pulling hard. Must pass vertex colour through, because "
               + "StressSkeleton draws every joint of one athlete in a single mesh and tints them there.")]
        public Material stress;

        [Header("Decals")]
        [Tooltip("The mark left in the sand where a jumper landed. Drawn as a ground-hugging quad.")]
        public Material sandMark;
        [Tooltip("The black streak a foot leaves when it slides on the deck instead of gripping.")]
        public Material skid;

        [Header("Scenery")]
        [Tooltip("The billboard crowd round the deck (PoDecath/CrowdBillboard). Vertex colour is the shirt.")]
        public Material crowd;
        [Tooltip("Cut-out tree sprites for the treeline round the grounds.")]
        public Material treeline;
        [Tooltip("Pennants on the rope above the rail (PoDecath/Pennant).")]
        public Material pennant;
        [Tooltip("The finish tape: two-sided, vertex coloured, alpha blended so it can fade.")]
        public Material tape;
        [Tooltip("Heat shimmer curtains (PoDecath/HeatHaze). PC tier only.")]
        public Material haze;

        [Header("Featured athlete")]
        [Tooltip("Additive fresnel rim on whoever the gallery is on (PoDecath/AthleteRim).")]
        public Material rim;
        [Tooltip("Sweat droplets off a tired athlete's footfalls.")]
        public Material sweat;

        public bool IsUsable => dust != null;

        /// <summary>
        /// Whether this bank has everything the current code expects, rather than merely enough to run.
        ///
        /// The bakery used to re-bake only when the asset was missing entirely, which is correct right up
        /// to the moment a new material is added to this class: every existing project then keeps a bank
        /// that loads fine, passes <see cref="IsUsable"/>, and silently has a null in the new field — so
        /// the feature that needed it never appears and nothing says why. Adding a field here means adding
        /// it to this check.
        /// </summary>
        public bool IsComplete =>
            dust != null && sand != null && smoke != null && spark != null && confetti != null
            && trail != null && blobShadow != null && stress != null && sandMark != null && skid != null
            && crowd != null && treeline != null && pennant != null && tape != null && haze != null
            && rim != null && sweat != null;
    }
}
