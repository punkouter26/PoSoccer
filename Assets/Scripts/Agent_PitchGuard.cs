using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Corner-jam prevention, built at runtime on every pitch (grid clones included):
    /// smooth quarter-circle corner colliders (no wedge pockets), a zero-friction
    /// bouncy wall material so the ball reflects cleanly off all 4 sides instead of
    /// depenetrating into the out-of-bounds zone, and extra-bouncy corner arcs that
    /// eject a settling ball. The arcs sit just inside the 45-degree bevel sprites
    /// and overlap the walls at both ends, so there are no collider seams for the
    /// ball to lodge in.
    ///
    /// Bounciness is high enough (0.85 sides, 0.95 corners) that high-speed impacts
    /// keep play inside the pitch reliably, which is what allows
    /// <see cref="Agent_EnvController"/> to drop its containment watchdog: a
    /// train-and-play convention in this project (no OOB resets, ever).
    /// </summary>
    public static class Agent_PitchGuard
    {
        // Radius chosen so the arc midpoint sits flush in front of the existing
        // 45-degree bevel face (bevel face ~1.04 from the corner; r*(sqrt2-1)=1.08).
        public const float CornerRadius = 2.6f;
        const int ArcSegments = 12;
        const float WallOverlap = 0.45f;

        static PhysicsMaterial2D _slickWall;
        static PhysicsMaterial2D _bouncyCorner;

        public static void Build(Agent_EnvController env)
        {
            if (env == null || env.transform.Find("PitchGuard") != null) return;

            // Box2D combines friction as sqrt(a*b) and bounciness as max(a,b):
            // friction 0 on the wall wins regardless of the ball's material.
            // Bounciness was raised so a slammed ball reflects back into play
            // instead of depenetrating through the wall (which used to trigger
            // the OOB reset in the env controller). 0.85 sides + 0.95 corners
            // covers every realistic shot/trap speed.
            _slickWall ??= new PhysicsMaterial2D("SlickWall") { friction = 0f, bounciness = 0.85f };
            _bouncyCorner ??= new PhysicsMaterial2D("BouncyCorner") { friction = 0f, bounciness = 0.95f };

            foreach (var col in env.GetComponentsInChildren<Collider2D>())
                if (col.CompareTag("Wall") && !col.isTrigger)
                    col.sharedMaterial = _slickWall;

            var go = new GameObject("PitchGuard") { tag = "Wall" };
            go.transform.SetParent(env.transform, false);
            // The Wall tag buys arc-bounce audio, but Agent_Stadium also gives every
            // Wall a ShadowCaster2D. The guard has no sprite shape, so pre-occupy the
            // slot with a disabled caster to avoid a stray square shadow at midfield.
            var caster = go.AddComponent<UnityEngine.Rendering.Universal.ShadowCaster2D>();
            caster.enabled = false;

            Vector2 half = env.PitchHalfExtents;
            BuildCornerArc(go, half, new Vector2(1f, 1f));
            BuildCornerArc(go, half, new Vector2(1f, -1f));
            BuildCornerArc(go, half, new Vector2(-1f, 1f));
            BuildCornerArc(go, half, new Vector2(-1f, -1f));
        }

        static void BuildCornerArc(GameObject host, Vector2 half, Vector2 sign)
        {
            float r = CornerRadius;
            Vector2 center = new(sign.x * (half.x - r), sign.y * (half.y - r));

            var points = new List<Vector2>
            {
                // Lead-in flush along the side wall face (seals the arc-wall seam).
                new(sign.x * half.x, center.y - sign.y * WallOverlap),
            };
            for (int i = 0; i <= ArcSegments; i++)
            {
                float t = i / (float)ArcSegments * Mathf.PI * 0.5f;
                points.Add(center + new Vector2(sign.x * Mathf.Cos(t) * r, sign.y * Mathf.Sin(t) * r));
            }
            // Lead-out flush along the end wall face.
            points.Add(new Vector2(center.x - sign.x * WallOverlap, sign.y * half.y));

            var edge = host.AddComponent<EdgeCollider2D>();
            edge.points = points.ToArray();
            edge.edgeRadius = 0.04f;
            edge.sharedMaterial = _bouncyCorner;

            // Faint line so the physical curve reads on screen.
            var lineGo = new GameObject("CornerArc");
            lineGo.transform.SetParent(host.transform, false);
            var line = lineGo.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++)
                line.SetPosition(i, points[i]);
            line.startWidth = line.endWidth = 0.06f;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = line.endColor = new Color(1f, 1f, 1f, 0.35f);
            line.sortingOrder = 1;
        }
    }
}
