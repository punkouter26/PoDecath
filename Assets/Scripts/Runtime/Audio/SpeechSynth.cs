using System;
using UnityEngine;

namespace PoDecath.Audio
{
    /// <summary>
    /// Something that can say a line out loud. One small interface so the commentary never has to know
    /// what is speaking for it, and so a better voice can be dropped in later without touching a word of
    /// what gets said.
    /// </summary>
    public interface ISpeechVoice
    {
        /// <summary>False when this voice could not start on this platform. Nothing calls a dead voice.</summary>
        bool Available { get; }

        /// <summary>
        /// Says one line, interrupting anything already being said. Commentary is not a queue: by the time
        /// a line has waited its turn the thing it describes is two incidents ago.
        /// </summary>
        void Speak(string text, float rate);

        void Stop();
        void Dispose();
    }

    /// <summary>
    /// Picks the best voice this build has and hands it to whoever wants to talk.
    ///
    /// The honest state of this, platform by platform:
    ///
    /// - **Android** gets a real voice, through the platform's own <c>TextToSpeech</c> over
    ///   <c>com.unity.modules.androidjni</c>, which this project already depends on. Nothing to install,
    ///   no licence, no extra megabytes in the APK.
    /// - **Windows** (editor and standalone) gets a real voice through the system speech synthesiser,
    ///   driven out of process. It is not elegant — it shells out per line — but a line every few seconds
    ///   is well inside what that costs, and it means the owner hears the commentary on the machine the
    ///   game is actually developed on rather than only on a phone.
    /// - **Everything else** gets no voice, and the subtitles carry the commentary on their own. That is
    ///   a real limitation, not a temporary one: it needs a bundled synthesiser to fix.
    ///
    /// The subtitle is not a fallback for the voice. It is always drawn, on every platform, because a
    /// broadcast caption is useful with the sound off and because it is the only part of this feature that
    /// can be checked in a screenshot.
    /// </summary>
    [DefaultExecutionOrder(118)]
    public class SpeechSynth : MonoBehaviour
    {
        [Tooltip("Speaking rate. 1 is the platform's normal pace; a race caller is quicker than that.")]
        [Range(0.5f, 2f)] public float rate = 1.25f;

        [Tooltip("Off means subtitles only, on every platform. Persisted.")]
        public bool startEnabled = true;

        const string Key = "podecath.commentary.voice";

        ISpeechVoice _voice;

        /// <summary>Whether a voice actually started. False on platforms with none, and when muted.</summary>
        public bool HasVoice => _voice != null && _voice.Available && Enabled;

        /// <summary>Which voice is speaking, for the diagnostics panel.</summary>
        public string VoiceName { get; private set; } = "none (subtitles only)";

        /// <summary>Muting the voice without touching the subtitles. Persisted across launches.</summary>
        public bool Enabled
        {
            get => PlayerPrefs.GetInt(Key, startEnabled ? 1 : 0) != 0;
            set
            {
                PlayerPrefs.SetInt(Key, value ? 1 : 0);
                if (!value) _voice?.Stop();
            }
        }

        void Awake()
        {
            _voice = Build(out string name);
            VoiceName = name;
        }

        void OnDestroy()
        {
            _voice?.Dispose();
            _voice = null;
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) _voice?.Stop();
        }

        static ISpeechVoice Build(out string name)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var android = new AndroidVoice();
            if (android.Available) { name = "Android TextToSpeech"; return android; }
            android.Dispose();
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            var windows = new WindowsVoice();
            if (windows.Available) { name = "Windows System.Speech"; return windows; }
            windows.Dispose();
#endif
            name = "none (subtitles only)";
            return null;
        }

        /// <summary>Says a line, if there is anything to say it with. Silent and harmless if there is not.</summary>
        public void Say(string text)
        {
            if (string.IsNullOrEmpty(text) || !HasVoice) return;
            try { _voice.Speak(text, rate); }
            catch (Exception e) { Debug.LogWarning($"[SpeechSynth] {VoiceName} failed: {e.Message}"); }
        }

        public void Silence() => _voice?.Stop();

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>
        /// The platform synthesiser. Built against the API-21 <c>speak</c> overload
        /// (<c>CharSequence, int, Bundle, String</c>); the older three-argument one was removed long before
        /// any device this would ship to.
        /// </summary>
        class AndroidVoice : ISpeechVoice
        {
            AndroidJavaObject _tts;
            bool _ready;

            class InitListener : AndroidJavaProxy
            {
                readonly AndroidVoice _owner;
                public InitListener(AndroidVoice owner) : base("android.speech.tts.TextToSpeech$OnInitListener")
                    => _owner = owner;

                // TextToSpeech.SUCCESS is 0.
                void onInit(int status) => _owner._ready = status == 0;
            }

            public AndroidVoice()
            {
                try
                {
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new InitListener(this));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SpeechSynth] no Android TextToSpeech: {e.Message}");
                    _tts = null;
                }
            }

            // Initialisation is asynchronous and takes a second or two, so this is false until the engine
            // has called back. Lines spoken before then are simply dropped rather than queued: by the time
            // the voice is up, the gun has already gone.
            public bool Available => _tts != null && _ready;

            public void Speak(string text, float rate)
            {
                if (!Available) return;
                _tts.Call<int>("setSpeechRate", rate);
                // QUEUE_FLUSH is 0. The Bundle is cast rather than passed as a bare null so JNI can pick
                // the overload; an untyped null is ambiguous against the deprecated HashMap signature.
                _tts.Call<int>("speak", text, 0, (AndroidJavaObject)null, "podecath");
            }

            public void Stop()
            {
                if (_tts != null && _ready) _tts.Call<int>("stop");
            }

            public void Dispose()
            {
                try { _tts?.Call("shutdown"); } catch { }
                _tts?.Dispose();
                _tts = null;
            }
        }
#endif

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        /// <summary>
        /// The Windows system voice, driven out of process.
        ///
        /// Unity's scripting runtime does not ship <c>System.Speech</c>, so it cannot be referenced
        /// directly; PowerShell can load it from the desktop framework, which is on every Windows install
        /// this project would run on. One process per line, killed when the next line pre-empts it.
        ///
        /// This is not how a shipping game should talk. It is how the machine the game is built on can
        /// talk today, with nothing to install and no licence to read, and it is worth having because
        /// commentary that can only be heard on a phone cannot be tuned.
        /// </summary>
        class WindowsVoice : ISpeechVoice
        {
            System.Diagnostics.Process _speaking;

            public bool Available { get; private set; } = true;

            public void Speak(string text, float rate)
            {
                Stop();
                // System.Speech's rate is -10..10, not a multiplier. 1.25x maps to roughly +2.
                int wpm = Mathf.Clamp(Mathf.RoundToInt((rate - 1f) * 8f), -10, 10);
                string safe = text.Replace("'", "''");
                string script =
                    "Add-Type -AssemblyName System.Speech; " +
                    "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                    $"$s.Rate = {wpm}; $s.Speak('{safe}')";

                var info = new System.Diagnostics.ProcessStartInfo("powershell.exe")
                {
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                try { _speaking = System.Diagnostics.Process.Start(info); }
                catch (Exception e)
                {
                    // A machine with PowerShell locked down is a machine with no voice, not a machine that
                    // should keep trying once a line.
                    Available = false;
                    Debug.LogWarning($"[SpeechSynth] Windows voice disabled: {e.Message}");
                }
            }

            public void Stop()
            {
                try
                {
                    if (_speaking != null && !_speaking.HasExited) _speaking.Kill();
                }
                catch { }
                _speaking = null;
            }

            public void Dispose() => Stop();
        }
#endif
    }
}
