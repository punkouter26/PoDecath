using System;
using UnityEngine;

namespace PoDecath.Audio
{
    /// <summary>
    /// The mix: four buses, a master, and the ducking between them.
    ///
    /// This is a code mixer rather than an <c>AudioMixer</c> asset, deliberately. Creating a mixer asset
    /// from a script needs <c>UnityEditor.Audio.AudioMixerController</c>, which is editor-internal, and the
    /// project's rule is that its assets are generated and reproducible rather than hand-authored. What a
    /// mixer would have bought us here is buses, ducking and a per-bus filter — the first two are below, and
    /// the third is done where it actually belongs, on the sources that need it
    /// (<see cref="ListenerAcoustics"/> puts the low pass and the reverb on the listener instead).
    ///
    /// Every source in the game multiplies its own volume by <see cref="Level"/> for its bus. That means
    /// one number turns the crowd down under a piece of commentary, and one number mutes everything for a
    /// results card, without any source knowing about any other.
    /// </summary>
    public static class AudioMix
    {
        public enum Bus
        {
            /// <summary>The crowd bed and its reactions. The loudest thing, and the first thing ducked.</summary>
            Crowd,
            /// <summary>Gun, bell, clatter, sand, footfalls — anything the event itself makes.</summary>
            Sfx,
            /// <summary>Camera cuts, stings, anything that belongs to the broadcast rather than the stadium.</summary>
            Broadcast,
            /// <summary>Menus and buttons. Never ducked, because a button has to answer.</summary>
            Ui,
        }

        const string KeyMaster = "podecath.vol.master";
        const string KeyCrowd = "podecath.vol.crowd";
        const string KeySfx = "podecath.vol.sfx";

        static readonly float[] _bus = { 1f, 1f, 1f, 1f };
        static readonly float[] _duck = { 1f, 1f, 1f, 1f };
        static readonly float[] _duckTarget = { 1f, 1f, 1f, 1f };
        static float _master = 1f;
        static bool _loaded;

        /// <summary>Raised when a level changes, so anything holding a cached volume can re-read it.</summary>
        public static event Action Changed;

        public static float Master
        {
            get { Load(); return _master; }
            set { Load(); _master = Mathf.Clamp01(value); Save(); Changed?.Invoke(); }
        }

        /// <summary>The audible level for a bus right now: its own setting, its ducking, and the master.</summary>
        public static float Level(Bus bus)
        {
            Load();
            int i = (int)bus;
            return _bus[i] * _duck[i] * _master;
        }

        public static float Setting(Bus bus)
        {
            Load();
            return _bus[(int)bus];
        }

        public static void Set(Bus bus, float value)
        {
            Load();
            _bus[(int)bus] = Mathf.Clamp01(value);
            Save();
            Changed?.Invoke();
        }

        /// <summary>
        /// Ducks a bus to <paramref name="to"/> of its normal level. Nothing here is instant — the recovery
        /// is eased in <see cref="Tick"/> — because a crowd that snaps back to full the frame a sting ends
        /// sounds like a fault rather than a mix.
        /// </summary>
        public static void Duck(Bus bus, float to)
        {
            Load();
            int i = (int)bus;
            _duckTarget[i] = Mathf.Clamp01(to);
            _duck[i] = Mathf.Min(_duck[i], _duckTarget[i]);   // ducking is immediate, releasing is not
        }

        /// <summary>Releases a duck; the bus eases back up over the next moment.</summary>
        public static void Release(Bus bus)
        {
            Load();
            _duckTarget[(int)bus] = 1f;
        }

        /// <summary>
        /// Advances the duck envelopes. Called once a frame from whatever owns the mix in the scene —
        /// <see cref="RaceAudio"/> in a race, nothing at all in a menu, where nothing ducks.
        /// </summary>
        public static void Tick(float unscaledDelta, float releasePerSecond = 1.6f)
        {
            Load();
            for (int i = 0; i < _duck.Length; i++)
                _duck[i] = Mathf.MoveTowards(_duck[i], _duckTarget[i], releasePerSecond * unscaledDelta);
        }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _master = PlayerPrefs.GetFloat(KeyMaster, 1f);
            _bus[(int)Bus.Crowd] = PlayerPrefs.GetFloat(KeyCrowd, 1f);
            _bus[(int)Bus.Sfx] = PlayerPrefs.GetFloat(KeySfx, 1f);
        }

        static void Save()
        {
            PlayerPrefs.SetFloat(KeyMaster, _master);
            PlayerPrefs.SetFloat(KeyCrowd, _bus[(int)Bus.Crowd]);
            PlayerPrefs.SetFloat(KeySfx, _bus[(int)Bus.Sfx]);
        }
    }
}
