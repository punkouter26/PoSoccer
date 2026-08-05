using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Builds the purely cosmetic parts of a soccer agent: the personality body
    /// colour, the team-coloured eye, the team frame outline and the identity
    /// letter. Split out of <see cref="Agent_Soccer"/> so the agent file carries
    /// observations, actions, locomotion and rewards only.
    ///
    /// Deliberately a static helper rather than a MonoBehaviour: the frame tuning
    /// values are [SerializeField] on Agent_Soccer and already serialized into
    /// SCN_Training and SCN_Exhibition. A component would have to be added to
    /// every agent GameObject in both scenes, and any miss would silently drop a
    /// player's outline and letter. A static call keeps the scenes untouched.
    /// </summary>
    public static class Agent_SoccerView
    {
        private static readonly Color BlueTeamColor = new Color(0.2f, 0.5f, 1f);
        private static readonly Color RedTeamColor = new Color(1f, 0.25f, 0.2f);

        /// <summary>Team tint used by the eye and the frame outline.</summary>
        public static Color TeamColor(Agent_Soccer.Team team)
        {
            return team == Agent_Soccer.Team.Blue ? BlueTeamColor : RedTeamColor;
        }

        /// <summary>
        /// Applies every cosmetic element to <paramref name="root"/>. Returns the
        /// identity-letter transform when this call created it, otherwise null —
        /// the caller keeps it upright in Update.
        /// </summary>
        public static Transform Build(Transform root, Reward_Settings rewards,
            Agent_Soccer.Team team, float frameInset, float frameThickness, float frameZ)
        {
            if (root == null || rewards == null)
            {
                return null;
            }

            // Body wears the personality color; the eye shows the team.
            SpriteRenderer body = root.GetComponent<SpriteRenderer>();
            if (body != null && rewards.playerColor.a > 0f)
            {
                body.color = rewards.playerColor;
            }

            Color teamColor = TeamColor(team);

            Transform eye = root.Find("Eye");
            if (eye != null && eye.TryGetComponent(out SpriteRenderer eyeRenderer))
            {
                eyeRenderer.color = teamColor;
            }

            // Thick team-colored frame around the body: a 4-line LineRenderer
            // drawn just outside the body sprite so the team reads at a glance,
            // even on the small portrait phone view. We use a LineRenderer per
            // border instead of a 1.3x halo so the outline always looks line-shaped
            // and stays solid even when the sprite shape is non-square.
            if (body != null && body.sprite != null && root.Find("TeamFrame_Top") == null)
            {
                BuildTeamFrame(root, body, teamColor, frameInset, frameThickness, -frameZ);
            }

            // Identity letter (S/M/K/N) on the body, driven by the assigned profile.
            if (!string.IsNullOrEmpty(rewards.playerName) && root.Find("Label") == null)
            {
                var labelGo = new GameObject("Label");
                labelGo.transform.SetParent(root, false);
                labelGo.transform.localPosition = new Vector3(0f, -0.12f, 0f);

                var text = labelGo.AddComponent<TextMesh>();
                text.text = rewards.playerName.Substring(0, 1);
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                text.fontSize = 96;
                text.characterSize = 0.085f;
                text.anchor = TextAnchor.MiddleCenter;
                text.alignment = TextAlignment.Center;
                text.fontStyle = FontStyle.Bold;
                text.color = Color.black;

                var renderer = labelGo.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = text.font.material;
                renderer.sortingOrder = 5;
                return labelGo.transform;
            }

            return null;
        }

        private static void BuildTeamFrame(Transform parent, SpriteRenderer body, Color color,
            float inset, float thickness, float zOffset)
        {
            // Read the sprite's local bounds. SpriteRenderer.bounds is world-space;
            // we want the half-extents in the body's LOCAL space so the frame
            // follows rotation and scale exactly.
            Bounds b = body.sprite.bounds;
            // body-local size = (bounds * 2) * transform.localScale (per axis).
            Vector3 bodyScale = body.transform.localScale;
            float halfW = b.extents.x * Mathf.Abs(bodyScale.x) + inset;
            float halfH = b.extents.y * Mathf.Abs(bodyScale.y) + inset;

            BuildTeamFrameEdge(parent, "TeamFrame_Top",
                new Vector3(-halfW, halfH, zOffset),
                new Vector3(halfW, halfH, zOffset),
                color, thickness, body.sortingLayerName, body.sortingOrder - 2);
            BuildTeamFrameEdge(parent, "TeamFrame_Bottom",
                new Vector3(-halfW, -halfH, zOffset),
                new Vector3(halfW, -halfH, zOffset),
                color, thickness, body.sortingLayerName, body.sortingOrder - 2);
            BuildTeamFrameEdge(parent, "TeamFrame_Left",
                new Vector3(-halfW, -halfH, zOffset),
                new Vector3(-halfW, halfH, zOffset),
                color, thickness, body.sortingLayerName, body.sortingOrder - 2);
            BuildTeamFrameEdge(parent, "TeamFrame_Right",
                new Vector3(halfW, -halfH, zOffset),
                new Vector3(halfW, halfH, zOffset),
                color, thickness, body.sortingLayerName, body.sortingOrder - 2);
        }

        private static void BuildTeamFrameEdge(Transform parent, string name, Vector3 localA,
            Vector3 localB, Color color, float thickness, string sortingLayerName,
            int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.SetPosition(0, localA);
            lr.SetPosition(1, localB);
            lr.startWidth = thickness;
            lr.endWidth = thickness;
            lr.numCornerVertices = 0;
            lr.numCapVertices = 0;
            lr.alignment = LineAlignment.View;
            lr.startColor = color;
            lr.endColor = color;
            // Use the URP unlit sprite material so the team color stays bright even
            // under the GoalGlow point lights; fall back to Sprites/Default if the
            // project doesn't ship the URP 2D unlit shader.
            var mat = new Material(Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                ?? Shader.Find("Sprites/Default"));
            mat.color = color;
            lr.sharedMaterial = mat;
            lr.sortingLayerName = sortingLayerName;
            lr.sortingOrder = sortingOrder;
        }
    }
}
