using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// The one settings surface in the game: a bottom sheet with a tab strip,
    /// opened from the menu and from the pause panel.
    ///
    /// WHY ONE. Settings had accreted wherever there was room: a three-button
    /// accessibility shelf on the menu, four volume sliders on the pause panel, a
    /// mute toggle in both, and a broadcast-strip switch that was a serialized
    /// field nobody could reach on a phone. Each was reasonable where it landed
    /// and together they meant no screen had all of them - you could change the
    /// palette only from the menu and the mix only mid-match.
    ///
    /// SHAPE. A dim scrim over the host panel with the sheet docked to the bottom
    /// edge, inside thumb reach in portrait. Four tabs switch the body IN PLACE;
    /// the body has a fixed height so the tab strip never moves under the thumb.
    /// No nested popups, no ScrollView: a tab that outgrows the body is a tab that
    /// needs splitting, and the fixed height is what makes that visible.
    ///
    /// EVERY CHANGE APPLIES LIVE AND PERSISTS IMMEDIATELY, as the volume sliders
    /// always have. There is no OK button, so there is no half-applied state and
    /// closing is always safe - by the CLOSE button, a tap on the scrim, or back.
    ///
    /// Static, because it holds no scene state: the settings themselves live in
    /// PlayerPrefs behind Agent_Audio / Agent_Palette / Agent_Haptics, and the
    /// sheet is rebuilt from them on every open. The only thing it keeps is which
    /// tab was last looked at.
    ///
    /// The host passes a callback that runs after any change, for whatever it has
    /// baked in that a USS variable cannot reach - the HUD re-reads the broadcast
    /// switch and rebuilds its team-tinted chips there.
    /// </summary>
    public static class Agent_SettingsSheet
    {
        static readonly string[] TabNames = { "AUDIO", "DISPLAY", "ACCESS", "ADVANCED" };

        const int TAB_AUDIO = 0;
        const int TAB_DISPLAY = 1;
        const int TAB_ACCESS = 2;

        static VisualElement _host, _scrim, _body;
        static readonly Button[] Tabs = new Button[4];
        static System.Action _onChanged;
        static int _tab;

        /// <summary>
        /// True while the sheet is on a live panel. The panel check matters: these
        /// are statics, and a scene load destroys the host document without
        /// calling <see cref="Close"/>, which would otherwise leave this reporting
        /// an open sheet that no longer exists - and every back press after that
        /// would be swallowed closing it.
        /// </summary>
        public static bool IsOpen => _scrim != null && _scrim.panel != null;

        /// <summary>
        /// Raises the sheet over <paramref name="host"/> (a panel root).
        /// <paramref name="onChanged"/> runs after every setting change.
        /// </summary>
        public static void Open(VisualElement host, System.Action onChanged = null)
        {
            if (host == null) return;
            Close();

            _host = host;
            _onChanged = onChanged;

            _scrim = new VisualElement { name = "settings-sheet" };
            _scrim.AddToClassList("sheet-scrim");
            // A tap on the scrim itself dismisses. The target check matters:
            // without it every tap inside the sheet bubbles up here and closes it
            // the moment a slider is touched.
            _scrim.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, _scrim)) Close();
            });
            // The scrim is absolute on the panel root, so it carries its own
            // safe-area inset - and reserves Agent_Chrome's bottom bar, because
            // the chrome draws ABOVE this panel and its DEBUG button would
            // otherwise sit on top of the sheet's tab strip.
            Agent_UIStyle.BindSafeArea(_scrim, 0f, Agent_Chrome.BAR_HEIGHT);

            var sheet = new VisualElement { name = "settings-panel" };
            sheet.AddToClassList("sheet");
            _scrim.Add(sheet);

            var head = new VisualElement();
            head.AddToClassList("sheet__head");
            var title = new Label("SETTINGS");
            title.AddToClassList("sheet__title");
            head.Add(title);
            var close = new Button(Close) { text = "CLOSE", name = "settings-close" };
            close.AddToClassList("btn");
            head.Add(close);
            sheet.Add(head);

            _body = new VisualElement { name = "settings-body" };
            _body.AddToClassList("sheet__body");
            sheet.Add(_body);

            // Tabs BELOW the body: the sheet is docked to the bottom edge, so the
            // strip a thumb switches with is the part nearest the thumb.
            var tabs = new VisualElement();
            tabs.AddToClassList("tabs");
            for (int tabIndex = 0; tabIndex < TabNames.Length; tabIndex++)
            {
                int captured = tabIndex;
                var tab = new Button(() => ShowTab(captured)) { text = TabNames[tabIndex] };
                tab.AddToClassList("btn");
                tab.AddToClassList("tab");
                Tabs[tabIndex] = tab;
                tabs.Add(tab);
            }
            sheet.Add(tabs);

            host.Add(_scrim);
            Agent_UIStyle.PlayEntrance(_scrim);
            ShowTab(_tab);
        }

        /// <summary>Dismisses the sheet. Safe when it is not open.</summary>
        public static void Close()
        {
            if (_scrim != null) _scrim.RemoveFromHierarchy();
            _scrim = null;
            _body = null;
            _host = null;
            _onChanged = null;
            for (int tabIndex = 0; tabIndex < Tabs.Length; tabIndex++) Tabs[tabIndex] = null;
        }

        static void ShowTab(int index)
        {
            if (_body == null) return;
            _tab = Mathf.Clamp(index, 0, TabNames.Length - 1);

            for (int tabIndex = 0; tabIndex < Tabs.Length; tabIndex++)
            {
                if (Tabs[tabIndex] != null)
                    Tabs[tabIndex].EnableInClassList(Agent_UIStyle.ON, tabIndex == _tab);
            }

            _body.Clear();
            switch (_tab)
            {
                case TAB_AUDIO: BuildAudio(); break;
                case TAB_DISPLAY: BuildDisplay(); break;
                case TAB_ACCESS: BuildAccess(); break;
                default: BuildAdvanced(); break;
            }
        }

        // -- Tabs --------------------------------------------------------------

        static void BuildAudio()
        {
            _body.Add(Row("Sound", Switch(
                () => !Agent_Audio.Muted,
                on => Agent_Audio.Muted = !on)));
            _body.Add(Agent_UIStyle.VolumeSliders());
        }

        static void BuildDisplay()
        {
            _body.Add(Row("Text size", Cycle(
                () => TypeLabel(Agent_Palette.TypeScale),
                () => Agent_Palette.TypeScale != 0,
                Agent_Palette.CycleTypeScale)));
            _body.Add(Row("Frame-rate counter", Switch(
                () => Agent_Chrome.ShowFps,
                on => Agent_Chrome.ShowFps = on)));
        }

        static void BuildAccess()
        {
            // What each control is currently SET TO, never what it would switch
            // to. A control labelled with its next state reads as an instruction
            // and leaves the player with no way to know where they are - which
            // matters most for exactly the people this tab exists for.
            _body.Add(Row("Team colours", Cycle(
                () => Agent_Palette.Label(Agent_Palette.Current),
                () => Agent_Palette.Current != Agent_Palette.Mode.Standard,
                Agent_Palette.CycleMode)));
            _body.Add(Row("Vibration", Switch(
                () => Agent_Haptics.Enabled,
                on => Agent_Haptics.Enabled = on)));
        }

        static void BuildAdvanced()
        {
            _body.Add(Row("Broadcast stats in a match", Switch(
                () => Agent_HUD.BroadcastEnabled,
                on => Agent_HUD.BroadcastEnabled = on)));
            _body.Add(Row("Open health panel on start", Switch(
                () => Agent_Telemetry.ShowOnStart,
                on => Agent_Telemetry.ShowOnStart = on)));
        }

        static string TypeLabel(int step)
        {
            switch (step)
            {
                case 1: return "LARGE";
                case 2: return "LARGER";
                default: return "NORMAL";
            }
        }

        // -- Controls ----------------------------------------------------------

        static VisualElement Row(string label, VisualElement control)
        {
            var row = new VisualElement();
            row.AddToClassList("sheet__row");

            var name = new Label(label);
            name.AddToClassList("sheet__label");
            row.Add(name);

            row.Add(control);
            return row;
        }

        /// <summary>An ON / OFF control.</summary>
        static Button Switch(System.Func<bool> get, System.Action<bool> set)
        {
            return Cycle(
                () => get() ? "ON" : "OFF",
                get,
                () => set(!get()));
        }

        /// <summary>
        /// A control that steps through its values on each tap and always shows
        /// the current one. A .btn, so it is --touch-min tall like every other
        /// tappable control in the game.
        /// </summary>
        static Button Cycle(System.Func<string> text, System.Func<bool> on, System.Action advance)
        {
            Button button = null;
            button = new Button(() =>
            {
                advance();
                button.text = text();
                button.EnableInClassList(Agent_UIStyle.ON, on());
                NotifyChanged();
            })
            { text = text() };
            button.AddToClassList("btn");
            button.AddToClassList("sheet__value");
            button.EnableInClassList(Agent_UIStyle.ON, on());
            return button;
        }

        /// <summary>
        /// Re-stamps the palette and type-scale classes on the host root - they are
        /// USS variable overrides, so that one call restyles every element on the
        /// panel, this sheet included - then lets the host fix up anything it has
        /// baked in that a variable cannot reach.
        /// </summary>
        static void NotifyChanged()
        {
            if (_host != null) Agent_UIStyle.ApplyAccessibility(_host);
            if (_onChanged != null) _onChanged();
        }
    }
}
