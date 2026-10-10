using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// The spectator's hands: tap a player to lock the camera on it, pinch or
    /// scroll to zoom, <c>[</c> and <c>]</c> to step the playback speed.
    ///
    /// EVERYTHING HERE IS A REQUEST TO SOMETHING THAT ALREADY OWNS THE STATE. This
    /// component holds no camera transform and writes no time scale. Focus goes to
    /// <see cref="Agent_CameraFollow.SetFocus"/>, zoom to
    /// <see cref="Agent_CameraFollow.SpectatorZoom"/>, speed to
    /// <see cref="Agent_TimeFreeze.SpectatorSpeed"/> - so the replay still outranks
    /// a focus, a freeze still outranks a speed, and the pan clamp that keeps the
    /// goal mouths out from under the HUD bands still applies to a zoomed shot.
    /// A second writer of any of those three is precisely the bug each owner's
    /// docstring exists to prevent.
    ///
    /// WHAT COUNTS AS A TAP ON THE PITCH. The HUD's root container spans the whole
    /// screen and is pickable, so "did the panel pick anything" is true everywhere
    /// and cannot be the test. A tap is passed through to the world only when all
    /// of these hold:
    ///  - the clock is not frozen, at press AND at release (pause panel, end
    ///    panel, countdown, and the goal replay - whose own skip gesture is this
    ///    same click);
    ///  - it lands in the clear band, not on the scoreboard or the chip row;
    ///  - nothing PAINTED and no Button is under it in any UI panel (the chrome's
    ///    MENU / DEBUG buttons, the brain-cam sheet).
    ///
    /// KEYS. <c>[</c> slower, <c>]</c> faster, through 0.25 / 0.5 / 1 / 2 / 4. Chosen
    /// against the keys already taken in a match: I V T B F3 Tab Esc Space, and
    /// W A S D Q E K Shift for the heuristic driver.
    ///
    /// Presentation only, and it reaches the clock and the camera, so it gates on
    /// <see cref="Agent_Presentation.IsMatchScene"/>: never in SCN_Training, never
    /// in an eval, and never on the gallery's cloned pitches - it is installed by
    /// Agent_Presentation.Install, which the gallery does not call.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_SpectatorInput : MonoBehaviour
    {
        [Tooltip("World metres around the tap inside which the nearest player is picked. " +
                 "Bodies are ~0.5 m across; on a phone the wide shot puts that at a few " +
                 "millimetres, so the radius is a finger, not a body.")]
        [SerializeField] private float _pickRadius = 1.2f;
        [Tooltip("Longest press (seconds, unscaled) that still counts as a tap.")]
        [SerializeField] private float _tapMaxSeconds = 0.35f;
        [Tooltip("Furthest the pointer may travel during a tap, as a fraction of screen height.")]
        [Range(0.005f, 0.1f)] [SerializeField] private float _tapMaxTravel = 0.025f;
        [Tooltip("Zoom change per mouse-wheel notch.")]
        [Range(0.01f, 0.2f)] [SerializeField] private float _scrollStep = 0.05f;
        [SerializeField] private bool _enableSpectatorInput = true;

        /// <summary>The speeds <c>[</c> and <c>]</c> step through, ascending.</summary>
        static readonly float[] SpeedSteps = { 0.25f, 0.5f, 1f, 2f, 4f };

        Agent_EnvController _env;
        Agent_HUD _hud;
        Camera _cam;
        Agent_CameraFollow _follow;

        Agent_Soccer _focused;

        bool _pressed;
        bool _pressFrozen;
        bool _gesture;
        Vector2 _pressPosition;
        float _pressTime;

        bool _pinching;
        float _pinchDistance;

        /// <summary>The player the camera is locked on, or null.</summary>
        public Agent_Soccer FocusedAgent =>
            _focused != null && _follow != null && _follow.Focus == _focused.transform
                ? _focused : null;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindAnyObjectByType<Agent_HUD>();

            if (!_enableSpectatorInput || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _cam = Camera.main;
            _follow = _cam != null ? _cam.GetComponent<Agent_CameraFollow>() : null;
            if (_follow == null)
            {
                // No rig, nothing to steer. The speed keys would still work, but a
                // match scene without Agent_CameraFollow is the gallery or a broken
                // bootstrap, and neither wants half a control scheme.
                enabled = false;
            }
        }

        void OnDestroy()
        {
            // The camera lives on another GameObject and can outlive this one for a
            // frame during teardown; do not leave it locked on a pitch that is going.
            if (_follow != null) _follow.ClearFocus();
        }

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.leftBracketKey.wasPressedThisFrame) StepSpeed(-1);
                if (keyboard.rightBracketKey.wasPressedThisFrame) StepSpeed(1);
            }

            int touchesDown = UpdatePinch();
            UpdateScroll();
            UpdateTap(touchesDown);
        }

        // -- Speed -------------------------------------------------------------

        /// <summary>
        /// Moves <see cref="Agent_TimeFreeze.SpectatorSpeed"/> one notch along
        /// 0.25 / 0.5 / 1 / 2 / 4 and returns the new value. Steps from the notch
        /// NEAREST the current speed, so a value set to something off the ladder
        /// by a slider still steps sensibly. Static: it touches only static state,
        /// and an on-screen control can call it without finding this component.
        /// </summary>
        public static float StepSpeed(int direction)
        {
            float current = Agent_TimeFreeze.SpectatorSpeed;
            int nearest = 0;
            float best = float.MaxValue;
            for (int stepIndex = 0; stepIndex < SpeedSteps.Length; stepIndex++)
            {
                float gap = Mathf.Abs(SpeedSteps[stepIndex] - current);
                if (gap < best)
                {
                    best = gap;
                    nearest = stepIndex;
                }
            }

            int next = Mathf.Clamp(nearest + (direction < 0 ? -1 : 1), 0, SpeedSteps.Length - 1);
            Agent_TimeFreeze.SpectatorSpeed = SpeedSteps[next];
            return Agent_TimeFreeze.SpectatorSpeed;
        }

        // -- Zoom --------------------------------------------------------------

        /// <summary>
        /// Two fingers: the ratio of this frame's separation to last frame's scales
        /// the zoom. Returns how many touches are down, which the tap detector
        /// needs so the fingers of a pinch are never read as taps.
        ///
        /// Exactly two. Three is Agent_Telemetry's toggle gesture, and a zoom that
        /// also fired during it would move the camera every time someone opened
        /// the overlay.
        /// </summary>
        int UpdatePinch()
        {
            Touchscreen touchscreen = Touchscreen.current;
            if (touchscreen == null)
            {
                _pinching = false;
                return 0;
            }

            int down = 0;
            Vector2 first = default;
            Vector2 second = default;
            var touches = touchscreen.touches;
            for (int touchIndex = 0; touchIndex < touches.Count; touchIndex++)
            {
                var touch = touches[touchIndex];
                if (!touch.press.isPressed) continue;
                if (down == 0) first = touch.position.ReadValue();
                else if (down == 1) second = touch.position.ReadValue();
                down++;
            }

            if (down >= 2) _gesture = true;

            if (down != 2)
            {
                _pinching = false;
                return down;
            }

            float distance = (first - second).magnitude;
            // The first frame of a pinch only records the separation: there is no
            // previous frame to take a ratio against, and using a stale one from
            // the last gesture would jump the zoom on contact.
            if (_pinching && _pinchDistance > 1f && distance > 1f)
            {
                // Fingers apart = larger separation = SMALLER ortho = zoom in.
                _follow.SpectatorZoom *= _pinchDistance / distance;
            }
            _pinchDistance = distance;
            _pinching = true;
            return down;
        }

        void UpdateScroll()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Approximately(scroll, 0f)) return;

            // While frozen the wheel belongs to whatever panel is up - the end
            // panel scrolls.
            if (Agent_TimeFreeze.IsFrozen) return;

            // Wheel up = zoom in. Sign only: the magnitude of one notch is 120 on
            // Windows and 1 on some other platforms.
            _follow.SpectatorZoom -= Mathf.Sign(scroll) * _scrollStep;
        }

        // -- Focus -------------------------------------------------------------

        void UpdateTap(int touchesDown)
        {
            Pointer pointer = Pointer.current;
            if (pointer == null) return;

            if (pointer.press.wasPressedThisFrame)
            {
                _pressed = true;
                _pressPosition = pointer.position.ReadValue();
                _pressTime = Time.unscaledTime;
                _pressFrozen = Agent_TimeFreeze.IsFrozen;
                _gesture = touchesDown >= 2;
            }

            if (!_pressed || !pointer.press.wasReleasedThisFrame) return;
            _pressed = false;

            if (_gesture) return;
            if (_pressFrozen || Agent_TimeFreeze.IsFrozen) return;
            if (Time.unscaledTime - _pressTime > _tapMaxSeconds) return;

            Vector2 released = pointer.position.ReadValue();
            float travel = Mathf.Max(12f, Screen.height * _tapMaxTravel);
            if ((released - _pressPosition).sqrMagnitude > travel * travel) return;

            if (!IsOnThePitch(_pressPosition)) return;

            ToggleFocusAt(_cam.ScreenToWorldPoint(
                new Vector3(_pressPosition.x, _pressPosition.y, 0f)));
        }

        /// <summary>
        /// Focus the nearest player within the pick radius of
        /// <paramref name="worldPoint"/>; tapping the focused player again, or
        /// empty pitch, releases the lock. Public so the tap can be driven without
        /// a pointer device.
        /// </summary>
        public void ToggleFocusAt(Vector2 worldPoint)
        {
            if (_follow == null || _env == null) return;

            Agent_Soccer nearest = null;
            float best = _pickRadius * _pickRadius;
            var agents = _env.agents;
            for (int agentIndex = 0; agentIndex < agents.Count; agentIndex++)
            {
                Agent_Soccer agent = agents[agentIndex];
                if (agent == null || agent.Body == null) continue;

                float distance = (agent.Body.position - worldPoint).sqrMagnitude;
                if (distance <= best)
                {
                    best = distance;
                    nearest = agent;
                }
            }

            if (nearest == null || nearest == FocusedAgent)
            {
                _focused = null;
                _follow.ClearFocus();
                return;
            }

            _focused = nearest;
            _follow.SetFocus(nearest.transform);
        }

        /// <summary>
        /// True when a screen point is over open pitch rather than interface. Runs
        /// on a completed tap only - never per frame - so the document sweep and
        /// the pick are paid a few times a minute.
        /// </summary>
        bool IsOnThePitch(Vector2 screenPoint)
        {
            if (Screen.height <= 0) return false;

            // The bands first: they are opaque, they are where every HUD control
            // lives, and the check is two comparisons.
            float height01 = screenPoint.y / Screen.height;
            if (_hud != null)
            {
                if (height01 < _hud.BottomBandFraction) return false;
                if (height01 > 1f - _hud.TopBandFraction) return false;
            }

            // UI Toolkit's panel space has its origin at the TOP left; the Input
            // System's screen space has it at the bottom left.
            var flipped = new Vector2(screenPoint.x, Screen.height - screenPoint.y);

            // Documents sharing one PanelSettings share one panel, so consecutive
            // repeats are skipped rather than picked again.
            UIDocument[] documents = FindObjectsByType<UIDocument>();
            IPanel previous = null;
            for (int documentIndex = 0; documentIndex < documents.Length; documentIndex++)
            {
                UIDocument document = documents[documentIndex];
                if (document == null) continue;

                VisualElement root = document.rootVisualElement;
                if (root == null) continue;

                IPanel panel = root.panel;
                if (panel == null || panel == previous) continue;
                previous = panel;

                Vector2 panelPoint = RuntimePanelUtils.ScreenToPanel(panel, flipped);
                VisualElement hit = panel.Pick(panelPoint);
                while (hit != null)
                {
                    // A Button is interface whatever it looks like; anything else
                    // counts only if it actually paints, which is what separates
                    // the brain-cam sheet from the HUD's invisible full-screen
                    // layout container.
                    if (hit is Button) return false;
                    if (hit.resolvedStyle.backgroundColor.a > 0.05f) return false;
                    hit = hit.parent;
                }
            }

            return true;
        }
    }
}
