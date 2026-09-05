using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using PoDecath.Sim;

namespace PoDecath.UI
{
    /// <summary>
    /// Results card shown once every runner has finished or fallen. Rows are pre-built by the scene
    /// builder (one per possible grid slot) and filled from <see cref="DashEvent.Results"/>, tinted with
    /// each athlete colour so a GREEN reference bot and a custom bot are tellable apart at a glance.
    /// </summary>
    public class RaceResultsModal : MonoBehaviour
    {
        public DashEvent race;
        [Tooltip("Root object toggled on and off; leave the component itself enabled.")]
        public GameObject panel;
        public Text titleText;
        public Text subtitleText;
        public List<Text> rankTexts = new List<Text>();
        public List<Text> nameTexts = new List<Text>();
        public List<Text> timeTexts = new List<Text>();
        public Button againButton;
        public Button changeButton;
        public string setupSceneName = "RaceSetup";
        [Tooltip("Hidden while the results are up. The live HUD reads through the dimmed backdrop otherwise.")]
        public List<GameObject> hideWhileShown = new List<GameObject>();

        void Start()
        {
            if (againButton != null) againButton.onClick.AddListener(RaceAgain);
            if (changeButton != null) changeButton.onClick.AddListener(ChangeRunners);
            if (race != null) race.RaceComplete += Show;
            Hide();
        }

        void OnDestroy()
        {
            if (race != null) race.RaceComplete -= Show;
        }

        public void Show(List<DashEvent.RaceResult> results)
        {
            int rows = Mathf.Min(rankTexts.Count, Mathf.Min(nameTexts.Count, timeTexts.Count));
            for (int i = 0; i < rows; i++)
            {
                bool used = i < results.Count;
                rankTexts[i].transform.parent.gameObject.SetActive(used);
                if (!used) continue;

                DashEvent.RaceResult r = results[i];
                rankTexts[i].text = r.finished ? $"{r.rank}" : "-";
                nameTexts[i].text = r.name;
                timeTexts[i].text = r.Status;

                Color c = r.finished ? r.color : new Color(r.color.r, r.color.g, r.color.b, 0.55f);
                nameTexts[i].color = c;
                rankTexts[i].color = c;
                timeTexts[i].color = r.finished ? Color.white : new Color(1f, 0.55f, 0.45f);
            }

            if (titleText != null) titleText.text = "RESULTS";
            // The event words its own sub-heading: a lap race counts finishers, the long jump
            // counts marks. Both are DashEvent.Results underneath.
            if (subtitleText != null && race != null) subtitleText.text = race.ResultsSubtitle(results);
            SetBackdropVisible(false);
            if (panel != null) panel.SetActive(true);
        }

        public void Hide()
        {
            if (panel != null) panel.SetActive(false);
            SetBackdropVisible(true);
        }

        void SetBackdropVisible(bool visible)
        {
            foreach (GameObject go in hideWhileShown) if (go != null) go.SetActive(visible);
        }

        void RaceAgain()
        {
            Hide();
            if (race != null) race.RestartNow();
        }

        void ChangeRunners()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(setupSceneName);
        }
    }
}
