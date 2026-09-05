using UnityEngine;

namespace PoDecath.Audio
{
    /// <summary>
    /// Every sound the game plays, in one asset, so a scene builder wires one field instead of a dozen.
    ///
    /// The clips it points at are generated, not recorded: the project ships no audio, and
    /// <c>PoDecath/Bake Audio Clips</c> synthesises the whole set into <c>Assets/Audio/</c> from filtered
    /// noise and decaying partials. They are deliberately plain — a crowd bed, a starter's pistol, footfalls,
    /// a bell, a clatter — and any of them can be replaced by dropping a real file on the matching field
    /// here without touching a line of code.
    /// </summary>
    [CreateAssetMenu(menuName = "PoDecath/Audio Bank", fileName = "AudioBank")]
    public class AudioBank : ScriptableObject
    {
        [Header("Crowd")]
        [Tooltip("Seamless loop under the whole event; the race audio rides its volume, not its content.")]
        public AudioClip crowdBed;
        [Tooltip("Cheer. Played on a finisher, a landing in the sand, and the end of the event.")]
        public AudioClip crowdSwell;
        [Tooltip("The other noise a crowd makes: someone has gone down.")]
        public AudioClip crowdGroan;
        [Tooltip("Applause: the settled, rhythmic noise after something is decided, not the roar during it.")]
        public AudioClip crowdApplause;
        [Tooltip("A rhythmic clap the crowd builds when a race is close. Looped and faded in and out.")]
        public AudioClip crowdChant;

        [Header("Ambience")]
        [Tooltip("Wind over the roof. Louder the higher the camera goes, which is what sells the height.")]
        public AudioClip windBed;

        [Header("Event")]
        public AudioClip pistol;
        public AudioClip countBeep;
        [Tooltip("Rung as the leader starts the last lap. Only multi-lap events ever hear it.")]
        public AudioClip lapBell;

        [Header("Contact")]
        [Tooltip("A hurdle knocked clean over: the bar, the frame, the deck.")]
        public AudioClip hurdleClatter;
        [Tooltip("A hurdle clipped but left standing. Far more common than a topple, and a different sound.")]
        public AudioClip hurdleClip;
        public AudioClip sandThud;
        [Tooltip("On the asphalt deck. Picked round-robin per step so a field of sixteen does not sound "
               + "like one runner.")]
        public AudioClip[] footfalls = new AudioClip[0];
        [Tooltip("In the pit. Softer, with grain in it and almost no slap.")]
        public AudioClip[] footfallsSand = new AudioClip[0];
        [Tooltip("On the poured runway and the infield deck. Duller and lower than asphalt.")]
        public AudioClip[] footfallsRubber = new AudioClip[0];
        [Tooltip("One breath cycle, pitched and looped per athlete as it tires.")]
        public AudioClip breath;

        [Header("Broadcast")]
        [Tooltip("Under a camera cut, well down in the mix.")]
        public AudioClip whoosh;
        [Tooltip("The short musical stab under a lower third or a result.")]
        public AudioClip sting;

        /// <summary>What an athlete is running on. Picks which footfall set a step comes from.</summary>
        public enum Surface { Asphalt, Sand, Rubber }

        public bool HasFootfalls => footfalls != null && footfalls.Length > 0;

        public AudioClip Footfall(int i) => Footfall(i, Surface.Asphalt);

        /// <summary>
        /// One footfall from the set for a surface, falling back to the asphalt set if that surface was
        /// never baked — a bank with only the original four clips still works exactly as it did.
        /// </summary>
        public AudioClip Footfall(int i, Surface surface)
        {
            AudioClip[] set = surface switch
            {
                Surface.Sand => footfallsSand,
                Surface.Rubber => footfallsRubber,
                _ => footfalls,
            };
            if (set == null || set.Length == 0) set = footfalls;
            if (set == null || set.Length == 0) return null;
            return set[(i & 0x7fffffff) % set.Length];
        }
    }
}
