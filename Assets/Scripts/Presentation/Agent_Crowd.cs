using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Tilemaps;

namespace PoSoccer
{
    /// <summary>
    /// Stadium dressing built at runtime: a tilemapped crowd ring, advertising
    /// boards along the touchline, and a pool of camera-flash lights that pop in
    /// the stands and spill onto the pitch.
    ///
    /// SIZED FOR WHAT THE CAMERA ACTUALLY SHOWS. Agent_CameraFollow derives its
    /// wide shot as the smallest orthographic size that fits the whole pitch, and
    /// on a 9:16 portrait viewport that resolves to "fit the pitch height" - which
    /// leaves roughly half a world unit of margin past the goal lines and about one
    /// past the touchlines. Deep stands would therefore be invisible in every
    /// shot. So the budget goes where it is seen: the boards sit hard against the
    /// touchline, the stands are shallow, and the spectacle is carried by
    /// flashbulbs, whose light reaches the pitch even when their source does not.
    ///
    /// Both tilemaps are one draw call each and the crowd shimmer walks a
    /// contiguous cursor rather than scattering random cells, so it dirties one
    /// tilemap chunk per frame instead of many.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Crowd : MonoBehaviour
    {
        [Tooltip("World size of one crowd cell.")]
        [SerializeField] private float _cellSize = 0.5f;
        [Tooltip("Depth of the crowd ring outside the boards, in world units. Kept " +
                 "shallow on purpose: the portrait camera leaves roughly half a world " +
                 "unit of margin past the goal line, so deeper rows are built and " +
                 "shimmered every frame for pixels that are never on screen.")]
        [SerializeField] private float _standsDepth = 1.5f;
        [Tooltip("Gap between the touchline and the advertising boards.")]
        [SerializeField] private float _boardGap = 0.35f;
        [Tooltip("Thickness of the advertising board ring.")]
        [SerializeField] private float _boardDepth = 0.5f;
        [Tooltip("Fraction of crowd cells occupied by a spectator; the rest read as empty seats.")]
        [Range(0.2f, 1f)] [SerializeField] private float _occupancy = 0.62f;
        [Tooltip("Number of pooled camera-flash lights.")]
        [SerializeField] private int _flashbulbs = 10;
        [Tooltip("Flashes per second across the whole crowd during normal play.")]
        [SerializeField] private float _flashRate = 1.6f;
        [Tooltip("Cells recoloured per frame to keep the crowd alive. 0 disables the " +
                 "shimmer entirely - with a shallow stand ring the movement is barely " +
                 "visible, and every write dirties a tilemap chunk for a mesh rebuild.")]
        [SerializeField] private int _shimmerPerFrame = 4;
        [SerializeField] private bool _enableCrowd = true;

        static readonly Color[] SpectatorTones =
        {
            new(0.82f, 0.65f, 0.52f), new(0.62f, 0.45f, 0.35f), new(0.42f, 0.30f, 0.24f),
            new(0.90f, 0.76f, 0.63f), new(0.55f, 0.58f, 0.66f), new(0.70f, 0.72f, 0.78f),
        };

        static readonly Color EmptySeat = new(0.10f, 0.13f, 0.16f);

        Agent_EnvController _env;
        Tilemap _stands;
        Tile _tile;

        readonly List<Vector3Int> _cells = new();
        readonly List<Vector3> _cellWorld = new();
        readonly List<Color> _cellBase = new();
        int _shimmerCursor;

        struct Flashbulb
        {
            public Light2D Light;
            public float Life;
            public float Cooldown;
        }

        Flashbulb[] _bulbs;
        float _flashBoost;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindFirstObjectByType<Agent_HUD>();

            if (!_enableCrowd || !Agent_Presentation.IsMatchScene(hud))
            {
                enabled = false;
                return;
            }

            // Start, not Awake: Agent_MatchLoader resizes the pitch per squad size
            // from its own Awake, so the extents are only final by the time Start runs.
            Build();
            _env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
        }

        // -- Construction ----------------------------------------------------

        void Build()
        {
            Vector2 pitch = _env.PitchHalfExtents;
            Vector2 boardInner = pitch + Vector2.one * _boardGap;
            Vector2 boardOuter = boardInner + Vector2.one * _boardDepth;
            Vector2 standsInner = boardOuter + Vector2.one * 0.3f;
            Vector2 standsOuter = standsInner + Vector2.one * _standsDepth;

            var gridGo = new GameObject("Stadium_Crowd");
            gridGo.transform.SetParent(transform, false);
            var grid = gridGo.AddComponent<Grid>();
            grid.cellSize = new Vector3(_cellSize, _cellSize, 0f);

            _tile = ScriptableObject.CreateInstance<Tile>();
            _tile.sprite = Agent_Art.Square(_cellSize);
            _tile.hideFlags = HideFlags.HideAndDontSave;

            Material lit = Agent_Stadium.Instance != null
                ? Agent_Stadium.Instance.litMaterial : null;

            // Hoardings get the sheen material when Agent_Surfaces built one -
            // it runs at -35, so it has already assigned BoardMaterial by now.
            var surfaces = GetComponent<Agent_Surfaces>();
            Material boardMaterial = surfaces != null && surfaces.BoardMaterial != null
                ? surfaces.BoardMaterial : lit;

            _stands = NewLayer(gridGo.transform, "Stands", -18, lit);
            var boards = NewLayer(gridGo.transform, "Boards", -16, boardMaterial);

            FillRing(_stands, standsInner, standsOuter, CrowdColor, _cells, _cellWorld);
            FillRing(boards, boardInner, boardOuter, BoardColor, null, null);

            BuildFlashbulbs(standsInner, standsOuter);
        }

        Tilemap NewLayer(Transform parent, string label, int sortingOrder, Material lit)
        {
            var go = new GameObject(label);
            go.transform.SetParent(parent, false);
            var tilemap = go.AddComponent<Tilemap>();
            var renderer = go.AddComponent<TilemapRenderer>();
            renderer.sortingOrder = sortingOrder;
            // Match the rest of the scene's lighting; Agent_Stadium only reassigns
            // SpriteRenderers, and a TilemapRenderer is not one.
            if (lit != null) renderer.sharedMaterial = lit;
            return tilemap;
        }

        /// <summary>
        /// Fills the rectangular annulus between <paramref name="inner"/> and
        /// <paramref name="outer"/> half-extents, colouring each cell through
        /// <paramref name="tint"/>.
        /// </summary>
        void FillRing(Tilemap tilemap, Vector2 inner, Vector2 outer,
            System.Func<Vector3, Color> tint, List<Vector3Int> cellsOut, List<Vector3> worldOut)
        {
            int maxX = Mathf.CeilToInt(outer.x / _cellSize) + 1;
            int maxY = Mathf.CeilToInt(outer.y / _cellSize) + 1;

            for (int y = -maxY; y <= maxY; y++)
            {
                for (int x = -maxX; x <= maxX; x++)
                {
                    var cell = new Vector3Int(x, y, 0);
                    Vector3 world = tilemap.GetCellCenterWorld(cell);
                    Vector3 local = world - transform.position;

                    bool insideOuter = Mathf.Abs(local.x) <= outer.x && Mathf.Abs(local.y) <= outer.y;
                    bool insideInner = Mathf.Abs(local.x) < inner.x && Mathf.Abs(local.y) < inner.y;
                    if (!insideOuter || insideInner) continue;

                    tilemap.SetTile(cell, _tile);
                    // Required before SetColor: tiles lock their colour by default.
                    tilemap.SetTileFlags(cell, TileFlags.None);
                    tilemap.SetColor(cell, tint(local));

                    if (cellsOut != null)
                    {
                        cellsOut.Add(cell);
                        // Kept so the shimmer can vary AROUND the seat colour
                        // instead of compounding on the previous frame's value.
                        _cellBase.Add(tilemap.GetColor(cell));
                    }
                    if (worldOut != null) worldOut.Add(world);
                }
            }
        }

        Color CrowdColor(Vector3 local)
        {
            if (Random.value > _occupancy) return EmptySeat;
            Color tone = SpectatorTones[Random.Range(0, SpectatorTones.Length)];
            // Rows further from the pitch sit deeper in shadow, which reads as depth.
            float depth = Mathf.InverseLerp(0f, _standsDepth,
                Mathf.Max(Mathf.Abs(local.x) - _env.PitchHalfExtents.x,
                          Mathf.Abs(local.y) - _env.PitchHalfExtents.y));
            return Color.Lerp(tone, EmptySeat, Mathf.Clamp01(depth) * 0.55f);
        }

        Color BoardColor(Vector3 local)
        {
            // Alternating blocks of team colour and dark, like hoardings.
            int block = Mathf.FloorToInt((Mathf.Abs(local.x) + Mathf.Abs(local.y)) / (_cellSize * 6f));
            switch (block % 3)
            {
                case 0: return Agent_UIStyle.BlueTeam * 0.75f;
                case 1: return new Color(0.08f, 0.09f, 0.10f);
                default: return Agent_UIStyle.RedTeam * 0.75f;
            }
        }

        void BuildFlashbulbs(Vector2 inner, Vector2 outer)
        {
            _bulbs = new Flashbulb[Mathf.Max(0, _flashbulbs)];
            for (int i = 0; i < _bulbs.Length; i++)
            {
                var go = new GameObject("Flashbulb");
                go.transform.SetParent(transform, false);
                var light = go.AddComponent<Light2D>();
                light.lightType = Light2D.LightType.Point;
                light.color = new Color(0.92f, 0.96f, 1f);
                light.pointLightInnerRadius = 0.1f;
                light.pointLightOuterRadius = 2.2f;
                light.intensity = 0f;
                _bulbs[i].Light = light;
                _bulbs[i].Cooldown = Random.Range(0f, 3f);
            }
        }

        // -- Life ------------------------------------------------------------

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null) return;
            // Everyone reaches for their phone at once.
            _flashBoost = 1f;
        }

        void Update()
        {
            Shimmer();
            TickFlashbulbs();
        }

        /// <summary>
        /// Recolours a short contiguous run of cells each frame. Contiguous, not
        /// random: scattered writes dirty many tilemap chunks at once and each one
        /// costs a mesh rebuild.
        /// </summary>
        void Shimmer()
        {
            if (_stands == null || _cells.Count == 0) return;

            for (int i = 0; i < _shimmerPerFrame; i++)
            {
                _shimmerCursor = (_shimmerCursor + 1) % _cells.Count;
                var cell = _cells[_shimmerCursor];
                Color seat = _cellBase[_shimmerCursor];
                if (seat == EmptySeat) continue;
                // Around the base colour, never off the previous value: reading
                // back and re-multiplying is a multiplicative random walk, which
                // drifts the whole crowd darker over the course of a match.
                float jiggle = Random.Range(0.92f, 1.08f);
                _stands.SetColor(cell, new Color(
                    Mathf.Clamp01(seat.r * jiggle),
                    Mathf.Clamp01(seat.g * jiggle),
                    Mathf.Clamp01(seat.b * jiggle), 1f));
            }
        }

        void TickFlashbulbs()
        {
            if (_bulbs == null || _cellWorld.Count == 0) return;

            float dt = Time.unscaledDeltaTime;
            _flashBoost = Mathf.Max(0f, _flashBoost - dt * 0.35f);
            // Goals turn a trickle of flashes into a storm.
            float rate = _flashRate * (1f + _flashBoost * 14f);
            float meanGap = _bulbs.Length / Mathf.Max(0.01f, rate);

            for (int i = 0; i < _bulbs.Length; i++)
            {
                var light = _bulbs[i].Light;
                if (light == null) continue;

                if (_bulbs[i].Life > 0f)
                {
                    _bulbs[i].Life -= dt;
                    // Hard attack, fast decay - a xenon pop, not a fade-in.
                    light.intensity = Mathf.Max(0f, _bulbs[i].Life / 0.12f) * 2.4f;
                    if (_bulbs[i].Life <= 0f)
                    {
                        light.intensity = 0f;
                        _bulbs[i].Cooldown = Random.Range(0.2f, 2f) * meanGap;
                    }
                    continue;
                }

                _bulbs[i].Cooldown -= dt;
                if (_bulbs[i].Cooldown > 0f) continue;

                light.transform.position = _cellWorld[Random.Range(0, _cellWorld.Count)];
                _bulbs[i].Life = 0.12f;
            }
        }
    }
}
