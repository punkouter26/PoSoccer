using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Scene-view diagnostics (PRD Visual Diagnostics): facing arrow on the eye axis,
    /// stamina sphere, and the 120° forward vision arc.
    /// </summary>
    [RequireComponent(typeof(Agent_Soccer))]
    public sealed class Agent_DebugGizmos : MonoBehaviour
    {
        public bool drawVisionArc = true;
        public bool drawStamina = true;
        public bool drawFacing = true;

        Agent_Soccer _agent;
        Agent_Stamina _stamina;

        void OnDrawGizmos()
        {
            if (_agent == null) _agent = GetComponent<Agent_Soccer>();
            if (_stamina == null) _stamina = GetComponent<Agent_Stamina>();

            Vector3 pos = transform.position;
            Vector3 fwd = transform.up;

            if (drawFacing)
            {
                Gizmos.color = _agent.team == Agent_Soccer.Team.Blue ? Color.cyan : Color.red;
                Gizmos.DrawLine(pos, pos + fwd * 1.2f);
                Gizmos.DrawSphere(pos + fwd * 1.2f, 0.07f);
            }

            if (drawStamina && _stamina != null && Application.isPlaying)
            {
                Gizmos.color = Color.Lerp(Color.red, Color.green, _stamina.Ratio);
                Gizmos.DrawWireSphere(pos, 0.55f + 0.25f * _stamina.Ratio);
            }

            if (drawVisionArc)
            {
                Gizmos.color = new Color(1f, 1f, 0f, 0.35f);
                float half = Sensor_Vision.ArcDegrees * 0.5f;
                int rays = Sensor_Vision.RaysPerDirection * 2 + 1;
                for (int i = 0; i < rays; i++)
                {
                    float t = rays == 1 ? 0f : (float)i / (rays - 1);
                    float angle = Mathf.Lerp(-half, half, t);
                    Vector3 dir = Quaternion.Euler(0f, 0f, angle) * fwd;
                    Gizmos.DrawLine(pos, pos + dir * Sensor_Vision.RayLength);
                }
            }
        }
    }
}
