using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Subtle visualization of what a brain-driven player actually perceives: the
    /// RayPerceptionSensor2D arc configured by <see cref="Sensor_Vision"/> (120 deg
    /// forward, 12 m, centered on the +Y eye axis). Unity only draws those rays as
    /// editor gizmos, so without this the sensor is invisible during play.
    ///
    /// Only agents running a trained brain get a cone. The heuristic bot reads ball
    /// and goal positions straight off the transforms and never consults the sensor,
    /// so drawing one on it would claim a limit it does not have.
    ///
    /// The mesh is built once and parented to the body, so it inherits rotation for
    /// free - no per-frame work. Alpha lives in the vertex colors, which lets every
    /// cone on the pitch share one material and one draw call's worth of state.
    /// </summary>
    public static class Agent_VisionCone
    {
        const string CONE_NAME = "VisionCone";
        const int SEGMENTS = 24;
        /// <summary>Alpha at the apex. Deliberately faint - this is an overlay, not a highlight.</summary>
        const float APEX_ALPHA = 0.10f;
        /// <summary>Local Z offset. Positive pushes it behind the body, in front of the pitch.</summary>
        const float CONE_Z = 0.03f;
        /// <summary>Rendered this far under the body so it never veils the player or the team frame.</summary>
        const int SORTING_OFFSET = -3;

        static Material _sharedMaterial;

        /// <summary>
        /// Attach a cone to <paramref name="body"/>'s GameObject. Safe to call twice -
        /// the second call is a no-op.
        /// </summary>
        public static void Attach(Transform parent, SpriteRenderer body, Color tint)
        {
            if (parent == null || parent.Find(CONE_NAME) != null)
            {
                return;
            }

            var go = new GameObject(CONE_NAME);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = BuildFan(
                Sensor_Vision.ArcDegrees, Sensor_Vision.RayLength, tint);

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = SharedMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (body != null)
            {
                renderer.sortingLayerName = body.sortingLayerName;
                renderer.sortingOrder = body.sortingOrder + SORTING_OFFSET;
            }
        }

        /// <summary>
        /// Triangle fan from the apex out to the arc. Vertex alpha runs from
        /// APEX_ALPHA at the player to zero at the rim, so the cone dissolves into
        /// the pitch instead of ending on a hard edge.
        /// </summary>
        static Mesh BuildFan(float arcDegrees, float radius, Color tint)
        {
            var vertices = new Vector3[SEGMENTS + 2];
            var colors = new Color[SEGMENTS + 2];
            var triangles = new int[SEGMENTS * 3];

            vertices[0] = new Vector3(0f, 0f, CONE_Z);
            colors[0] = Premultiplied(tint, APEX_ALPHA);

            float half = arcDegrees * 0.5f;
            float step = arcDegrees / SEGMENTS;
            for (int segment = 0; segment <= SEGMENTS; segment++)
            {
                float degrees = -half + step * segment;
                float radians = degrees * Mathf.Deg2Rad;
                // Measured off the +Y eye axis, matching how Sensor_Vision lays out its rays.
                vertices[segment + 1] = new Vector3(
                    Mathf.Sin(radians) * radius, Mathf.Cos(radians) * radius, CONE_Z);
                colors[segment + 1] = Premultiplied(tint, 0f);
            }

            for (int segment = 0; segment < SEGMENTS; segment++)
            {
                triangles[segment * 3] = 0;
                triangles[segment * 3 + 1] = segment + 1;
                triangles[segment * 3 + 2] = segment + 2;
            }

            var mesh = new Mesh { name = "VisionConeFan" };
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// The URP 2D sprite shader blends One / OneMinusSrcAlpha, so the source RGB
        /// must arrive already scaled by alpha. Passing full-brightness RGB with a low
        /// alpha renders almost additively instead - a faint blue cone turns the whole
        /// pitch cyan.
        /// </summary>
        static Color Premultiplied(Color rgb, float alpha) =>
            new(rgb.r * alpha, rgb.g * alpha, rgb.b * alpha, alpha);

        /// <summary>
        /// One unlit, vertex-colored, transparent material for every cone in the
        /// scene - per-player color rides in the mesh, so nothing here varies.
        /// </summary>
        static Material SharedMaterial()
        {
            if (_sharedMaterial != null)
            {
                return _sharedMaterial;
            }

            // The 2D renderer's own sprite shader is the one guaranteed to be in the
            // build; Sprites/Default is the fallback for a non-URP context.
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            _sharedMaterial = new Material(shader) { name = "VisionConeShared" };
            return _sharedMaterial;
        }
    }
}
