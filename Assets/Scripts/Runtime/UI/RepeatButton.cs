using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PoDecath.UI
{
    /// <summary>
    /// A button that keeps firing while it is held down. Sits alongside the normal Button for its visuals
    /// and interactable state, but drives the action itself, so whoever subscribes must not also wire
    /// Button.onClick or every press would count twice.
    ///
    /// The race setup counters run 0..16; stepping that far one tap at a time is not worth asking for.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class RepeatButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        [Tooltip("Pause after the first press before the repeat starts.")]
        public float firstDelay = 0.4f;
        [Tooltip("Gap between repeats while held.")]
        public float repeatInterval = 0.08f;

        /// <summary>Fires once on press, then repeatedly while the button stays held.</summary>
        public event Action Pressed;

        Button _button;
        bool _held;
        float _nextRepeat;

        void Awake() => _button = GetComponent<Button>();

        bool Usable => _button == null || _button.interactable;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!Usable) return;
            _held = true;
            _nextRepeat = Time.unscaledTime + firstDelay;
            Pressed?.Invoke();
        }

        public void OnPointerUp(PointerEventData eventData) => _held = false;
        public void OnPointerExit(PointerEventData eventData) => _held = false;
        void OnDisable() => _held = false;

        void Update()
        {
            if (!_held) return;
            if (!Usable) { _held = false; return; }   // ran into the cap or the floor
            if (Time.unscaledTime < _nextRepeat) return;
            _nextRepeat = Time.unscaledTime + repeatInterval;
            Pressed?.Invoke();
        }
    }
}
