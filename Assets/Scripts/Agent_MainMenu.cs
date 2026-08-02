using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Opening menu (UI Toolkit, mobile portrait, safe-area): pick the Blue and
    /// Red player from the roster, then launch the match scene. The whole tree is
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

        Reward_Settings[] _roster;
        bool _twoVTwo = true;
        Button _btn1v1, _btn2v2;
        readonly Reward_Settings[] _picks = new Reward_Settings[4];   // B1, B2, R1, R2
        readonly VisualElement[] _slotSections = new VisualElement[4];
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

            _roster = new[] { standard, matt, kim, nick };
            _picks[0] = standard; _picks[1] = nick;   // Blue: STANDARD + NICK
            _picks[2] = matt;     _picks[3] = kim;    // Red:  MATT + KIM

            root.Clear();
            var safe = new VisualElement();
            safe.style.flexGrow = 1;
            ApplySafeArea(safe);
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
            subtitle.style.marginBottom = 70;
            safe.Add(subtitle);

            safe.Add(BuildModeRow());

            var blue = Agent_UIStyle.BlueTeam;
            var red = Agent_UIStyle.RedTeam;
            _slotSections[0] = BuildPickerRow("BLUE 1", blue, _slotButtons[0], p => { _picks[0] = p; Restyle(); });
            _slotSections[1] = BuildPickerRow("BLUE 2", blue, _slotButtons[1], p => { _picks[1] = p; Restyle(); });
            _slotSections[2] = BuildPickerRow("RED 1", red, _slotButtons[2], p => { _picks[2] = p; Restyle(); });
            _slotSections[3] = BuildPickerRow("RED 2", red, _slotButtons[3], p => { _picks[3] = p; Restyle(); });
            foreach (var section in _slotSections) safe.Add(section);
            ApplyMode();

            var play = new Button(StartMatch) { text = "PLAY" };
            play.style.fontSize = 72;
            play.style.unityFontStyleAndWeight = FontStyle.Bold;
            play.style.marginTop = 80;
            play.style.paddingLeft = 120; play.style.paddingRight = 120;
            play.style.paddingTop = 28; play.style.paddingBottom = 28;
            play.style.backgroundColor = Agent_UIStyle.Accent;
            play.style.color = Agent_UIStyle.TextPrimary;
            Agent_UIStyle.Round(play);
            safe.Add(play);

            Button sound = null;
            sound = new Button(() =>
            {
                Agent_Audio.Muted = !Agent_Audio.Muted;
                sound.text = Agent_Audio.Muted ? "SOUND: OFF" : "SOUND: ON";
            })
            { text = Agent_Audio.Muted ? "SOUND: OFF" : "SOUND: ON" };
            sound.style.fontSize = Agent_UIStyle.FontS;
            sound.style.color = Agent_UIStyle.TextMuted;
            sound.style.backgroundColor = Agent_UIStyle.PanelBg;
            Agent_UIStyle.Round(sound);
            sound.style.marginTop = 28;
            sound.style.paddingLeft = 40; sound.style.paddingRight = 40;
            sound.style.paddingTop = 14; sound.style.paddingBottom = 14;
            safe.Add(sound);

            Restyle();
        }

        VisualElement BuildModeRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 36;

            Button Make(string text, bool mode)
            {
                var b = new Button(() => { _twoVTwo = mode; ApplyMode(); }) { text = text };
                b.style.fontSize = Agent_UIStyle.FontM;
                b.style.unityFontStyleAndWeight = FontStyle.Bold;
                b.style.width = 220; b.style.height = 96;
                b.style.marginLeft = 12; b.style.marginRight = 12;
                Agent_UIStyle.Round(b);
                row.Add(b);
                return b;
            }
            _btn1v1 = Make("1 v 1", false);
            _btn2v2 = Make("2 v 2", true);
            return row;
        }

        void ApplyMode()
        {
            void Style(Button b, bool selected)
            {
                b.style.backgroundColor = selected ? Agent_UIStyle.Accent : Agent_UIStyle.PanelBg;
                b.style.color = selected ? Agent_UIStyle.TextPrimary : Agent_UIStyle.TextMuted;
            }
            Style(_btn1v1, !_twoVTwo);
            Style(_btn2v2, _twoVTwo);

            // Slots 1 and 3 are the second players - only shown in 2v2.
            if (_slotSections[1] != null)
                _slotSections[1].style.display = _twoVTwo ? DisplayStyle.Flex : DisplayStyle.None;
            if (_slotSections[3] != null)
                _slotSections[3].style.display = _twoVTwo ? DisplayStyle.Flex : DisplayStyle.None;
        }

        VisualElement BuildPickerRow(string teamLabel, Color teamColor,
            System.Collections.Generic.List<Button> buttons,
            System.Action<Reward_Settings> onPick)
        {
            var section = new VisualElement();
            section.style.marginBottom = 44;
            section.style.alignItems = Align.Center;

            var header = new Label(teamLabel);
            header.style.fontSize = 48;
            header.style.color = teamColor;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginBottom = 18;
            section.Add(header);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            section.Add(row);

            foreach (var profile in _roster)
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
            Agent_MatchSetup.TwoVTwo = _twoVTwo;
            Agent_MatchSetup.BluePlayer = _picks[0];
            Agent_MatchSetup.BluePlayer2 = _twoVTwo ? _picks[1] : null;
            Agent_MatchSetup.RedPlayer = _picks[2];
            Agent_MatchSetup.RedPlayer2 = _twoVTwo ? _picks[3] : null;
            SceneManager.LoadScene(matchScene);
        }

        static void ApplySafeArea(VisualElement element)
        {
            Rect safe = Screen.safeArea;
            element.style.paddingTop = Screen.height - safe.yMax;
            element.style.paddingBottom = safe.yMin;
            element.style.paddingLeft = safe.xMin;
            element.style.paddingRight = Screen.width - safe.xMax;
        }
    }
}
