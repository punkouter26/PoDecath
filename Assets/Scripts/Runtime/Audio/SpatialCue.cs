using UnityEngine;

namespace PoDecath.Audio
{
    /// <summary>
    /// A pool of 3D one-shot sources, so a cue can be fired from where it actually happens.
    ///
    /// The starter's pistol was played on a 2D source, which meant it came from nowhere and stayed at the
    /// same level whether the camera was on the grid or forty metres up over the roof. Everything an
    /// athletics event does has a place — the gun is behind the grid, the bell is at the line, the clatter
    /// is at the hurdle that went over, the thud is in the pit — and a shot that respects that is most of
    /// what makes a mix feel like a stadium rather than a menu.
    ///
    /// Pooled because the alternative, <c>AudioSource.PlayClipAtPoint</c>, creates and destroys a
    /// GameObject per cue, and a field of eight knocking hurdles over does that a lot.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public class SpatialCue : MonoBehaviour
    {
        [Tooltip("How many cues can be in the air at once. Beyond this the oldest source is stolen.")]
        public int voices = 8;
        [Tooltip("Inside this radius a cue is at full level.")]
        public float minDistance = 6f;
        [Tooltip("Beyond this it is inaudible. The broadcast cameras sit 8-40 m out.")]
        public float maxDistance = 140f;

        AudioSource[] _pool;
        int _next;

        static SpatialCue _instance;

        /// <summary>The pool in the current scene, if a race built one.</summary>
        public static SpatialCue Instance => _instance;

        void Awake()
        {
            _instance = this;
            _pool = new AudioSource[Mathf.Max(1, voices)];
            for (int i = 0; i < _pool.Length; i++)
            {
                var go = new GameObject($"Cue_{i}");
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;   // a gunshot outdoors falls off fast and far
                src.minDistance = minDistance;
                src.maxDistance = maxDistance;
                src.dopplerLevel = 0f;
                src.priority = 24;
                _pool[i] = src;
            }
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        /// <summary>
        /// Fires one clip at a world point on the given bus. Returns quietly if there is no pool, so a
        /// scene that never built one is not a special case at every call site.
        /// </summary>
        public static void Play(AudioClip clip, Vector3 at, float volume, AudioMix.Bus bus = AudioMix.Bus.Sfx, float pitch = 1f)
        {
            SpatialCue cue = _instance;
            if (cue == null || clip == null) return;
            cue.Fire(clip, at, volume, bus, pitch);
        }

        void Fire(AudioClip clip, Vector3 at, float volume, AudioMix.Bus bus, float pitch)
        {
            AudioSource src = _pool[_next];
            _next = (_next + 1) % _pool.Length;
            if (src == null) return;

            src.transform.position = at;
            src.pitch = pitch;
            src.PlayOneShot(clip, Mathf.Clamp01(volume) * AudioMix.Level(bus));
        }
    }
}
