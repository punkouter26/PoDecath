using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using PoDecath.Sim;

namespace PoDecath.Fx
{
    /// <summary>
    /// Keeps the last few seconds of the broadcast in memory and, when a race is decided, saves them as a
    /// looping GIF the viewer can share: the finish of a lap race, or the final landing of the long jump.
    ///
    /// What it records is the screen as the viewer saw it, overlay and all: the running order sliding, the
    /// lower third, the caption. That is what makes it a highlight rather than a camera test.
    ///
    /// Cost is kept flat and small. One grab every 1/<see cref="fps"/> s: a copy of the back buffer, a
    /// downscale to <see cref="longSide"/> px on the GPU, and an asynchronous readback, so the CPU never waits
    /// on the GPU. Each frame is quantised to 8-bit palette indices as it arrives (about 80 kB at 480 px
    /// tall), into a ring allocated once, and the encode runs on a worker thread after the race. Nothing on
    /// the per-frame path allocates.
    ///
    /// Clips land in <c>persistentDataPath/clips/</c>; the newest <see cref="keep"/> are kept.
    /// </summary>
    [DefaultExecutionOrder(140)]
    public class HighlightClip : MonoBehaviour
    {
        public RaceEvent race;
        [Tooltip("Frames per second in the clip. 12 reads as motion and keeps a 6 s clip under 2 MB.")]
        [Range(6, 20)] public int fps = 12;
        [Tooltip("Seconds of race kept in the clip.")]
        [Range(2f, 10f)] public float seconds = 6f;
        [Tooltip("Seconds recorded after the first runner crosses the line, so the clip ends on the finish "
               + "rather than on the lunge.")]
        public float tailSeconds = 1.5f;
        [Tooltip("Pixels along the clip's long side. The short side follows the screen's shape.")]
        public int longSide = 480;
        [Tooltip("Clips kept on the device; older ones are deleted.")]
        public int keep = 20;

        /// <summary>Where the last clip was written, or empty.</summary>
        public string LastClip { get; private set; } = "";
        /// <summary>One line for the results card: saving, saved where, or why not.</summary>
        public string Status { get; private set; } = "";
        public event Action<string> Saved;

        public static string Folder => Path.Combine(Application.persistentDataPath, "clips");

        RenderTexture _screen, _small;
        byte[][] _ring;
        int _head, _count, _w, _h;
        float _nextGrab;
        bool _recording, _triggered;
        float _stopAt = -1f;
        int _lastAttempt = -1;
        int _pending;
        Task<string> _encoding;
        bool _supported;

        void Awake()
        {
            _supported = SystemInfo.supportsAsyncGPUReadback;
            if (!_supported) Status = "Clips need GPU readback, which this device does not have.";
        }

        void OnEnable()
        {
            if (race != null) race.RaceComplete += OnComplete;
            if (_supported) StartCoroutine(Grabber());
        }

        void OnDisable()
        {
            if (race != null) race.RaceComplete -= OnComplete;
            StopAllCoroutines();
        }

        void OnDestroy()
        {
            if (_screen != null) _screen.Release();
            if (_small != null) _small.Release();
        }

        void Update()
        {
            if (!_supported || race == null) return;

            // A new attempt on the grid starts a fresh recording.
            if (race.Current == RaceEvent.Phase.Countdown && race.Attempt != _lastAttempt)
            {
                _lastAttempt = race.Attempt;
                _recording = true;
                _triggered = false;
                _stopAt = -1f;
                _count = 0;
                _head = 0;
            }

            // A lap race is decided the moment somebody crosses the line; the tail keeps the picture running
            // through the finish itself. The long jump has no line, so it waits for the event's own end.
            if (_recording && !_triggered && race.Current == RaceEvent.Phase.Running && !(race is LongJumpEvent))
            {
                foreach (RaceEvent.Athlete a in race.Athletes)
                {
                    if (a == null || !a.finished) continue;
                    _triggered = true;
                    _stopAt = Time.unscaledTime + tailSeconds;
                    break;
                }
            }

            if (_stopAt > 0f && Time.unscaledTime >= _stopAt && _pending == 0) Finish();

            if (_encoding != null && _encoding.IsCompleted)
            {
                string path = _encoding.IsFaulted ? null : _encoding.Result;
                _encoding = null;
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Clip could not be saved.";
                }
                else
                {
                    LastClip = path;
                    Status = $"Clip saved: {Path.GetFileName(path)}";
                    Debug.Log($"[HighlightClip] {path}");
                    Saved?.Invoke(path);
                }
            }
        }

        void OnComplete(List<RaceEvent.RaceResult> results)
        {
            // The jump's last landing, or a lap race whose tail has not run out yet: stop here, before the
            // results card comes up over the picture.
            if (_recording && (!_triggered || _stopAt > Time.unscaledTime)) _stopAt = Time.unscaledTime;
            _triggered = true;
        }

        // ---------------------------------------------------------------- capture

        IEnumerator Grabber()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (true)
            {
                yield return endOfFrame;
                // Not while the last clip is still being written: the encoder reads the ring directly.
                if (!_recording || _encoding != null) continue;
                if (_ring == null && !Allocate()) continue;
                if (Time.unscaledTime < _nextGrab) continue;
                _nextGrab = Time.unscaledTime + 1f / fps;
                Grab();
            }
        }

        bool Allocate()
        {
            int sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
            float scale = longSide / (float)Mathf.Max(sw, sh);
            // Even sizes: some GPUs are fussy about odd readback widths, and nobody will miss the pixel.
            _w = Mathf.Max(2, Mathf.RoundToInt(sw * scale) & ~1);
            _h = Mathf.Max(2, Mathf.RoundToInt(sh * scale) & ~1);
            _screen = new RenderTexture(sw, sh, 0, RenderTextureFormat.ARGB32) { name = "HighlightScreen" };
            _small = new RenderTexture(_w, _h, 0, RenderTextureFormat.ARGB32) { name = "HighlightSmall" };
            _screen.Create();
            _small.Create();
            int frames = Mathf.CeilToInt(seconds * fps);
            _ring = new byte[frames][];
            for (int i = 0; i < frames; i++) _ring[i] = new byte[_w * _h];
            return true;
        }

        void Grab()
        {
            // The screen changed shape (rotation, a resized editor window): start the ring again at the new size.
            if (_screen.width != Screen.width || _screen.height != Screen.height)
            {
                _screen.Release(); _small.Release();
                _ring = null;
                _count = 0; _head = 0;
                if (!Allocate()) return;
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_screen);
            // On APIs whose texture origin is the top (D3D, Metal, Vulkan) the capture arrives upside down;
            // flip it in the same blit that shrinks it.
            if (SystemInfo.graphicsUVStartsAtTop) Graphics.Blit(_screen, _small, new Vector2(1f, -1f), new Vector2(0f, 1f));
            else Graphics.Blit(_screen, _small);

            int slot = _head;
            _head = (_head + 1) % _ring.Length;
            _count = Mathf.Min(_count + 1, _ring.Length);
            _pending++;
            AsyncGPUReadback.Request(_small, 0, TextureFormat.RGBA32, req =>
            {
                _pending--;
                if (req.hasError || _ring == null || slot >= _ring.Length) return;
                GifWriter.Quantise(req.GetData<byte>(), _w, _h, _ring[slot], bottomUp: true);
            });
        }

        void Finish()
        {
            _stopAt = -1f;
            _recording = false;
            if (_count < 2 || _encoding != null) return;

            // Oldest first. The ring is not written again until the next attempt starts, so the encoder can
            // read these arrays directly without a copy.
            var frames = new List<byte[]>(_count);
            int start = (_head - _count + _ring.Length) % _ring.Length;
            for (int i = 0; i < _count; i++) frames.Add(_ring[(start + i) % _ring.Length]);

            string evt = ScoringTable.Label(ScoringTable.Classify(race)).Replace(" ", "").ToLowerInvariant();
            string folder = Folder;
            string path = Path.Combine(folder, $"podecath_{evt}_{DateTime.Now:yyyyMMdd_HHmmss}.gif");
            int w = _w, h = _h, delay = Mathf.RoundToInt(100f / fps), keepN = keep;
            Status = "Saving clip...";
            _encoding = Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    GifWriter.Encode(fs, w, h, frames, delay);
                Prune(folder, keepN);
                return path;
            });
        }

        static void Prune(string folder, int keepN)
        {
            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(folder).GetFiles("podecath_*.gif"));
                files.Sort((a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
                for (int i = keepN; i < files.Count; i++) files[i].Delete();
            }
            catch { /* a clip that cannot be pruned is not worth failing the save over */ }
        }
    }
}
