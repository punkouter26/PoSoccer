using System.Collections.Generic;
using Unity.MLAgents.Policies;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// The checkpoint gallery: a grid of pitches, each running one brain against
    /// the same rule-based bot, with that brain's provenance captioned under it.
    ///
    /// THIS IS THE PROJECT'S OWN STORY, AND NOBODY COULD SEE IT. CLAUDE.md carries
    /// a table showing a 10.44 m chase covered 0.99 m at p17, 3.61 m at p18,
    /// 5.24 m at p20 and 8.48 m at p21 - an 8.6x improvement in locomotion and the
    /// only run in the project's history to break a 16-17% win-rate plateau. Every
    /// one of those numbers was produced headless, read out of a console, and
    /// written into a markdown file. The thing they describe has never been
    /// watched. Put four of them on one screen and the argument stops needing a
    /// table.
    ///
    /// IT WORKS BEFORE ANYTHING IS ARCHIVED. With no Agent_Checkpoint assets it
    /// falls back to the live roster - STANDARD at 10.0M steps, NICK at 7.0M, KIM
    /// at 2.0M, all from the p21 family - which is a real and interesting exhibit
    /// on its own: four brains, four step counts, one bot. Authoring checkpoints
    /// turns it into the training arc.
    ///
    /// WHAT IT DELIBERATELY DOES NOT DO:
    ///  - it does not grade anything. Four pitches playing for a minute is a
    ///    demonstration, not a measurement, and this codebase has already
    ///    retracted one result that came from treating a small sample as evidence.
    ///    The win rates in the captions come from evaluate.ps1 runs, with their
    ///    episode counts shown next to them, and are the model's record - not
    ///    something happening on screen;
    ///  - it does not run the match flow. No score, no first-to-5, no replay, no
    ///    director. Those components own global state (the clock, the camera) and
    ///    four copies of them would fight. Agent_Presentation.InstallGallery adds
    ///    only the per-pitch visual layer.
    ///
    /// Entered from the menu, which sets Agent_MatchSetup.GalleryMode and loads
    /// the exhibition scene. Cloning is the same trick Agent_TrainingGrid uses.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Gallery : MonoBehaviour
    {
        [Tooltip("Pitches per row.")]
        [SerializeField] private int _columns = 2;
        [Tooltip("Extra world-space gap between pitch bounding boxes.")]
        [SerializeField] private float _gap = 6f;
        [Tooltip("Hard cap on exhibits. Each pitch is a full physics environment with its " +
                 "own agents and its own inference, so this is a frame-time budget, not a " +
                 "layout preference.")]
        [SerializeField] private int _maxExhibits = 6;
        [Tooltip("World margin left around the grid when framing the camera.")]
        [SerializeField] private float _cameraMargin = 3f;

        // _exhibits is what was ASKED for; _pitches and _placed are what was
        // actually built, and they are index-aligned with each other by
        // construction - appended in the same statement, never separately.
        //
        // Captioning off _exhibits[i] against _pitches[i] instead would be correct
        // right up until one clone failed to produce an EnvController, at which
        // point every caption after it would sit under the wrong pitch and label a
        // brain that is not playing there. That is a wrong number presented
        // confidently, which is the failure mode this project keeps writing
        // retractions about - so the alignment is structural rather than assumed.
        readonly List<Agent_Checkpoint> _exhibits = new();
        readonly List<Agent_EnvController> _pitches = new();
        readonly List<Agent_Checkpoint> _placed = new();
        readonly List<Label> _captions = new();

        Agent_EnvController _env;
        Agent_HUD _hud;
        Camera _camera;
        VisualElement _captionLayer;

        // Checkpoints synthesised from live profiles when nothing is archived.
        // Held so they can be destroyed with the scene rather than leaked.
        readonly List<Agent_Checkpoint> _synthetic = new();

        void Awake()
        {
            _env = GetComponent<Agent_EnvController>();

            if (!Agent_Presentation.IsGalleryScene())
            {
                enabled = false;
                return;
            }

            ResolveExhibits();
        }

        void Start()
        {
            if (!enabled || _exhibits.Count == 0) return;

            _hud = FindFirstObjectByType<Agent_HUD>();
            _camera = Camera.main;

            BuildGrid();
            FrameCamera();
            BuildCaptions();
        }

        void OnDestroy()
        {
            for (int i = 0; i < _synthetic.Count; i++)
            {
                if (_synthetic[i] != null) Destroy(_synthetic[i]);
            }
            if (_captionLayer != null) _captionLayer.RemoveFromHierarchy();
        }

        void LateUpdate()
        {
            // Captions track their pitch through the panel's own projection, so a
            // resolution change, an orientation change or a camera nudge cannot
            // leave them stranded. Cheap: at most _maxExhibits of them.
            for (int i = 0; i < _captions.Count && i < _pitches.Count; i++)
            {
                PositionCaption(_captions[i], _pitches[i]);
            }
        }

        // -- Exhibits --------------------------------------------------------

        void ResolveExhibits()
        {
            var entries = Agent_MatchSetup.GalleryEntries;
            if (entries != null)
            {
                for (int i = 0; i < entries.Length && _exhibits.Count < _maxExhibits; i++)
                {
                    if (entries[i] != null) _exhibits.Add(entries[i]);
                }
            }

            if (_exhibits.Count > 0) return;

            // Nothing archived: exhibit the live roster instead. Synthesised in
            // memory rather than written to disk, because an asset the user did
            // not author is an asset the user has to clean up.
            var profiles = Agent_MatchSetup.GalleryProfiles;
            if (profiles != null)
            {
                AddProfiles(profiles);
                return;
            }

            // Direct scene load, no menu. THE OBVIOUS FALLBACK IS THE WRONG ONE:
            // Agent_EnvController.profileRoster is wired in SCN_Training and is
            // literally `profileRoster: []` in SCN_Exhibition, so reading only
            // that produced an empty gallery with no error - and, worse, a
            // PlayMode test that skipped itself and reported green. The
            // exhibition scene's real roster is the match loader's serialized
            // default slots, so try both and take whatever has brains in it.
            AddProfiles(_env.profileRoster);
            if (_exhibits.Count > 0) return;

            var loader = FindFirstObjectByType<Agent_MatchLoader>();
            if (loader == null) return;
            AddProfiles(loader.defaultBlue, loader.defaultBlue2,
                        loader.defaultRed, loader.defaultRed2);
        }

        /// <summary>
        /// Wraps each trained profile in a synthetic checkpoint. Skips anything
        /// untrained (nothing to exhibit) and anything already added, since the
        /// loader's four default slots routinely name the same profile twice.
        /// </summary>
        void AddProfiles(params Reward_Settings[] profiles)
        {
            if (profiles == null) return;

            for (int i = 0; i < profiles.Length && _exhibits.Count < _maxExhibits; i++)
            {
                var profile = profiles[i];
                if (profile == null || profile.brainModel == null) continue;
                if (AlreadyExhibited(profile)) continue;

                var checkpoint = ScriptableObject.CreateInstance<Agent_Checkpoint>();
                checkpoint.baseProfile = profile;
                checkpoint.label = profile.playerName;
                checkpoint.trainedOn = profile.trainedOn;
                _synthetic.Add(checkpoint);
                _exhibits.Add(checkpoint);
            }
        }

        bool AlreadyExhibited(Reward_Settings profile)
        {
            for (int i = 0; i < _exhibits.Count; i++)
            {
                if (_exhibits[i] != null && _exhibits[i].baseProfile == profile) return true;
            }
            return false;
        }

        // -- Grid ------------------------------------------------------------

        void BuildGrid()
        {
            Place(_env, _exhibits[0]);

            Vector2 half = _env.PitchHalfExtents;
            Vector2 spacing = new(half.x * 2f + _gap, half.y * 2f + _gap);
            Vector3 origin = transform.position;

            for (int i = 1; i < _exhibits.Count; i++)
            {
                int column = i % _columns;
                int row = i / _columns;
                Vector3 offset = new(column * spacing.x, -row * spacing.y, 0f);

                var clone = Instantiate(gameObject, origin + offset, transform.rotation);
                clone.name = $"Pitch_{_exhibits[i].ResolvedLabel}";

                // The clone carries a copy of THIS component, which would clone
                // again, and so on. Disabled AND destroyed: Destroy is deferred to
                // the end of the frame, and the disable is what guarantees the
                // clone's Start never runs in the meantime.
                var galleryOnClone = clone.GetComponent<Agent_Gallery>();
                if (galleryOnClone != null)
                {
                    galleryOnClone.enabled = false;
                    Destroy(galleryOnClone);
                }

                var cloneEnv = clone.GetComponent<Agent_EnvController>();
                if (cloneEnv == null) continue;

                Place(cloneEnv, _exhibits[i]);
            }

            // DELIBERATELY NOT RECENTRED. The obvious next step is to shift every
            // pitch so the grid straddles the origin, and it is a trap: the
            // original pitch's Agent_EnvController.Start has already run (it is at
            // execution order -50, this is at 0) and cached every spawn position
            // and the ball spawn in WORLD coordinates. Moving its root afterwards
            // would leave the first pitch resetting its players and ball to where
            // it used to be, one pitch away, on the first goal.
            //
            // Clones are safe from this - their Start has not run when they are
            // positioned - but that asymmetry is exactly the kind of thing that
            // reads as "the gallery is broken but only the first one". So the grid
            // simply grows right and down from wherever the authored pitch sits,
            // and FrameCamera centres the view on the real bounds instead.
        }

        /// <summary>
        /// Records a pitch and the brain it is showing as one entry, then
        /// configures it. The only place either list is appended to, which is what
        /// makes their alignment a property of the code rather than a convention.
        /// </summary>
        void Place(Agent_EnvController pitch, Agent_Checkpoint checkpoint)
        {
            _pitches.Add(pitch);
            _placed.Add(checkpoint);
            ApplyExhibit(pitch, checkpoint);
        }

        /// <summary>
        /// Blue plays the exhibited brain, red plays the bot. Every pitch faces
        /// the identical opponent, which is the only thing that makes two pitches
        /// worth putting next to each other.
        /// </summary>
        void ApplyExhibit(Agent_EnvController pitch, Agent_Checkpoint checkpoint)
        {
            var model = checkpoint.ResolvedModel;
            var profile = checkpoint.baseProfile;
            var opponent = Agent_MatchSetup.GalleryOpponent;

            // A clone is configured the moment Instantiate returns, which is
            // before its own Agent_EnvController.Start has run. The list is
            // serialized so it normally arrives already populated, but a scene
            // that left it empty would self-discover only in that Start - so
            // discover here too rather than silently configuring nobody.
            if (pitch.agents.Count == 0)
            {
                pitch.agents.AddRange(pitch.GetComponentsInChildren<Agent_Soccer>());
            }

            var agents = pitch.agents;
            for (int i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                if (agent == null) continue;

                var behavior = agent.GetComponent<BehaviorParameters>();
                if (behavior == null) continue;

                if (agent.team == Agent_Soccer.Team.Blue)
                {
                    if (profile != null)
                    {
                        agent.rewards = profile;
                        agent.brainName = profile.playerName;
                    }
                    if (model != null)
                    {
                        // The Model setter calls UpdateAgentPolicy, so this is a
                        // supported live swap rather than a field poke - which
                        // matters because the clone's Agent has already run Awake
                        // by the time Instantiate returns.
                        behavior.Model = model;
                        behavior.BehaviorType = BehaviorType.InferenceOnly;
                    }
                }
                else
                {
                    if (opponent != null)
                    {
                        agent.rewards = opponent;
                        agent.brainName = opponent.playerName;
                    }

                    // ORDER IS LOAD-BEARING. Both of these setters call
                    // BehaviorParameters.UpdateAgentPolicy, and building an
                    // InferenceOnly policy with no model throws
                    // UnityAgentsException outright. So the type has to drop to
                    // HeuristicOnly BEFORE the model is cleared, or the agent this
                    // pitch arrived carrying an inference policy - which is exactly
                    // what Agent_MatchLoader hands us whenever the red slot names a
                    // trained profile - dies on the way to becoming the bot.
                    behavior.BehaviorType = BehaviorType.HeuristicOnly;
                    behavior.Model = null;

                    var bot = agent.GetComponent<Agent_HeuristicBot>();
                    if (bot != null) bot.enabled = true;
                }
            }
        }

        // -- Camera ----------------------------------------------------------

        /// <summary>
        /// Static framing over the whole grid. Agent_Bootstrap does not attach
        /// Agent_CameraFollow in gallery mode, so nothing is fighting for the
        /// transform here - there is no ball to follow when there are six.
        /// </summary>
        void FrameCamera()
        {
            if (_camera == null || _pitches.Count == 0) return;

            Vector2 half = _env.PitchHalfExtents;
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;

            for (int i = 0; i < _pitches.Count; i++)
            {
                if (_pitches[i] == null) continue;
                Vector2 centre = _pitches[i].transform.position;
                minX = Mathf.Min(minX, centre.x - half.x);
                maxX = Mathf.Max(maxX, centre.x + half.x);
                minY = Mathf.Min(minY, centre.y - half.y);
                maxY = Mathf.Max(maxY, centre.y + half.y);
            }
            if (float.IsPositiveInfinity(minX)) return;

            // Extra room at the bottom of each pitch for its caption.
            float spanX = (maxX - minX) * 0.5f + _cameraMargin;
            float spanY = (maxY - minY) * 0.5f + _cameraMargin * 1.6f;
            float aspect = _camera.aspect > 0.01f ? _camera.aspect : 0.5625f;

            _camera.orthographic = true;
            _camera.orthographicSize = Mathf.Max(spanY, spanX / aspect);

            Vector3 position = _camera.transform.position;
            position.x = (minX + maxX) * 0.5f;
            position.y = (minY + maxY) * 0.5f;
            _camera.transform.position = position;   // z untouched: never change depth
        }

        // -- Captions --------------------------------------------------------

        void BuildCaptions()
        {
            VisualElement root = _hud != null ? _hud.OverlayRoot : null;
            if (root == null) return;

            _captionLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            _captionLayer.style.position = Position.Absolute;
            _captionLayer.style.left = 0;
            _captionLayer.style.right = 0;
            _captionLayer.style.top = 0;
            _captionLayer.style.bottom = 0;
            root.Add(_captionLayer);

            for (int i = 0; i < _pitches.Count; i++)
            {
                var caption = new Label(CaptionText(_placed[i]))
                {
                    pickingMode = PickingMode.Ignore,
                };
                caption.style.position = Position.Absolute;
                caption.style.fontSize = Agent_UIStyle.FontXS;
                caption.style.color = Agent_UIStyle.TextPrimary;
                caption.style.unityTextAlign = TextAnchor.MiddleCenter;
                caption.style.unityFontStyleAndWeight = FontStyle.Bold;
                caption.style.whiteSpace = WhiteSpace.Normal;
                caption.style.backgroundColor = Agent_UIStyle.PanelBg;
                Agent_UIStyle.Round(caption, 10);
                Agent_UIStyle.PadAll(caption, 10);

                _captionLayer.Add(caption);
                _captions.Add(caption);
            }
        }

        /// <summary>
        /// Label, run, steps, and the graded win rate WITH its episode count -
        /// because a win rate without a sample size is the exact shape of the
        /// claim this project has had to retract before.
        /// </summary>
        static string CaptionText(Agent_Checkpoint checkpoint)
        {
            string steps = Agent_PlayerCard.FormatSteps(checkpoint.ResolvedSteps);
            float rate = checkpoint.ResolvedWinRate;
            int episodes = checkpoint.ResolvedEpisodes;

            string record = rate < 0f
                ? "ungraded"
                : episodes > 0
                    ? $"{rate * 100f:0.0}% wins · n={episodes}"
                    : $"{rate * 100f:0.0}% wins";

            string text = $"{checkpoint.ResolvedLabel}  ·  {steps}  ·  {record}";

            string run = checkpoint.ResolvedRunId;
            if (!string.IsNullOrEmpty(run)) text += $"\n{run}";
            if (!string.IsNullOrEmpty(checkpoint.notes)) text += $"\n{checkpoint.notes}";

            // The one warning that has to be impossible to miss.
            if (checkpoint.IsPreBodyFrame)
            {
                text += "\n⚠ pre-body-frame · not comparable";
            }
            return text;
        }

        void PositionCaption(Label caption, Agent_EnvController pitch)
        {
            // panel is null until the element has been attached and laid out, and
            // RuntimePanelUtils throws on a null panel rather than returning zero.
            if (caption == null || caption.panel == null || pitch == null || _camera == null) return;

            Vector3 anchor = pitch.transform.position
                             + new Vector3(0f, -pitch.PitchHalfExtents.y - 1.2f, 0f);

            // World -> panel coordinates. Screen-space maths would be wrong here:
            // the panel is ScaleWithScreenSize at a 1080-wide reference, so screen
            // pixels and panel pixels differ by the scale factor on every device
            // that is not exactly 1080 wide.
            Vector2 panelPoint = RuntimePanelUtils.CameraTransformWorldToPanel(
                caption.panel, anchor, _camera);

            float width = caption.resolvedStyle.width;
            float height = caption.resolvedStyle.height;
            caption.style.left = panelPoint.x - (float.IsNaN(width) ? 0f : width * 0.5f);
            caption.style.top = panelPoint.y - (float.IsNaN(height) ? 0f : height * 0.5f);
        }
    }
}
