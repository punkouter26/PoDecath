using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// Race setup menu: one counter row per RL athlete definition, minus/plus, capped at
    /// <see cref="RaceRoster.MaxRunners"/> in total and at least one runner overall.
    ///
    /// The grid order interleaves the types round-robin rather than listing each block in turn. The
    /// starting grid is staggered two abreast (the deck is far too narrow to line a full field up in one
    /// row), so a block layout would drop one whole policy into the back rows and make it look beaten
    /// from the gun even though every runner covers the same lap. Interleaving spreads both policies
    /// evenly down the grid; the times in the results modal are the comparison that counts either way.
    /// </summary>
    public class RaceSetupController : MonoBehaviour
    {
        /// <summary>One event the menu can send the field to. The first entry is the default.</summary>
        [Serializable]
        public class EventChoice
        {
            public string label;
            public string sceneName;
            [Tooltip("One line under the title explaining what the field is about to do.")]
            public string hint;
            public Button button;
        }

        [Serializable]
        public class RunnerRow
        {
            public AthleteDefinition definition;
            public Button minusButton;
            public Button plusButton;
            public Text countText;
            public Text nameText;
            [NonSerialized] public int count;
        }

        public List<RunnerRow> rows = new List<RunnerRow>();
        [Tooltip("Events on offer; empty means the single race scene below.")]
        public List<EventChoice> events = new List<EventChoice>();
        public Color selectedEventColor = new Color(0.18f, 0.75f, 0.55f, 1f);
        public Color unselectedEventColor = new Color(0.16f, 0.18f, 0.24f, 0.95f);
        public Text totalText;
        public Text hintText;
        public Button startButton;
        public string raceSceneName = "RooftopRace";
        int _event;

        int Max => RaceRoster.MaxRunners;

        void Start()
        {
            SessionSettings.Load();
            SessionSettings.VisitedMenu = true;
            Time.timeScale = 1f;
            Application.targetFrameRate = 60;

            // Default to an even split of the field across whatever definitions this build has.
            int perRow = rows.Count > 0 ? Max / rows.Count : 0;
            for (int i = 0; i < rows.Count; i++)
            {
                RunnerRow row = rows[i];
                row.count = perRow;
                if (i == 0) row.count += Max - perRow * rows.Count;   // remainder to the first type
                if (row.nameText != null && row.definition != null) row.nameText.text = row.definition.displayName;

                RunnerRow captured = row;
                Wire(row.minusButton, () => Adjust(captured, -1));
                Wire(row.plusButton, () => Adjust(captured, +1));
            }
            if (startButton != null) startButton.onClick.AddListener(StartRace);
            for (int i = 0; i < events.Count; i++)
            {
                int captured = i;
                if (events[i].button != null) events[i].button.onClick.AddListener(() => SelectEvent(captured));
            }
            SelectEvent(0);
            Refresh();
        }

        /// <summary>
        /// Hold-to-repeat when the button has a <see cref="RepeatButton"/>, plain clicks otherwise. Only
        /// ever one of the two, or a press would be counted twice.
        /// </summary>
        static void Wire(Button button, Action action)
        {
            if (button == null) return;
            var repeat = button.GetComponent<RepeatButton>();
            if (repeat != null) repeat.Pressed += action;
            else button.onClick.AddListener(() => action());
        }

        /// <summary>Picks which scene the START button loads and re-words the hint for it.</summary>
        void SelectEvent(int index)
        {
            if (events.Count == 0)
            {
                if (hintText != null) hintText.text = $"Pick 1 to {Max} runners. They race one lap of the rooftop track.";
                return;
            }
            _event = Mathf.Clamp(index, 0, events.Count - 1);
            EventChoice chosen = events[_event];
            if (!string.IsNullOrEmpty(chosen.sceneName)) raceSceneName = chosen.sceneName;
            if (hintText != null) hintText.text = string.IsNullOrEmpty(chosen.hint) ? $"Pick 1 to {Max} athletes." : chosen.hint;
            for (int i = 0; i < events.Count; i++)
            {
                Button b = events[i].button;
                if (b == null) continue;
                Image img = b.GetComponent<Image>();
                if (img != null) img.color = i == _event ? selectedEventColor : unselectedEventColor;
            }
        }

        int Total()
        {
            int t = 0;
            foreach (RunnerRow r in rows) t += r.count;
            return t;
        }

        void Adjust(RunnerRow row, int delta)
        {
            int next = row.count + delta;
            if (next < 0) return;
            if (delta > 0 && Total() >= Max) return;
            if (delta < 0 && Total() <= 1) return;   // never leave an empty field
            row.count = next;
            Refresh();
        }

        void Refresh()
        {
            int total = Total();
            foreach (RunnerRow r in rows)
            {
                if (r.countText != null) r.countText.text = r.count.ToString();
                if (r.minusButton != null) r.minusButton.interactable = r.count > 0 && total > 1;
                if (r.plusButton != null) r.plusButton.interactable = total < Max;
            }
            if (totalText != null) totalText.text = $"Total  {total} / {Max}";
            if (startButton != null) startButton.interactable = total >= 1;
        }

        public void StartRace()
        {
            RaceRoster.Set(BuildGridOrder());
            SessionSettings.ApplyQuality();
            Time.timeScale = 1f;
            SceneManager.LoadScene(raceSceneName);
        }

        /// <summary>Round-robin over the types, so each grid row gets a mix rather than one policy per row.</summary>
        List<string> BuildGridOrder()
        {
            var remaining = new List<int>();
            foreach (RunnerRow r in rows) remaining.Add(r.count);

            var order = new List<string>();
            bool placed = true;
            while (placed && order.Count < Max)
            {
                placed = false;
                for (int i = 0; i < rows.Count && order.Count < Max; i++)
                {
                    if (remaining[i] <= 0 || rows[i].definition == null) continue;
                    order.Add(rows[i].definition.displayName);
                    remaining[i]--;
                    placed = true;
                }
            }
            return order;
        }
    }
}
