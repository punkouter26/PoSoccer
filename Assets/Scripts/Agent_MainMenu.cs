using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Opening menu (UI Toolkit, mobile portrait, safe-area): pick the Blue and
    /// Red players from the four-personality roster, then launch the match scene.
    /// Always 2v2 — the previous 1v1 mode toggle was removed. The whole tree is
    /// built in code so no UXML wiring is needed.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Agent_MainMenu : MonoBehaviour
    {
        [Header("Roster (profile assets)")]
        public Reward_Settings standard;
        public Reward_Settings matt;
        public Reward_Settings kim;
        public Reward_Settings nick;

        [Header("Flow")]
        public string matchScene = "SCN_Exhibition";

        readonly Reward_Settings[] _picks = new Reward_Settings[4];   // B1, B2, R1, R2
        readonly System.Collections.Generic.List<Button>[] _slotButtons =
        {
            new(), new(), new(), new(),
        };

        void OnEnable()
        {
            var doc = GetComponent<UIDocument>();
            var root = doc.rootVisualElement;
            if (doc.panelSettings == null || root == null)
            {
                Debug.LogWarning("Agent_MainMenu: UIDocument needs a PanelSettings asset.");
                return;
            }

            // Roster is local — built once per activation and never mutated.
            var roster = new[] { standard, matt, kim, nick };
            _picks[0] = standard; _picks[1] = nick;   // Blue: STANDARD + NICK
            _picks[2] = matt;     _picks[3] = kim;    // Red:  MATT + KIM

            root.Clear();
            var safe = new VisualElement();
            safe.style.flexGrow = 1;
            Agent_UIStyle.ApplySafeArea(safe);
            safe.style.backgroundColor = Agent_UIStyle.Background;
            safe.style.alignItems = Align.Center;
            safe.style.justifyContent = Justify.Center;
            root.Add(safe);

            // All sizes are in 1080x1920 reference-resolution units (9:16 per
            // UNITY_RULES; PanelSettings scales the panel to the actual screen,
            // match-width).
            var title = new Label("PoSoccer");
            title.style.fontSize = 120;
            title.style.color = Color.white;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 8;
            safe.Add(title);

            var subtitle = new Label("pick the matchup");
            subtitle.style.fontSize = Agent_UIStyle.FontM;
            subtitle.style.color = Agent_UIStyle.TextMuted;
            subtitle.style.marginBottom = 60;
            safe.Add(subtitle);

            _slotButtons[0].Clear();
            safe.Add(BuildPickerRow("BLUE 1", Agent_UIStyle.BlueTeam, _slotButtons[0], roster, p => { _picks[0] = p; Restyle(); }));
            _slotButtons[1].Clear();
            safe.Add(BuildPickerRow("BLUE 2", Agent_UIStyle.BlueTeam, _slotButtons[1], roster, p => { _picks[1] = p; Restyle(); }));
            _slotButtons[2].Clear();
            safe.Add(BuildPickerRow("RED 1", Agent_UIStyle.RedTeam, _slotButtons[2], roster, p => { _picks[2] = p; Restyle(); }));
            _slotButtons[3].Clear();
            safe.Add(BuildPickerRow("RED 2", Agent_UIStyle.RedTeam, _slotButtons[3], roster, p => { _picks[3] = p; Restyle(); }));

            var play = new Button(StartMatch) { text = "PLAY" };
            play.style.fontSize = 72;
            play.style.unityFontStyleAndWeight = FontStyle.Bold;
            play.style.marginTop = 60;
            play.style.paddingLeft = 120; play.style.paddingRight = 120;
            play.style.paddingTop = 28; play.style.paddingBottom = 28;
            play.style.backgroundColor = Agent_UIStyle.Accent;
            play.style.color = Agent_UIStyle.TextPrimary;
            Agent_UIStyle.Round(play);
            safe.Add(play);

            var sound = Agent_UIStyle.SoundToggleButton();
            sound.style.color = Agent_UIStyle.TextMuted;
            sound.style.marginTop = 28;
            safe.Add(sound);

            Restyle();
        }

        VisualElement BuildPickerRow(string teamLabel, Color teamColor,
            System.Collections.Generic.List<Button> buttons,
            Reward_Settings[] roster,
            System.Action<Reward_Settings> onPick)
        {
            var section = new VisualElement();
            section.style.marginBottom = 36;
            section.style.alignItems = Align.Center;

            // Team-color band above the row so players know BLUE 1/2 vs RED 1/2
            // without reading the label.
            var band = new VisualElement();
            band.style.height = 6; band.style.width = 220;
            band.style.backgroundColor = teamColor;
            band.style.marginBottom = 12;
            Agent_UIStyle.Round(band, 3);
            section.Add(band);

            var header = new Label(teamLabel);
            header.style.fontSize = 48;
            header.style.color = teamColor;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginBottom = 14;
            section.Add(header);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            section.Add(row);

            foreach (var profile in roster)
            {
                if (profile == null) continue;
                var captured = profile;
                // Untrained players are driven by the rule-based bot - say so up front.
                var b = new Button(() => onPick(captured))
                {
                    text = profile.brainModel != null
                        ? profile.playerName : $"{profile.playerName}\n(BOT)",
                    userData = profile,
                };
                b.style.width = 250; b.style.height = 170;
                b.style.marginLeft = 12; b.style.marginRight = 12;
                b.style.fontSize = 38;
                b.style.unityFontStyleAndWeight = FontStyle.Bold;
                b.style.color = Color.black;
                b.style.backgroundColor = profile.playerColor;
                buttons.Add(b);
                row.Add(b);
            }
            return section;
        }

        void Restyle()
        {
            for (int slot = 0; slot < 4; slot++)
            {
                foreach (var b in _slotButtons[slot])
                {
                    bool selected = ReferenceEquals(b.userData, _picks[slot]);
                    float w = selected ? 4f : 0f;
                    b.style.borderTopWidth = w; b.style.borderBottomWidth = w;
                    b.style.borderLeftWidth = w; b.style.borderRightWidth = w;
                    b.style.borderTopColor = Color.white; b.style.borderBottomColor = Color.white;
                    b.style.borderLeftColor = Color.white; b.style.borderRightColor = Color.white;
                    b.style.opacity = selected ? 1f : 0.55f;
                }
            }
        }

        void StartMatch()
        {
            Agent_MatchSetup.Applied = true;
            Agent_MatchSetup.BluePlayer = _picks[0];
            Agent_MatchSetup.BluePlayer2 = _picks[1];
            Agent_MatchSetup.RedPlayer = _picks[2];
            Agent_MatchSetup.RedPlayer2 = _picks[3];
            SceneManager.LoadScene(matchScene);
        }
    }
}