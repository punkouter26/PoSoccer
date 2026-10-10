using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Ball-following camera that frames the pitch to the actual viewport and tightens
    /// on the action when play is slow.
    ///
    /// Three things drive the zoom, widest wins:
    ///  - a wide establishing shot at kickoff and after each goal;
    ///  - the bounding box of the ball plus every player, so a scrum in one corner
    ///    fills the screen while a stretched counter-attack pulls back;
    ///  - ball speed, which biases wider so a fast break does not outrun the frame.
    ///
    /// The wide baseline is DERIVED, not authored: it is the smallest orthographic size
    /// that still shows the whole pitch at the current aspect, INSIDE the part of the
    /// screen the HUD does not cover. On a 9:16 phone the pitch is narrower than the
    /// screen, so this resolves to "fit the pitch height into the clear band", filling
    /// what is visible instead of leaving the authored letterbox - or, as it did before
    /// 2026-09-13, hiding both goal mouths under the scoreboard and the chip row.
    /// Recomputed every frame so Agent_PitchSizing resizing the pitch per squad size, the
    /// HUD bands growing with the squad, and any resolution, safe-area or orientation
    /// change are all picked up for free. See the block above FitOrthoSize.
    ///
    /// Who owns the frame, strongest first: the replay override, a spectator's
    /// focus lock, a director shot, and finally plain ball follow - which leads the
    /// ball by its velocity. The viewer's pinch / scroll zoom multiplies whichever
    /// of the last three is active.
    ///
    /// Execution order -50 so the camera is settled before MatchFX samples Camera.main.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class Agent_CameraFollow : MonoBehaviour
    {
        [Tooltip("Start wide + zoom to tight delay after kickoff (seconds).")]
        [SerializeField] private float _kickoffHoldSeconds = 2.0f;
        [Tooltip("Wide linger after each goal (seconds).")]
        [SerializeField] private float _goalHoldSeconds = 1.5f;
        [Tooltip("Tightest allowed framing as a fraction of the wide shot. " +
                 "0.45 = the camera may zoom to 2.2x when play is slow and compact.")]
        [Range(0.25f, 1f)] [SerializeField] private float _tightestFraction = 0.45f;
        [Tooltip("World units of breathing room kept around the ball and players.")]
        [SerializeField] private float _actionPadding = 3.0f;
        [Tooltip("Ball speed (m/s) at which the camera is pulled fully back to the wide " +
                 "shot. Below this it interpolates toward the tight action framing.")]
        [SerializeField] private float _speedForWide = 7.0f;
        [Tooltip("Position lerp toward the ball (per second at 60 fps).")]
        [Range(2f, 12f)] [SerializeField] private float _followSpeed = 6f;
        [Tooltip("Extra world units the view may show beyond the touchline.")]
        [SerializeField] private float _edgeMargin = DEFAULT_EDGE_MARGIN;
        [Tooltip("Fraction of the pitch WIDTH the wide shot may crop on very tall screens, " +
                 "so the pitch keeps filling the height instead of shrinking. The camera pans " +
                 "horizontally to cover whatever is cropped.")]
        [Range(0f, 0.35f)] [SerializeField] private float _maxWidthCrop = DEFAULT_MAX_WIDTH_CROP;

        [Header("Speed look-ahead (ball follow only)")]
        [Tooltip("Lead the ball by its own velocity, so a moving ball sits behind centre " +
                 "with the space it is travelling into ahead of it. Off = centre the ball.")]
        [SerializeField] private bool _enableLookAhead = true;
        [Tooltip("Seconds of ball travel the follow target leads by.")]
        [SerializeField] private float _lookAheadSeconds = 0.35f;
        [Tooltip("Hard cap on the lead, world metres.")]
        [SerializeField] private float _lookAheadMax = 3f;
        [Tooltip("How fast the lead chases its target (per second). Low enough that a " +
                 "ball bouncing off a wall - velocity reversed in one tick - swings the " +
                 "frame over a few hundred milliseconds instead of snapping it.")]
        [Range(1f, 12f)] [SerializeField] private float _lookAheadSmoothing = 4f;
        [Tooltip("Largest share of the visible half-frame the lead may use on each axis. " +
                 "The metre cap alone is not enough: at the tightest framing 3 m is nearly " +
                 "the whole half-width, and a lead that large frames the space and loses the ball.")]
        [Range(0f, 0.5f)] [SerializeField] private float _lookAheadFrameShare = 0.35f;

        [Header("Focus lock")]
        [Tooltip("Framing held on a focused player, as a fraction of the wide shot.")]
        [Range(0.25f, 1f)] [SerializeField] private float _focusFraction = 0.5f;

        Camera _cam;
        Agent_EnvController _env;
        Agent_HUD _hud;
        float _wideUntilTime;
        bool _wide;
        Transform _overrideTarget;
        float _overrideOrtho;
        Transform _focus;
        Vector2 _lookAhead;
        float _spectatorZoom = 1f;

        /// <summary>
        /// Point the camera at something other than the live ball, at a fixed
        /// framing. Used by <see cref="Agent_Replay"/>: a goal replay plays back
        /// ghosts near the goal mouth while the real ball has ALREADY been reset
        /// to the centre spot by Agent_EnvController.ResetPitch, so without an
        /// override the camera would pan away from the very thing it is showing.
        /// The pitch pan clamp still applies, so an override can never reveal the
        /// void outside the touchline. Pass null (or call
        /// <see cref="ClearOverrideTarget"/>) to hand the camera back to the ball.
        /// </summary>
        public void SetOverrideTarget(Transform target, float orthoSize)
        {
            _overrideTarget = target;
            _overrideOrtho = Mathf.Max(1f, orthoSize);
        }

        public void ClearOverrideTarget() => _overrideTarget = null;

        /// <summary>True while a replay (or anything else) owns the framing outright.</summary>
        public bool HasOverrideTarget => _overrideTarget != null;

        // ── Spectator focus lock ────────────────────────────────────────────
        //
        // PRIORITY: replay override > FOCUS > director shot > ball follow.
        //
        // Below the override because a goal replay is the one thing a viewer who
        // asked to watch a player still wants to see; the focus is not cleared by
        // it and resumes when the replay hands the camera back. Above the director
        // because a human choosing a subject is a stronger statement than a shot
        // grammar inferring one - Agent_Director reads Focus and stands down
        // rather than requesting shots that would be ignored.
        //
        // The pan clamp, the HUD inset and the smoothing all still apply, exactly
        // as they do for a shot: this retargets the rig, it does not bypass it.

        /// <summary>
        /// Hold an iso-style shot on <paramref name="target"/> until
        /// <see cref="ClearFocus"/> is called or the target is destroyed. Passing
        /// null clears.
        /// </summary>
        public void SetFocus(Transform target) => _focus = target;

        /// <summary>Hand the camera back to the director / ball follow.</summary>
        public void ClearFocus() => _focus = null;

        /// <summary>
        /// The transform currently focused, or null. Reports a real null for a
        /// target that has been destroyed - a destroyed Unity object still
        /// compares equal to null but is not reference-null, and a caller doing
        /// anything other than <c>== null</c> with it would otherwise be handed a
        /// dead object.
        /// </summary>
        public Transform Focus => _focus != null ? _focus : null;

        /// <summary>Tightest the spectator zoom may go, as a multiplier on the current shot.</summary>
        public const float MIN_SPECTATOR_ZOOM = 0.6f;

        /// <summary>
        /// Extra zoom the viewer has dialled in (pinch / scroll), 0.6 .. 1,
        /// multiplied onto whatever framing the rig would otherwise have chosen -
        /// wide hold, ball follow, director shot or focus alike. 1 = untouched.
        /// Never applied to a replay override: that framing is authored.
        ///
        /// It does NOT change <see cref="CurrentWideOrtho"/>. That number is the
        /// zoom REFERENCE every shot fraction is taken against and the one the
        /// portrait tests assert the HUD inset on; scaling it would make every
        /// director shot shrink twice.
        /// </summary>
        public float SpectatorZoom
        {
            get => _spectatorZoom;
            set => _spectatorZoom = Mathf.Clamp(value, MIN_SPECTATOR_ZOOM, 1f);
        }

        // ── Director shot channel ───────────────────────────────────────────
        //
        // A SHOT is weaker than an override: it asks for a focus point and a
        // framing, and the pan clamp, smoothing and pitch bounds all still apply
        // exactly as they do for the live ball shot. Agent_Director drives this.
        //
        // It is a separate channel from SetOverrideTarget on purpose. The replay
        // must be able to take the camera away from the director mid-cut without
        // the two of them fighting over one field, so the override simply wins:
        // LateUpdate checks it first and the shot is ignored while it stands.
        //
        // A shot also expires. A director that stops ticking - disabled, gated
        // out of a training scene, destroyed on scene change - must hand the
        // camera back rather than freeze it on the last frame it managed to
        // request, so a shot older than SHOT_TIMEOUT is dropped.

        const float SHOT_TIMEOUT = 0.5f;

        Vector2 _shotFocus;
        float _shotOrtho;
        float _shotSetAt = float.NegativeInfinity;
        bool _shotCut;

        /// <summary>
        /// Ask for a framing this frame. Re-request every frame to hold it; stop
        /// requesting (or call <see cref="ClearShot"/>) to hand the camera back
        /// to the default ball-follow behaviour.
        /// </summary>
        /// <param name="focus">World point to centre on, before the pan clamp.</param>
        /// <param name="orthoSize">Requested half-height; clamped to the pitch.</param>
        /// <param name="cut">
        /// True on the first frame of a hard cut: position and zoom snap instead
        /// of easing, which is what makes a camera change read as an edit rather
        /// than a swoop.
        /// </param>
        public void RequestShot(Vector2 focus, float orthoSize, bool cut = false)
        {
            _shotFocus = focus;
            _shotOrtho = Mathf.Max(1f, orthoSize);
            _shotSetAt = Time.unscaledTime;
            if (cut) _shotCut = true;
        }

        public void ClearShot() => _shotSetAt = float.NegativeInfinity;

        /// <summary>
        /// True while a shot request is still fresh enough to be honoured. Public
        /// because "did the shot expire" is otherwise unobservable from outside -
        /// the visible effect is a framing that may coincidentally match whatever
        /// the undirected rig would have chosen, which makes a test of the timeout
        /// either flaky or vacuous.
        /// </summary>
        public bool ShotActive => Time.unscaledTime - _shotSetAt <= SHOT_TIMEOUT;

        /// <summary>Wide baseline for the current pitch and aspect - the caller's zoom reference.</summary>
        public float CurrentWideOrtho
        {
            get
            {
                if (_cam == null || _env == null) return 10f;
                return PitchWideOrthoSize(_env.PitchHalfExtents + Vector2.one * _edgeMargin);
            }
        }

        void Awake()
        {
            _cam = Camera.main;
            _env = FindAnyObjectByType<Agent_EnvController>();
            _hud = FindAnyObjectByType<Agent_HUD>();
            _wide = true;
            _wideUntilTime = Time.time + _kickoffHoldSeconds;
            if (_env != null) _env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            _wide = true;
            _wideUntilTime = Time.time + _goalHoldSeconds;
            // The ball is about to be teleported to the centre spot. A lead built
            // from the shot that scored would otherwise be carried onto a
            // stationary ball and decay there as a visible drift.
            _lookAhead = Vector2.zero;
        }

        // ── The HUD inset ───────────────────────────────────────────────────
        //
        // THE PLAY AREA IS NOT THE VIEWPORT. Agent_HUD's own docstring says its
        // bands sit "above and below the pitch" so that "nothing ever covers the
        // play area". That was true of the authored 36 x 54 pitch, which this
        // camera letterboxed. It stopped being true the day the wide shot was
        // changed to fill the portrait viewport top to bottom: the bands became an
        // OVERLAY on a full-screen pitch.
        //
        // Measured consequence at 2v2 (12.6 x 25.3 m pitch, wide ortho 13.15): the
        // top band covers ~1.9 m of the view and the bottom band ~3.9 m, while the
        // goal lines sit at +/-12.65 against a 13.15 half-height - so BOTH GOAL
        // MOUTHS, the only two places a goal can happen, were behind the scoreboard
        // and the chip row. Nothing threw and no test saw it, because the portrait
        // tests assert the pitch fills the SCREEN, which is exactly the wrong
        // invariant once part of the screen is opaque.
        //
        // So the pitch is framed into the CLEAR BAND between the two HUD bands, and
        // the camera is offset so that band - not the viewport - is centred on the
        // pitch. Both numbers are read live every frame: they move with the safe
        // area, the type scale, the squad size (the chip row grows) and whether the
        // broadcast strip is up.

        /// <summary>
        /// Floor on the clear fraction. A HUD that somehow reported covering most of
        /// the screen would otherwise drive the zoom toward infinity; at 0.4 the worst
        /// case is a pitch framed 2.5x too wide, which is legible rather than absurd.
        /// </summary>
        const float MIN_CLEAR_FRACTION = 0.4f;

        /// <summary>Fraction of the viewport height that no HUD band covers, 0.4..1.</summary>
        public float ClearFraction => Mathf.Clamp(
            1f - TopInset01 - BottomInset01, MIN_CLEAR_FRACTION, 1f);

        float TopInset01 => _hud != null ? _hud.TopBandFraction : 0f;
        float BottomInset01 => _hud != null ? _hud.BottomBandFraction : 0f;

        /// <summary>
        /// Centre of the clear band in viewport coordinates (0 bottom, 1 top). Equals
        /// 0.5 when nothing is covered; the bands are asymmetric in practice (the
        /// bottom one carries the meter, the chips and the buttons) so this normally
        /// sits ABOVE centre, which pushes the camera down and the pitch up the screen.
        /// </summary>
        float ClearCentre01
        {
            get
            {
                float bottom = BottomInset01;
                float clear = ClearFraction;
                // Re-derive the top from the clamped clear fraction so the centre and
                // the height always describe the same band, even at the floor.
                return Mathf.Clamp01(bottom + clear * 0.5f);
            }
        }

        /// <summary>
        /// World-space Y offset from the focus point to the camera centre, for the
        /// given framing. Negative when the bottom band is the taller of the two.
        /// </summary>
        float FocusOffsetY(float orthoSize) => -(ClearCentre01 - 0.5f) * 2f * orthoSize;

        /// <summary>
        /// Smallest orthographic size showing the whole of <paramref name="half"/> at the
        /// current aspect, INSIDE the clear band. orthographicSize is the HALF height of
        /// the whole viewport, so the height constraint is divided by the clear fraction
        /// and the width constraint by the aspect. Max of the two = nothing gets cropped
        /// and nothing lands under a band. Used for the action framing, where either
        /// would hide a player.
        /// </summary>
        float FitOrthoSize(Vector2 half)
        {
            float aspect = _cam.aspect > 0.01f ? _cam.aspect : FALLBACK_ASPECT;
            return Mathf.Max(half.y / ClearFraction, half.x / aspect);
        }

        /// <summary>
        /// The wide establishing shot, which is allowed to crop the pitch's WIDTH.
        ///
        /// A pure fit was measured to frame badly on modern phones. It resolves to
        /// max(halfY, halfX / aspect), and at 18:9 and taller the width term wins, so the
        /// camera pulls back and the pitch shrinks - on a 20:9 screen that left ~2.5 world
        /// units of dead ground past each goal line and the pitch filling only ~84% of the
        /// height. The taller the phone, the worse it got, which is exactly backwards.
        ///
        /// Cropping width instead is safe here in a way cropping height would not be: the
        /// goal mouths are on the Y ends, the crop is symmetric about the centre line, and
        /// the pan clamp below already tracks the ball horizontally whenever the view is
        /// narrower than the pitch. So the tall-screen case degrades into a tracking shot
        /// rather than a letterbox.
        /// </summary>
        float PitchWideOrthoSize(Vector2 half) =>
            WideOrthoSize(half, _cam.aspect > 0.01f ? _cam.aspect : FALLBACK_ASPECT,
                _maxWidthCrop, ClearFraction);

        /// <summary>9:16, used when the camera reports no usable aspect yet.</summary>
        public const float FALLBACK_ASPECT = 0.5625f;

        /// <summary>The shipped value of <c>_maxWidthCrop</c>.</summary>
        public const float DEFAULT_MAX_WIDTH_CROP = 0.14f;

        /// <summary>The shipped value of <c>_edgeMargin</c>, in world units.</summary>
        public const float DEFAULT_EDGE_MARGIN = 0.5f;

        /// <summary>
        /// The wide establishing shot as pure arithmetic, so it can be swept
        /// across aspect ratios and squad sizes without a camera, a pitch or a
        /// play-mode frame.
        ///
        /// IT IS PUBLIC BECAUSE THE TEST USED TO RESTATE IT. Agent_PlayMode_Portrait
        /// carried its own `Mathf.Max(padded.y, padded.x * (1 - CROP) / aspect)`
        /// with a comment saying it "must match Agent_CameraFollow" - so the
        /// assertion held against a copy, and the one edit it exists to catch,
        /// a change to the real formula, would have left it green. Same shape of
        /// mistake as a shipped .onnx that loads against a changed observation
        /// contract: identical numbers, different meaning.
        /// </summary>
        /// <param name="paddedHalfExtents">Pitch half-extents INCLUDING the edge margin.</param>
        /// <param name="aspect">Viewport width / height.</param>
        /// <param name="maxWidthCrop">Fraction of pitch WIDTH the shot may crop.</param>
        /// <param name="clearFraction">
        /// Fraction of the viewport HEIGHT that no HUD band covers. The height fit is
        /// divided by it, because orthographicSize describes the whole viewport while
        /// the pitch may only use the clear part of it. Defaults to 1 - the
        /// no-HUD case, and what the existing three-argument callers mean.
        /// </param>
        public static float WideOrthoSize(Vector2 paddedHalfExtents, float aspect,
            float maxWidthCrop, float clearFraction = 1f)
        {
            if (aspect <= 0.01f) aspect = FALLBACK_ASPECT;
            clearFraction = Mathf.Clamp(clearFraction, MIN_CLEAR_FRACTION, 1f);
            float heightFit = paddedHalfExtents.y / clearFraction;
            float widthFit = paddedHalfExtents.x * (1f - maxWidthCrop) / aspect;
            return Mathf.Max(heightFit, widthFit);
        }

        void LateUpdate()
        {
            if (_cam == null || _env == null || _env.Ball == null) return;

            if (_wide && Time.time >= _wideUntilTime) _wide = false;

            Vector2 pitchHalf = _env.PitchHalfExtents + Vector2.one * _edgeMargin;
            float wideOrtho = PitchWideOrthoSize(pitchHalf);
            float tightestOrtho = wideOrtho * _tightestFraction;

            // The replay override retargets the camera; everything downstream
            // (smoothing, pan clamp) is shared with the live shot.
            bool overriding = _overrideTarget != null;
            // A focused player that has been destroyed (scene teardown, a squad
            // rebuilt under the camera) releases the lock by itself. Unity's ==
            // is what detects it; the assignment drops the dead reference.
            if (_focus == null) _focus = null;
            bool focused = !overriding && _focus != null;
            // Override outranks a director shot outright, so a replay taking the
            // camera mid-cut is never a tug of war. A stale shot is dropped.
            bool directed = !overriding && !focused && ShotActive;
            // Plain ball follow: the only mode the speed look-ahead applies to.
            // A shot already carries its own lead (Agent_Director adds one), a
            // focus is on a body rather than the ball, and an override is authored.
            bool following = !overriding && !focused && !directed;
            Vector2 ballPos = overriding
                ? (Vector2)_overrideTarget.position
                : focused ? (Vector2)_focus.position
                : directed ? _shotFocus
                : _env.Ball.position;
            float targetOrtho;

            if (overriding)
            {
                targetOrtho = Mathf.Clamp(_overrideOrtho, wideOrtho * 0.18f, wideOrtho);
            }
            else if (focused)
            {
                targetOrtho = Mathf.Clamp(wideOrtho * _focusFraction, tightestOrtho, wideOrtho);
            }
            else if (directed)
            {
                targetOrtho = Mathf.Clamp(_shotOrtho, tightestOrtho, wideOrtho);
            }
            else if (_wide)
            {
                targetOrtho = wideOrtho;
            }
            else
            {
                // Frame everything that matters: the ball and every player. Manual loop
                // rather than LINQ - this runs every frame (performance.md).
                float minX = ballPos.x, maxX = ballPos.x;
                float minY = ballPos.y, maxY = ballPos.y;
                var agents = _env.agents;
                for (int i = 0; i < agents.Count; i++)
                {
                    var a = agents[i];
                    if (a == null || a.Body == null) continue;
                    Vector2 p = a.Body.position;
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.y < minY) minY = p.y;
                    if (p.y > maxY) maxY = p.y;
                }

                // Half-extents of the action, measured from the camera's focus point (the
                // ball) so the framing stays ball-centred rather than drifting to the
                // bounding-box centre, which would fight the position follow below.
                float actionHalfX = Mathf.Max(Mathf.Abs(maxX - ballPos.x),
                                              Mathf.Abs(ballPos.x - minX)) + _actionPadding;
                float actionHalfY = Mathf.Max(Mathf.Abs(maxY - ballPos.y),
                                              Mathf.Abs(ballPos.y - minY)) + _actionPadding;
                float actionOrtho = FitOrthoSize(new Vector2(actionHalfX, actionHalfY));

                // A fast ball needs more lead room; a slow one means a scrum worth
                // filling the screen with.
                float speed01 = Mathf.Clamp01(_env.Ball.linearVelocity.magnitude / _speedForWide);
                targetOrtho = Mathf.Lerp(actionOrtho, wideOrtho, speed01);
                targetOrtho = Mathf.Clamp(targetOrtho, tightestOrtho, wideOrtho);
            }

            // The viewer's own zoom, on top of whichever framing won above. After
            // the per-mode clamps on purpose: those bound what the RIG may choose,
            // and this is a person overriding the rig. Floored at the same 18% of
            // wide the replay override uses, so no combination reaches a size the
            // pan clamp has never been run at.
            if (!overriding)
            {
                targetOrtho = Mathf.Max(wideOrtho * 0.18f, targetOrtho * _spectatorZoom);
            }

            // Frame-rate independent smoothing; easing out of the wide shot is a shade
            // slower than easing into it so goal replays do not snap.
            // A cut is the whole point of a shot grammar: an edit reads as an
            // edit because it is instantaneous. Consumed here so the snap lasts
            // exactly one frame no matter how many times the director asked.
            bool cutting = directed && _shotCut;
            _shotCut = false;

            float zoomK = overriding ? 7f : ((directed || focused) ? 5f : (_wide ? 4f : 3f));
            _cam.orthographicSize = cutting
                ? targetOrtho
                : Mathf.Lerp(_cam.orthographicSize, targetOrtho,
                    1f - Mathf.Exp(-zoomK * Time.unscaledDeltaTime));

            // Clamp so the view never pans past the pitch. This previously computed
            // pitchHalf and then clamped against the CAMERA's half-extents instead,
            // leaving the pitch bounds unused and letting the camera drift off the pitch.
            //
            // The VERTICAL clamp is against the CLEAR band's half-height, not the
            // camera's: the part of the view above the top band and below the bottom
            // one is not play area, so allowing the focus to travel into it would put
            // the ball back under a band at the ends of the pitch - the exact defect
            // the inset exists to remove.
            float camHalfH = _cam.orthographicSize;
            float camHalfW = camHalfH * _cam.aspect;
            float clearHalfH = camHalfH * ClearFraction;
            float panX = Mathf.Max(0f, pitchHalf.x - camHalfW);
            float panY = Mathf.Max(0f, pitchHalf.y - clearHalfH);

            // Speed look-ahead. Smoothed every frame whether or not it is applied,
            // so it decays to zero under a shot or a focus and eases back in
            // afterwards instead of resuming at whatever it was when it lost the
            // camera. Zero during the wide hold: the establishing shot is centred
            // by definition and there is no pan room to spend a lead on anyway.
            Vector2 wantedLead = following && _enableLookAhead && !_wide
                ? Vector2.ClampMagnitude(_env.Ball.linearVelocity * _lookAheadSeconds, _lookAheadMax)
                : Vector2.zero;
            _lookAhead = Vector2.Lerp(_lookAhead, wantedLead,
                1f - Mathf.Exp(-_lookAheadSmoothing * Time.unscaledDeltaTime));

            Vector2 target = ballPos;
            if (following)
            {
                // Capped per axis against the VISIBLE half-frame - the clear band
                // vertically, not the viewport - so the lead can never push the
                // ball itself under a HUD band or off the side of the screen.
                float leadX = camHalfW * _lookAheadFrameShare;
                float leadY = clearHalfH * _lookAheadFrameShare;
                target.x += Mathf.Clamp(_lookAhead.x, -leadX, leadX);
                target.y += Mathf.Clamp(_lookAhead.y, -leadY, leadY);
            }
            // The lead is added BEFORE the pan clamp, never after: the clamp is
            // what keeps the goal mouths out from under the bands, and a lead
            // applied on the far side of it would slide them straight back.
            target.x = Mathf.Clamp(target.x, -panX, panX);
            target.y = Mathf.Clamp(target.y, -panY, panY);
            // Shift the camera so the CLEAR band centres on the focus point. Applied
            // after the clamp, not before it: the clamp is a statement about where the
            // play area may sit, and the offset is how the rig achieves it.
            target.y += FocusOffsetY(camHalfH);

            Vector3 pos = _cam.transform.position;
            float followSpeed = overriding ? _followSpeed * 2f
                : (directed || focused) ? _followSpeed * 1.5f
                : _followSpeed;
            float t = cutting ? 1f : 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
            pos.x = Mathf.Lerp(pos.x, target.x, t);
            pos.y = Mathf.Lerp(pos.y, target.y, t);
            _cam.transform.position = pos;   // z untouched: never change camera depth
        }
    }
}
