using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// The post-match dossier: where each player actually spent the match, as a
    /// heatmap, plus the three scalars that fall out of it.
    ///
    /// WHY THIS EXISTS, IN THIS PROJECT SPECIFICALLY. CLAUDE.md's measured finding
    /// is that all four personalities are STATISTICALLY INDISTINGUISHABLE on win
    /// rate despite 12x different step counts - "which is itself the finding". Win
    /// rate is one number at the end of a thousand episodes and it has now failed
    /// four times to separate four brains that were trained on deliberately
    /// different reward mixes. Occupancy is a different measurement of the same
    /// match, and it is the one that SHOULD separate them: KIM carries
    /// defensivePositionScale and NICK carries possessionScale, so if those reward
    /// terms do anything at all they do it to where the body goes. A flat heatmap
    /// across all four would itself be a finding, and a sharper one than another
    /// win rate inside the noise band.
    ///
    /// EVERYTHING HERE IS MEASURED. Occupancy is the integral of dt over the cell
    /// the body was in, sampled at the physics rate; advancement is the mean of the
    /// body's own-goal-to-target-goal coordinate; width is the standard deviation
    /// of its lateral position. No model, no estimate, no weights - it sits on the
    /// same side of the end panel as Agent_MatchStats and deliberately not next to
    /// the win-probability strip.
    ///
    /// NORMALISED COORDINATES, NOT WORLD ONES. Cells are indexed from position
    /// divided by the LIVE half-extents, because <see cref="Agent_Overtime"/> moves
    /// the walls mid-match. A grid in metres would smear a player's second half
    /// across different cells than its first for no reason the player did anything
    /// about.
    ///
    /// SAMPLED IN FixedUpdate for the reason Agent_MatchStats gives: occupancy at
    /// 60 fps and occupancy at 144 fps have to be the same number.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Dossier : MonoBehaviour
    {
        [Header("Grid")]
        [Tooltip("Cells across the pitch width.")]
        [SerializeField] private int _columns = 12;
        [Tooltip("Cells along the pitch length. Twice the column count matches the 2:1 " +
                 "futsal ratio Agent_PitchSizing holds, so cells stay square.")]
        [SerializeField] private int _rows = 24;

        [Header("Card")]
        [Tooltip("Heatmap tile width in the 1080x1920 reference space. Four tiles plus " +
                 "gutters must fit 1080.")]
        [SerializeField] private int _tileWidth = 210;
        [Tooltip("Seconds of tracked play below which no card is shown. A heatmap of four " +
                 "seconds is noise wearing a broadcast font.")]
        [SerializeField] private float _minimumSeconds = 10f;
        [SerializeField] private bool _enableDossier = true;

        /// <summary>
        /// One player's occupancy record. A class rather than a struct because it
        /// owns the grid array; allocated once per player on first sight.
        /// </summary>
        sealed class Track
        {
            public float[] Cells;
            public float Seconds;
            public float PeakCell;

            /// <summary>Sum of the attack-relative long-axis coordinate, -1 (own goal) .. +1 (target goal).</summary>
            public float AdvanceSum;
            /// <summary>Sum and sum-of-squares of normalised lateral position, for the spread.</summary>
            public float LateralSum;
            public float LateralSquareSum;
            /// <summary>Seconds spent in the attacking half.</summary>
            public float AttackingSeconds;

            public Track(int cells) => Cells = new float[cells];

            public float Advancement => Seconds > 0.01f ? AdvanceSum / Seconds : 0f;

            public float AttackingShare => Seconds > 0.01f ? AttackingSeconds / Seconds : 0f;

            /// <summary>Standard deviation of normalised lateral position, 0 (a rail) .. ~0.58 (uniform).</summary>
            public float Width
            {
                get
                {
                    if (Seconds <= 0.01f) return 0f;
                    float mean = LateralSum / Seconds;
                    float variance = LateralSquareSum / Seconds - mean * mean;
                    return variance > 0f ? Mathf.Sqrt(variance) : 0f;
                }
            }
        }

        Agent_EnvController _env;
        Agent_HUD _hud;

        readonly Dictionary<Agent_Soccer, Track> _tracks = new();
        readonly List<Texture2D> _textures = new();
        Color32[] _pixels;

        /// <summary>Seconds of play the dossier has tracked. Read by the tests.</summary>
        public float TrackedSeconds { get; private set; }

        /// <summary>Mean attack-relative position, -1 (own goal line) to +1 (target goal line).</summary>
        public float AdvancementOf(Agent_Soccer agent) =>
            agent != null && _tracks.TryGetValue(agent, out var track) ? track.Advancement : 0f;

        /// <summary>Fraction of the match spent in the attacking half.</summary>
        public float AttackingShareOf(Agent_Soccer agent) =>
            agent != null && _tracks.TryGetValue(agent, out var track) ? track.AttackingShare : 0f;

        /// <summary>Occupancy grid for one player, row-major from the own-goal end. Null before its first tick.</summary>
        public float[] GridOf(Agent_Soccer agent) =>
            agent != null && _tracks.TryGetValue(agent, out var track) ? track.Cells : null;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableDossier || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _columns = Mathf.Max(2, _columns);
            _rows = Mathf.Max(2, _rows);
            _pixels = new Color32[_columns * _rows];

            if (_hud != null) _hud.AddEndPanelSection(BuildDossierCard);
        }

        void OnDestroy()
        {
            if (_hud != null) _hud.RemoveEndPanelSection(BuildDossierCard);
            // The card's tiles are runtime textures with no asset behind them, so
            // nothing else will ever collect them.
            for (int i = 0; i < _textures.Count; i++)
            {
                if (_textures[i] == null) continue;
                if (Application.isPlaying) Destroy(_textures[i]);
                else DestroyImmediate(_textures[i]);
            }
            _textures.Clear();
        }

        void FixedUpdate()
        {
            if (_env == null) return;
            // A paused match accrues no occupancy, for the same reason it accrues no
            // possession in Agent_MatchStats: a long look at the pause menu would
            // otherwise hand the heatmap to whoever happened to be standing still.
            if (Agent_TimeFreeze.IsFrozen) return;

            Vector2 half = _env.PitchHalfExtents;
            if (half.x < 0.01f || half.y < 0.01f) return;

            float dt = Time.fixedDeltaTime;
            TrackedSeconds += dt;
            Vector2 centre = transform.position;

            var roster = _env.agents;
            for (int i = 0; i < roster.Count; i++)
            {
                var agent = roster[i];
                if (agent == null || agent.Body == null || !agent.isActiveAndEnabled) continue;

                if (!_tracks.TryGetValue(agent, out var track))
                {
                    track = new Track(_columns * _rows);
                    _tracks[agent] = track;
                }

                Vector2 local = agent.Body.position - centre;
                float lateral = Mathf.Clamp(local.x / half.x, -1f, 1f);

                // Attack-relative long axis. Blue spawns in the -y half and attacks
                // +y (Agent_EnvController.ResetPitch), so Red's axis is mirrored -
                // which is what makes two players on opposite sides comparable at
                // all. Without it, "advanced" would mean the opposite thing per team.
                float forward = Mathf.Clamp(local.y / half.y, -1f, 1f);
                if (agent.team == Agent_Soccer.Team.Red) forward = -forward;

                int column = Mathf.Clamp(Mathf.FloorToInt((lateral + 1f) * 0.5f * _columns), 0, _columns - 1);
                int row = Mathf.Clamp(Mathf.FloorToInt((forward + 1f) * 0.5f * _rows), 0, _rows - 1);

                int cell = row * _columns + column;
                track.Cells[cell] += dt;
                if (track.Cells[cell] > track.PeakCell) track.PeakCell = track.Cells[cell];

                track.Seconds += dt;
                track.AdvanceSum += forward * dt;
                track.LateralSum += lateral * dt;
                track.LateralSquareSum += lateral * lateral * dt;
                if (forward > 0f) track.AttackingSeconds += dt;
            }
        }

        // -- End-of-match card -----------------------------------------------

        /// <summary>
        /// One tile per player plus the three scalars under each. Built once, at the
        /// end, where there is room for it - the same division of labour
        /// Agent_MatchStats uses between its mid-match ticker and its end table.
        /// </summary>
        VisualElement BuildDossierCard()
        {
            if (_env == null || TrackedSeconds < _minimumSeconds) return null;

            var roster = _env.agents;
            var card = new VisualElement();
            card.AddToClassList("card");

            var heading = new Label("WHERE THEY PLAYED");
            heading.AddToClassList("card__heading");
            card.Add(heading);

            var strip = new VisualElement();
            strip.style.flexDirection = FlexDirection.Row;
            strip.style.justifyContent = Justify.Center;
            strip.style.flexWrap = Wrap.Wrap;
            card.Add(strip);

            int drawn = 0;
            for (int i = 0; i < roster.Count; i++)
            {
                var agent = roster[i];
                if (agent == null || !_tracks.TryGetValue(agent, out var track)) continue;
                if (track.Seconds < 0.01f) continue;

                strip.Add(BuildTile(agent, track));
                drawn++;
            }
            if (drawn == 0) return null;

            var caption = new Label("occupancy integrated at the physics rate  ·  " +
                                    "each map is drawn attacking upward  ·  measured, not modelled");
            caption.AddToClassList("card__tile-caption");
            card.Add(caption);

            return card;
        }

        VisualElement BuildTile(Agent_Soccer agent, Track track)
        {
            var column = new VisualElement();
            column.style.alignItems = Align.Center;
            column.style.marginLeft = 8;
            column.style.marginRight = 8;
            column.style.marginTop = 12;

            var name = new Label(NameOf(agent));
            name.style.fontSize = Agent_UIStyle.FontXS;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.color = Agent_SoccerView.TeamColor(agent.team);
            column.Add(name);

            var map = new VisualElement();
            map.style.width = _tileWidth;
            // Height from the grid's own aspect, so changing _rows cannot silently
            // stretch the map into a shape the pitch never had.
            map.style.height = _tileWidth * _rows / Mathf.Max(1, _columns);
            map.style.marginTop = 6;
            map.style.backgroundColor = new Color(1f, 1f, 1f, 0.06f);
            map.style.backgroundImage = new StyleBackground(BuildTexture(agent, track));
            Agent_UIStyle.Round(map, 10);
            column.Add(map);

            var figures = new Label(
                $"adv {track.Advancement:+0.00;-0.00}\n" +
                $"att {track.AttackingShare * 100f:0}%\n" +
                $"width {track.Width:0.00}");
            figures.style.fontSize = Agent_UIStyle.FontXS;
            figures.style.color = Agent_UIStyle.TextMuted;
            figures.style.unityTextAlign = TextAnchor.MiddleCenter;
            figures.style.marginTop = 6;
            column.Add(figures);

            return column;
        }

        /// <summary>
        /// The heatmap itself: the player's own colour at an alpha proportional to
        /// dwell time, normalised by that player's own peak cell.
        ///
        /// PER-PLAYER NORMALISATION IS DELIBERATE. A shared scale would render the
        /// quiet player as a nearly blank tile, which reads as "no data" rather than
        /// as "spread out" - and spread is exactly what this card exists to show.
        /// The absolute figures underneath carry the comparison instead.
        ///
        /// Row 0 is the own-goal end and the texture's row 0 is its bottom, so the
        /// map already reads attacking-upward with no flip.
        /// </summary>
        Texture2D BuildTexture(Agent_Soccer agent, Track track)
        {
            var texture = new Texture2D(_columns, _rows, TextureFormat.RGBA32, mipChain: false)
            {
                name = $"Dossier_{NameOf(agent)}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            Color team = Agent_SoccerView.TeamColor(agent.team);
            float peak = Mathf.Max(0.0001f, track.PeakCell);

            for (int i = 0; i < track.Cells.Length && i < _pixels.Length; i++)
            {
                // Square root rather than linear: dwell time is heavily skewed toward
                // the few cells a player loiters in, and a linear ramp renders every
                // other cell as black - losing the shape of the coverage, which is
                // the thing being looked at.
                float intensity = Mathf.Sqrt(Mathf.Clamp01(track.Cells[i] / peak));
                _pixels[i] = new Color(team.r, team.g, team.b, intensity);
            }

            texture.SetPixels32(_pixels);
            texture.Apply(updateMipmaps: false);
            _textures.Add(texture);
            return texture;
        }

        static string NameOf(Agent_Soccer agent)
        {
            if (agent == null) return "-";
            return agent.rewards != null ? agent.rewards.playerName : agent.brainName;
        }
    }
}
