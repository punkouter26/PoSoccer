using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Impact weight: a brief dip in playback rate on a goal and on the hardest
    /// contacts, recovering on an ease-out so the game accelerates back to speed
    /// rather than snapping.
    ///
    /// SAFETY FIRST, BECAUSE THIS ONE CAN CORRUPT THE BENCHMARK. Everything else
    /// in the presentation layer only costs frames if it leaks into a headless
    /// run; this changes Time.timeScale, which is the axis the ML-Agents trainer
    /// and Agent_EvalStats both measure against. Agent_Presentation's docstring
    /// already makes the point about a replay freezing the clock during an eval.
    /// So this component is gated three ways and every one of them is
    /// independent: Agent_Presentation.IsMatchScene (which itself rejects eval
    /// mode and a connected trainer), an explicit batch-mode check, and a
    /// serialized off switch.
    ///
    /// It never writes Time.timeScale itself - it drives
    /// Agent_TimeFreeze.SlowMotion, so a full freeze (replay, countdown, end
    /// panel) always outranks it and a dip can never resume a paused game.
    ///
    /// Audio deliberately does NOT pitch down with the dip. A 90 ms detune on the
    /// crowd bed reads as a dropout, not as weight; Agent_Audio's ducking already
    /// carries the moment.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Hitstop : MonoBehaviour
    {
        [Tooltip("Playback rate at the bottom of a goal dip.")]
        [Range(0.05f, 1f)] [SerializeField] private float _goalScale = 0.32f;
        [Tooltip("Seconds held at the bottom of a goal dip, in real time.")]
        [SerializeField] private float _goalHold = 0.09f;
        [Tooltip("Seconds spent easing back to full speed after a goal dip.")]
        [SerializeField] private float _goalRecover = 0.28f;

        [Tooltip("Playback rate at the bottom of a hard-contact dip.")]
        [Range(0.05f, 1f)] [SerializeField] private float _impactScale = 0.6f;
        [Tooltip("Seconds held at the bottom of a contact dip, in real time.")]
        [SerializeField] private float _impactHold = 0.04f;
        [Tooltip("Seconds spent easing back after a contact dip.")]
        [SerializeField] private float _impactRecover = 0.12f;
        [Tooltip("Relative impact speed (m/s) below which a contact is not worth stopping for.")]
        [SerializeField] private float _impactThreshold = 11f;
        [Tooltip("Minimum real seconds between contact dips, so a scramble in the box does not stutter.")]
        [SerializeField] private float _impactCooldown = 0.7f;

        [SerializeField] private bool _enableHitstop = true;

        Agent_EnvController _env;
        CancellationTokenSource _dipCts;
        float _nextImpactAllowed;

        void Start()
        {
            var hud = FindFirstObjectByType<Agent_HUD>();
            // Batch mode is checked separately from IsMatchScene on purpose. The
            // gate is the thing standing between a cosmetic effect and a corrupted
            // eval, and one predicate covering two unrelated conditions is one
            // refactor away from covering neither.
            if (!_enableHitstop || Application.isBatchMode || !Agent_Presentation.IsMatchScene(hud))
            {
                enabled = false;
                return;
            }

            _env = GetComponent<Agent_EnvController>();
            _env.EpisodeEnded += OnEpisodeEnded;
            Agent_MatchFX.BallContact.Hit += OnBallHit;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            Agent_MatchFX.BallContact.Hit -= OnBallHit;

            _dipCts?.Cancel();
            _dipCts?.Dispose();
            _dipCts = null;

            // A component destroyed mid-dip (scene change, REMATCH) would
            // otherwise hand the next scene a 0.32x clock.
            Agent_TimeFreeze.SlowMotion = 1f;
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null) return;   // stalemate: nothing happened worth stopping for
            Dip(_goalScale, _goalHold, _goalRecover);
        }

        void OnBallHit(Collision2D collision)
        {
            if (collision.relativeVelocity.magnitude < _impactThreshold) return;
            if (Time.unscaledTime < _nextImpactAllowed) return;
            _nextImpactAllowed = Time.unscaledTime + _impactCooldown;
            Dip(_impactScale, _impactHold, _impactRecover);
        }

        void Dip(float scale, float hold, float recover)
        {
            // A new dip supersedes the one in flight rather than stacking, so two
            // goals in quick succession cannot compound into a crawl.
            _dipCts?.Cancel();
            _dipCts?.Dispose();
            _dipCts = CancellationTokenSource.CreateLinkedTokenSource(
                this.GetCancellationTokenOnDestroy());
            DipAsync(scale, hold, recover, _dipCts.Token).Forget();
        }

        async UniTaskVoid DipAsync(float scale, float hold, float recover, CancellationToken token)
        {
            Agent_TimeFreeze.SlowMotion = scale;

            // Every wait here is in REAL time. Waiting in scaled time inside a
            // time-scale effect is self-referential: the dip would stretch itself
            // by exactly the factor it just applied.
            await UniTask.Delay(System.TimeSpan.FromSeconds(hold),
                DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, token);

            float elapsed = 0f;
            while (elapsed < recover)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / recover);
                // Ease-out cubic: most of the speed comes back immediately and the
                // last of it arrives gently, which is what reads as "released"
                // rather than "resumed".
                float eased = 1f - Mathf.Pow(1f - t, 3f);
                Agent_TimeFreeze.SlowMotion = Mathf.Lerp(scale, 1f, eased);
                await UniTask.Yield(PlayerLoopTiming.Update, token);
            }

            Agent_TimeFreeze.SlowMotion = 1f;
        }
    }
}
