using UnityEngine;
using PoDecath.Env;
using PoDecath.Sim;

namespace PoDecath.Audio
{
    /// <summary>
    /// The crowd, placed around the roof instead of played down the middle.
    ///
    /// The bed used to be one source at <c>spatialBlend = 0</c>, which meant that whichever way the
    /// broadcast camera turned — and it turns constantly, seven positions round a 100 m loop — the crowd
    /// stayed exactly where it was, dead centre, at exactly the same level. That is the single loudest
    /// thing in the mix telling you the camera is not really anywhere.
    ///
    /// Ringing the deck with a handful of 3D sources fixes it for almost nothing: the same clip, started at
    /// different offsets into itself so they do not phase into one voice, spread evenly round the loop at
    /// crowd height below the deck. Cut to the far bend and the crowd swings round behind you.
    ///
    /// The whole ring rides one level, set by <see cref="RaceAudio"/> from what the race is doing, so the
    /// crowd still builds and falls as one body.
    /// </summary>
    [DefaultExecutionOrder(115)]
    public class CrowdRing : MonoBehaviour
    {
        [Header("Wiring")]
        public AudioBank bank;
        [Tooltip("The loop the emitters are placed around. Without it they ring this transform instead.")]
        public TrackPath path;

        [Tooltip("Play the looping crowd bed - the continuous wash of noise under everything. Off by "
               + "owner request (2026-09-13): it reads as white noise/hiss rather than as a crowd. The "
               + "ring itself stays, so cheers, groans and applause still arrive from all round the deck; "
               + "only the constant bed is silent.")]
        public bool playBed = false;

        [Header("Placement")]
        [Tooltip("How far outside the deck edge the crowd stands.")]
        public float outward = 14f;
        [Tooltip("Height relative to the deck. Negative: the crowd is below a rooftop track, looking up.")]
        public float height = -5f;
        [Tooltip("Nothing quieter with distance inside this radius; the ring should not have holes in it.")]
        public float minDistance = 18f;
        public float maxDistance = 130f;

        AudioSource[] _sources;
        float _level;

        /// <summary>0..1, applied across the whole ring. Set by whatever is watching the race.</summary>
        public float Level
        {
            get => _level;
            set => _level = Mathf.Clamp01(value);
        }

        void Start()
        {
            // The ring is still worth building with the bed switched off: Burst() plays the crowd's
            // reactions through these same sources, and those are the part that is actually a crowd.
            if (bank == null || (playBed && bank.crowdBed == null)) { enabled = false; return; }

            int count = Mathf.Max(1, RenderTier.CrowdEmitters);
            _sources = new AudioSource[count];
            float lap = path != null ? path.LapLength : 0f;

            for (int i = 0; i < count; i++)
            {
                var go = new GameObject($"Crowd_{i}");
                go.transform.SetParent(transform, false);
                go.transform.position = Place(i, count, lap);

                var src = go.AddComponent<AudioSource>();
                src.clip = playBed ? bank.crowdBed : null;
                src.loop = playBed;
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = minDistance;
                src.maxDistance = maxDistance;
                src.dopplerLevel = 0f;         // a crowd does not move; only the camera does
                src.spread = 110f;             // a wide body of sound, not a point
                src.priority = 8;              // just behind the reserved slot, well ahead of footfalls
                // Offset into the loop and detuned a little, so N copies of one clip do not sum into one
                // very loud copy of it with a comb filter across the middle.
                src.pitch = 1f + (i - count * 0.5f) * 0.004f;
                // With the bed off the source plays nothing of its own and exists only to carry
                // one-shots. PlayOneShot scales by AudioSource.volume, and Burst() already applies both
                // the crowd bus level and the 1/sqrt(N) ring sum, so this has to sit at 1 or the
                // reactions would be attenuated twice.
                src.volume = playBed ? 0f : 1f;
                if (playBed)
                {
                    src.Play();
                    src.time = bank.crowdBed.length * i / count;
                }
                _sources[i] = src;
            }
        }

        Vector3 Place(int i, int count, float lap)
        {
            float t = (i + 0.5f) / count;
            if (path != null && lap > 0f)
            {
                // Around the loop itself, so the near side of the crowd is always the side you are on.
                Vector3 p = path.Position(t * lap, path.deckWidth * 0.5f + outward);
                return p + Vector3.up * height;
            }
            float angle = t * Mathf.PI * 2f;
            return transform.position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * outward + Vector3.up * height;
        }

        /// <summary>
        /// A reaction across the whole ring: a cheer, a groan, applause. Every emitter fires it at once at
        /// a fraction of the level, so N of them sum to roughly one — which is what makes the reaction
        /// arrive from all round the deck instead of from a point in it.
        ///
        /// The variation between emitters comes from the per-emitter detune each source was given at
        /// start-up, not from a pitch argument: a one-shot inherits the pitch of the source that plays it,
        /// and these sources are in the middle of looping the crowd bed. Retuning one to colour a cheer
        /// would bend the bed underneath it for as long as the cheer lasted.
        /// </summary>
        public void Burst(AudioClip clip, float volume)
        {
            if (_sources == null || clip == null) return;
            // Square root, not a straight division: uncorrelated sources sum in power, not in amplitude.
            float level = Mathf.Clamp01(volume) / Mathf.Sqrt(_sources.Length) * AudioMix.Level(AudioMix.Bus.Crowd);
            for (int i = 0; i < _sources.Length; i++)
                if (_sources[i] != null) _sources[i].PlayOneShot(clip, level);
        }

        void Update()
        {
            // Nothing to ride when the bed is off: the level exists to swell the loop, and writing it
            // to volume would silence the one-shots along with it.
            if (_sources == null || !playBed) return;
            float v = _level * AudioMix.Level(AudioMix.Bus.Crowd);
            for (int i = 0; i < _sources.Length; i++)
                if (_sources[i] != null) _sources[i].volume = v;
        }
    }
}
