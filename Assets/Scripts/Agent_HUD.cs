using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// UI Toolkit runtime HUD (UNITY_RULES: UI Toolkit only, Safe Area compliant,
    /// mobile portrait 9:16). Shows per-agent stamina bars and the episode step counter.
    /// Builds its element tree in code so no UXML asset wiring is required in-scene.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Agent_HUD : MonoBehaviour
    {
        public Agent_EnvController env;

        UIDocument _doc;
        Label _stepLabel;
        readonly List<(Agent_Soccer agent, VisualElement fill, Label label)> _bars = new();

        void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            var root = _doc.rootVisualElement;
            if (_doc.panelSettings == null || root == null)
            {
                Debug.LogWarning("Agent_HUD: assign a PanelSettings asset (with a runtime " +
                                 "theme) to the UIDocument; HUD disabled for this session.");
                enabled = false;
                return;
            }
            root.Clear();

            // Safe-area padding so notches/rounded corners never clip the HUD.
            var safe = new VisualElement { name = "safe-area" };
            safe.style.flexGrow = 1;
            ApplySafeArea(safe);
            root.Add(safe);

            var panel = new VisualElement { name = "hud-panel" };
            panel.style.position = Position.Absolute;
            panel.style.top = 8; panel.style.left = 8; panel.style.right = 8;
            panel.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            panel.style.borderTopLeftRadius = 8; panel.style.borderTopRightRadius = 8;
            panel.style.borderBottomLeftRadius = 8; panel.style.borderBottomRightRadius = 8;
            panel.style.paddingTop = 6; panel.style.paddingBottom = 6;
            panel.style.paddingLeft = 10; panel.style.paddingRight = 10;
            safe.Add(panel);

            _stepLabel = new Label("step 0");
            _stepLabel.style.color = Color.white;
            _stepLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            panel.Add(_stepLabel);
            _panel = panel;
        }

        VisualElement _panel;

        // Rows are built lazily: the env controller discovers its agents in Start(),
        // which runs after this component's OnEnable.
        void BuildRows()
        {
            var panel = _panel;
            if (panel == null) return;
            foreach (var agent in env.agents)
            {
                if (agent == null) continue;
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginTop = 3;

                var label = new Label(agent.name);
                label.style.color = agent.team == Agent_Soccer.Team.Blue
                    ? new Color(0.4f, 0.8f, 1f) : new Color(1f, 0.5f, 0.5f);
                label.style.width = 110;
                row.Add(label);

                var barBg = new VisualElement();
                barBg.style.flexGrow = 1;
                barBg.style.height = 8;
                barBg.style.backgroundColor = new Color(1f, 1f, 1f, 0.15f);
                var fill = new VisualElement();
                fill.style.height = 8;
                fill.style.backgroundColor = new Color(0.3f, 0.9f, 0.4f);
                barBg.Add(fill);
                row.Add(barBg);

                panel.Add(row);
                _bars.Add((agent, fill, label));
            }
        }

        static void ApplySafeArea(VisualElement element)
        {
            Rect safe = Screen.safeArea;
            element.style.paddingTop = Screen.height - safe.yMax;
            element.style.paddingBottom = safe.yMin;
            element.style.paddingLeft = safe.xMin;
            element.style.paddingRight = Screen.width - safe.xMax;
        }

        void Update()
        {
            if (env == null || _stepLabel == null) return;
            if (_bars.Count == 0 && env.agents.Count > 0) BuildRows();
            _stepLabel.text = $"step {env.StepCount} / goal width {env.CurrentGoalWidth:0.0}m";

            foreach (var (agent, fill, _) in _bars)
            {
                if (agent == null) continue;
                float ratio = agent.Stamina != null ? agent.Stamina.Ratio : 0f;
                fill.style.width = Length.Percent(ratio * 100f);
                fill.style.backgroundColor = Color.Lerp(
                    new Color(0.9f, 0.3f, 0.2f), new Color(0.3f, 0.9f, 0.4f), ratio);
            }
        }
    }
}
