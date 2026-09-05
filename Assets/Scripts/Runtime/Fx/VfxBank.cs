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

        [Header("Decals")]
        [Tooltip("The mark left in the sand where a jumper landed. Drawn as a ground-hugging quad.")]
        public Material sandMark;

        public bool IsUsable => dust != null;
    }
}
