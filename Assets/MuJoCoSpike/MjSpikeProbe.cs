using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PoDecath.Spike
{
    /// <summary>
    /// Reports the MuJoCo-in-Unity spike numbers to the device log (adb logcat).
    ///
    /// It answers two questions on real hardware: is MuJoCo actually integrating the athlete (the
    /// pelvis moves, and the body ends up on the deck when nothing drives it), and what does a frame
    /// cost while it does.
    ///
    /// It runs one staged sweep rather than needing a rebuild per configuration: 1 and 16 athletes,
    /// each at the 50 Hz control rate and at the 200 Hz rate the MJCF actually asks for. The 200 Hz
    /// case is the one that matters for training parity, and it is four times the physics work.
    /// </summary>
    public class MjSpikeProbe : MonoBehaviour
    {
        [Tooltip("Athlete root GameObject to watch. Empty = auto-detect from the first MjBody found.")]
        public string athleteRoot = "";

        [Tooltip("Clone count for the multi-athlete stages.")]
        public int multiCount = 16;

        [Tooltip("Seconds to measure per stage, after a settle period.")]
        public float measureSeconds = 6f;

        [Tooltip("Seconds to let the frame time settle before measuring.")]
        public float settleSeconds = 1.5f;

        [Tooltip("Spacing between athletes, metres.")]
        public float cloneSpacing = 2.5f;

        readonly List<GameObject> _clones = new List<GameObject>();
        Transform _watched;
        GameObject _root;

        void Start()
        {
            _root = FindAthleteRoot();
            if (_root == null)
            {
                Debug.LogError("[MjSpike] no athlete root found (looked for an MjBody)");
                return;
            }
            var pelvis = _root.transform.Find("pelvis");
            _watched = pelvis != null ? pelvis : _root.transform;

            // -1 is NOT "uncapped" on Android -- it means "platform default", which the device resolves
            // to 30 FPS. That pinned every stage of the first device sweep at exactly 33.3 ms and hid
            // whatever MuJoCo actually cost. Ask for a rate above the display's so the number measured
            // is the machine's, not the cap's.
            Application.targetFrameRate = 120;
            QualitySettings.vSyncCount = 0;

            Debug.Log($"[MjSpike] start | athlete={_root.name} | unity={Application.unityVersion} "
                      + $"| device={SystemInfo.deviceModel} | cpu={SystemInfo.processorType} "
                      + $"| cores={SystemInfo.processorCount} | gpu={SystemInfo.graphicsDeviceName} "
                      + $"| screen={Screen.width}x{Screen.height}");
            StartCoroutine(Sweep());
        }

        /// <summary>
        /// Finds the imported model's root whatever the importer happened to name it (it numbers the
        /// node, so the name changes on every re-import). The MjScene object owns the model; the bodies
        /// live under the athlete root, so the first MjBody's topmost ancestor that is not the scene
        /// itself is the model root.
        /// </summary>
        GameObject FindAthleteRoot()
        {
            if (!string.IsNullOrEmpty(athleteRoot))
            {
                var byName = GameObject.Find(athleteRoot);
                if (byName != null) return byName;
            }
            var body = UnityEngine.Object.FindAnyObjectByType<Mujoco.MjBody>();
            if (body == null) return null;
            var t = body.transform;
            while (t.parent != null && t.parent.GetComponent<Mujoco.MjScene>() == null) t = t.parent;
            return t.gameObject;
        }

        IEnumerator MeasureStage(string label, int athletes, float fixedDt)
        {
            Time.fixedDeltaTime = fixedDt;
            yield return new WaitForSeconds(settleSeconds);

            var times = new List<float>();
            var startPos = _watched.position;
            float until = Time.unscaledTime + measureSeconds;
            while (Time.unscaledTime < until)
            {
                times.Add(Time.unscaledDeltaTime);
                yield return null;
            }

            float sum = 0f;
            for (int i = 0; i < times.Count; i++) sum += times[i];
            float avgMs = times.Count > 0 ? sum / times.Count * 1000f : 0f;
            float best = 999f;
            for (int i = 0; i < times.Count; i++)
                if (times[i] * 1000f < best) best = times[i] * 1000f;

            Debug.Log($"[MjSpike] STAGE {label} | athletes={athletes} | fixedDt={fixedDt:F4} "
                      + $"| avgFrameMs={avgMs:F2} | bestFrameMs={best:F2} | fps={1000f / Mathf.Max(0.0001f, avgMs):F1} "
                      + $"| frames={times.Count} | pelvisY={_watched.position.y:F3} "
                      + $"| pelvisMoved={Vector3.Distance(_watched.position, startPos):F3}m");
        }

        IEnumerator Sweep()
        {
            // Stage 1: the imported athlete alone, at the 50 Hz default.
            yield return MeasureStage("single-50hz", 1, 0.02f);
            // Stage 2: same, at the 200 Hz rate training uses. Four times the physics work.
            yield return MeasureStage("single-200hz", 1, 0.005f);

            SpawnClones(multiCount - 1);
            yield return new WaitForSeconds(1.5f);

            yield return MeasureStage($"{multiCount}-athletes-50hz", multiCount, 0.02f);
            yield return MeasureStage($"{multiCount}-athletes-200hz", multiCount, 0.005f);

            Debug.Log("[MjSpike] SWEEP COMPLETE");
        }

        void SpawnClones(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var c = Instantiate(_root);
                c.name = $"{athleteRoot}_clone{i + 1}";
                c.transform.position = new Vector3(cloneSpacing * (i + 1), 0f, 0f);
                _clones.Add(c);
            }
            Debug.Log($"[MjSpike] spawned {count} clones ({count + 1} athletes total)");
        }
    }
}
