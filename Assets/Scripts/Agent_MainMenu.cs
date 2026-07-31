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
        Reward_Settings _blue, _red;
        readonly System.Collections.Generic.List<Button> _blueButtons = new();
        readonly System.Collections.Generic.List<Button> _redButtons = new();

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
            _blue = standard;
            _red = standard;

            root.Clear();
            var safe = new VisualElement();
            safe.style.flexGrow = 1;
            ApplySafeArea(safe);
            safe.style.backgroundColor = new Color(0.05f, 0.09f, 0.07f);
            safe.style.alignItems = Align.Center;
            safe.style.justifyContent = Justify.Center;
            root.Add(safe);

            var title = new Label("PoSoccer");
            title.style.fontSize = 56;
            title.style.color = Color.white;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 4;
            safe.Add(title);

            var subtitle = new Label("pick the matchup");
            subtitle.style.fontSize = 18;
            subtitle.style.color = new Color(0.7f, 0.75f, 0.72f);
            subtitle.style.marginBottom = 28;
            safe.Add(subtitle);

            safe.Add(BuildPickerRow("BLUE", new Color(0.2f, 0.5f, 1f), _blueButtons,
                p => { _blue = p; Restyle(); }));
            safe.Add(BuildPickerRow("RED", new Color(1f, 0.25f, 0.2f), _redButtons,
                p => { _red = p; Restyle(); }));

            var play = new Button(StartMatch) { text = "PLAY" };
            play.style.fontSize = 30;
            play.style.unityFontStyleAndWeight = FontStyle.Bold;
            play.style.marginTop = 32;
            play.style.paddingLeft = 48; play.style.paddingRight = 48;
            play.style.paddingTop = 12; play.style.paddingBottom = 12;
            play.style.backgroundColor = new Color(0.16f, 0.55f, 0.28f);
            play.style.color = Color.white;
            play.style.borderTopLeftRadius = 10; play.style.borderTopRightRadius = 10;
            play.style.borderBottomLeftRadius = 10; play.style.borderBottomRightRadius = 10;
            safe.Add(play);

            Restyle();
        }

        VisualElement BuildPickerRow(string teamLabel, Color teamColor,
            System.Collections.Generic.List<Button> buttons,
            System.Action<Reward_Settings> onPick)
        {
            var section = new VisualElement();
            section.style.marginBottom = 18;
            section.style.alignItems = Align.Center;

            var header = new Label(teamLabel);
            header.style.fontSize = 20;
            header.style.color = teamColor;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginBottom = 8;
            section.Add(header);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            section.Add(row);

            foreach (var profile in _roster)
            {
                if (profile == null) continue;
                var captured = profile;
                var b = new Button(() => onPick(captured))
                {
                    text = profile.playerName,
                    userData = profile,
                };
                b.style.width = 86; b.style.height = 64;
                b.style.marginLeft = 4; b.style.marginRight = 4;
                b.style.fontSize = 15;
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
            void Apply(System.Collections.Generic.List<Button> buttons, Reward_Settings picked)
            {
                foreach (var b in buttons)
                {
                    bool selected = ReferenceEquals(b.userData, picked);
                    float w = selected ? 4f : 0f;
                    b.style.borderTopWidth = w; b.style.borderBottomWidth = w;
                    b.style.borderLeftWidth = w; b.style.borderRightWidth = w;
                    b.style.borderTopColor = Color.white; b.style.borderBottomColor = Color.white;
                    b.style.borderLeftColor = Color.white; b.style.borderRightColor = Color.white;
                    b.style.opacity = selected ? 1f : 0.55f;
                }
            }
            Apply(_blueButtons, _blue);
            Apply(_redButtons, _red);
        }

        void StartMatch()
        {
            Agent_MatchSetup.BluePlayer = _blue;
            Agent_MatchSetup.RedPlayer = _red;
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
