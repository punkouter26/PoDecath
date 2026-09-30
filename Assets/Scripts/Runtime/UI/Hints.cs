using UnityEngine;
using UnityEngine.UIElements;

namespace PoDecath.UI
{
    /// <summary>
    /// One-time hints for the taps nothing on screen explains: a face on the menu adds a runner and its "i"
    /// shows the record, a name in the running order locks the cameras on, a tap on the card hands them
    /// back, the frame-rate chip opens the live stats.
    ///
    /// Each is shown once per device, ever (PlayerPrefs), as a small bubble next to the thing it is about,
    /// and goes away on a tap or after a few seconds. One at a time: a hint that finds another up waits for
    /// the next chance rather than stacking. Never during the demo loop, which nobody is holding.
    /// </summary>
    public static class Hints
    {
        /// <summary>How long a hint stays up untouched (real seconds; the UI captures lengthen it to photograph one).</summary>
        public static float Seconds = 6f;
        const string Prefix = "podecath.hint.";

        static VisualElement _current;

        /// <summary>Whether <paramref name="key"/> has been shown on this device.</summary>
        public static bool Seen(string key) => PlayerPrefs.GetInt(Prefix + key, 0) == 1;

        /// <summary>Forgets every hint, so they show again. For testing; nothing in the game calls it.</summary>
        public static void ResetAll()
        {
            foreach (string k in new[] { "tiles", "follow", "unpin", "fps" }) PlayerPrefs.DeleteKey(Prefix + k);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Shows <paramref name="text"/> beside <paramref name="target"/> (under it, or over it when
        /// <paramref name="above"/>) if this hint has never been shown. True when it went up.
        /// </summary>
        public static bool ShowOnce(string key, VisualElement target, string text, bool above = false)
        {
            if (Sim.DemoMode.Active || Seen(key)) return false;
            if (_current != null && _current.panel != null) return false;
            if (target == null || target.panel == null) return false;
            Rect t = target.worldBound;
            if (float.IsNaN(t.width) || t.width < 1f) return false;

            // Into the target's screen: the element its UXML names "root", so a screen that is put away (the
            // race overlay when the results card comes up) takes its hint with it. The stylesheet the bubble is
            // styled by is attached above that. Failing a "root", the document's own root.
            VisualElement host = null;
            for (VisualElement e = target; e != null; e = e.parent) if (e.name == "root") host = e;
            if (host == null)
            {
                host = target;
                while (host.parent != null && host.parent.parent != null) host = host.parent;
            }
            VisualElement screen = target.panel.visualTree;

            var bubble = new Label(text);
            bubble.AddToClassList("hint-bubble");
            bubble.pickingMode = PickingMode.Position;
            host.Add(bubble);
            Rect h = host.worldBound;
            float width = Mathf.Min(760f, screen.worldBound.width - 64f);
            float left = Mathf.Clamp(t.center.x - width * 0.5f, 32f, screen.worldBound.width - 32f - width);
            bubble.style.position = Position.Absolute;
            bubble.style.width = width;
            bubble.style.left = left - h.xMin;
            if (above) bubble.style.bottom = h.yMax - (t.yMin - 12f);
            else bubble.style.top = t.yMax + 12f - h.yMin;

            _current = bubble;
            bubble.RegisterCallback<PointerDownEvent>(_ => Dismiss(bubble));
            bubble.schedule.Execute(() => Dismiss(bubble)).StartingIn((long)(Seconds * 1000));
            PlayerPrefs.SetInt(Prefix + key, 1);
            PlayerPrefs.Save();
            return true;
        }

        static void Dismiss(VisualElement bubble)
        {
            bubble.RemoveFromHierarchy();
            if (_current == bubble) _current = null;
        }
    }
}
