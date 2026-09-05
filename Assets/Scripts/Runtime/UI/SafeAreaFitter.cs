using UnityEngine;

namespace PoDecath.UI
{
    /// <summary>Keeps a full-stretch RectTransform inside Screen.safeArea (notches, home indicator).</summary>
    [RequireComponent(typeof(RectTransform))]
    [ExecuteAlways]
    public class SafeAreaFitter : MonoBehaviour
    {
        Rect _lastSafeArea = new Rect(0, 0, 0, 0);
        Vector2Int _lastScreen;
        RectTransform _rt;

        void OnEnable() { _rt = GetComponent<RectTransform>(); Apply(); }
        void Update() { if (_lastSafeArea != Screen.safeArea || _lastScreen.x != Screen.width || _lastScreen.y != Screen.height) Apply(); }

        void Apply()
        {
            if (_rt == null) _rt = GetComponent<RectTransform>();
            Rect sa = Screen.safeArea;
            _lastSafeArea = sa;
            _lastScreen = new Vector2Int(Screen.width, Screen.height);
            if (Screen.width <= 0 || Screen.height <= 0) return;

            // Device Simulator and some editor views report a safe area for a different resolution
            // than Screen.width/height; a safe area that does not fit the screen is meaningless.
            if (sa.width <= 0f || sa.height <= 0f || sa.xMax > Screen.width + 1f || sa.yMax > Screen.height + 1f)
                sa = new Rect(0f, 0f, Screen.width, Screen.height);

            Vector2 min = sa.position;
            Vector2 max = sa.position + sa.size;
            min.x = Mathf.Clamp01(min.x / Screen.width); min.y = Mathf.Clamp01(min.y / Screen.height);
            max.x = Mathf.Clamp01(max.x / Screen.width); max.y = Mathf.Clamp01(max.y / Screen.height);
            _rt.anchorMin = min;
            _rt.anchorMax = max;
            _rt.offsetMin = Vector2.zero;
            _rt.offsetMax = Vector2.zero;
        }
    }
}
