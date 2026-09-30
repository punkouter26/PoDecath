using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoDecath.UI
{
    /// <summary>
    /// Every change of screen in the game goes through here: START, FIELD, MENU, the season's NEXT and the
    /// demo loop. The scene loads in the background behind a card that names where it is going and fills a
    /// bar, instead of a blocking LoadScene that froze the last frame for the seconds a rooftop scene takes
    /// to come in (and on Android, long enough to risk "app not responding").
    ///
    /// One load at a time. A second request while one is under way is dropped and said so: two taps on START,
    /// or MENU pressed while the demo moves on, used to queue two synchronous loads back to back.
    ///
    /// The card is its own UIDocument on an object that survives the load, drawn in code (no UXML to lose),
    /// on the same PanelSettings as the screen it leaves, at a sorting order above everything else.
    /// </summary>
    public static class SceneLoader
    {
        /// <summary>A load is under way.</summary>
        public static bool Busy { get; private set; }

        /// <summary>Loads <paramref name="scene"/> behind the card, which says <paramref name="label"/>.</summary>
        public static void Load(string scene, string label = null)
        {
            if (string.IsNullOrEmpty(scene)) { Debug.LogError("[SceneLoader] no scene name."); return; }
            if (Busy) { Debug.Log($"[SceneLoader] already loading; '{scene}' ignored."); return; }
            Busy = true;
            Time.timeScale = 1f;   // slow motion or a stepped capture must not outlive the screen it was set on
            var go = new GameObject("SceneLoader");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<SceneLoaderCard>().Begin(scene, label);
        }

        internal static void Done() => Busy = false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Busy = false;   // play mode in the editor without a domain reload
    }

    /// <summary>The card itself and the coroutine that drives the load.</summary>
    public class SceneLoaderCard : MonoBehaviour
    {
        /// <summary>Longest a load may take before the console is told something is wrong.</summary>
        public const float SlowSeconds = 30f;

        VisualElement _fill;
        Label _status;

        public void Begin(string scene, string label)
        {
            PanelSettings panel = null;
            foreach (UIDocument d in FindObjectsByType<UIDocument>())
                if (d.panelSettings != null && d.panelSettings.targetTexture == null) { panel = d.panelSettings; break; }
            if (panel != null) BuildCard(panel, label);
            StartCoroutine(Run(scene));
        }

        void BuildCard(PanelSettings panel, string label)
        {
            gameObject.SetActive(false);   // the document must have its panel before it enables
            var doc = gameObject.AddComponent<UIDocument>();
            doc.panelSettings = panel;
            doc.sortingOrder = 1000;
            gameObject.SetActive(true);

            VisualElement root = doc.rootVisualElement;
            root.style.position = Position.Absolute;
            root.style.left = 0; root.style.right = 0; root.style.top = 0; root.style.bottom = 0;
            root.style.backgroundColor = new Color(0.04f, 0.05f, 0.07f, 1f);
            root.style.alignItems = Align.Center;
            root.style.justifyContent = Justify.Center;

            var small = new Label("LOADING");
            small.style.fontSize = 28; small.style.letterSpacing = 6;
            small.style.color = new Color(0.62f, 0.67f, 0.75f);
            var big = new Label(string.IsNullOrEmpty(label) ? "" : label.ToUpperInvariant());
            big.style.fontSize = 88; big.style.unityFontStyleAndWeight = FontStyle.Bold;
            big.style.color = Color.white; big.style.marginTop = 12; big.style.marginBottom = 40;
            var track = new VisualElement();
            track.style.width = 600; track.style.height = 14;
            track.style.backgroundColor = new Color(1f, 1f, 1f, 0.12f);
            SetRadius(track, 7);
            _fill = new VisualElement();
            _fill.style.width = Length.Percent(4); _fill.style.height = Length.Percent(100);
            _fill.style.backgroundColor = new Color(0.18f, 0.75f, 0.55f);
            SetRadius(_fill, 7);
            track.Add(_fill);
            _status = new Label("");
            _status.style.fontSize = 26; _status.style.marginTop = 24;
            _status.style.color = new Color(0.62f, 0.67f, 0.75f);

            root.Add(small); root.Add(big); root.Add(track); root.Add(_status);
        }

        static void SetRadius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        IEnumerator Run(string scene)
        {
            // Two frames for the card to be drawn before the load starts taking the main thread.
            yield return null;
            yield return null;

            AsyncOperation op = SceneManager.LoadSceneAsync(scene);
            if (op == null)
            {
                Debug.LogError($"[SceneLoader] '{scene}' could not be loaded (not in the build list?).");
                Finish();
                yield break;
            }

            float started = Time.unscaledTime;
            bool warned = false;
            while (!op.isDone)
            {
                // Unity reports 0..0.9 while reading and the last tenth while activating.
                if (_fill != null) _fill.style.width = Length.Percent(Mathf.Lerp(4f, 100f, Mathf.Clamp01(op.progress / 0.9f)));
                if (!warned && Time.unscaledTime - started > SlowSeconds)
                {
                    warned = true;
                    Debug.LogError($"[SceneLoader] '{scene}' has taken over {SlowSeconds:F0} s to load and is still at {op.progress:P0}.");
                    if (_status != null) _status.text = "Still loading...";
                }
                yield return null;
            }

            // One frame of the new scene under the card, so its own screens have laid themselves out.
            if (_fill != null) _fill.style.width = Length.Percent(100);
            yield return null;
            Finish();
        }

        void Finish()
        {
            SceneLoader.Done();
            Destroy(gameObject);
        }

        void OnDestroy() => SceneLoader.Done();
    }
}
