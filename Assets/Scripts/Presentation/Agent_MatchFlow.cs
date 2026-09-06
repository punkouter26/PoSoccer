using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Gives the match a shape: a 3-2-1 kickoff, a halftime break, a golden-goal
    /// finish when both sides are one goal from winning, and a man-of-the-match
    /// card on the end panel.
    ///
    /// It is also the director that orders the goal sequence. Left alone, a goal
    /// fires four independent reactions in one frame (score, horn, replay, end
    /// panel) and they trip over each other - the end panel appears over the top
    /// of a replay that is still playing. Here the sequence is explicit:
    ///
    ///     goal -> callout -> replay -> [halftime | golden goal | full time] -> GO
    ///
    /// The HUD hands over control of the end panel via Agent_HUD.DeferEndPanel, so
    /// the result card waits for the replay and the final whistle.
    ///
    /// NOTE ON HALFTIME: the sides do NOT swap ends. Goal ownership is baked into
    /// each agent's team-relative observations, so swapping would silently put
    /// every brain out of distribution mid-match for no gameplay gain.
    ///
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_MatchFlow : MonoBehaviour
    {
        [Tooltip("Seconds per kickoff countdown step (3 / 2 / 1 / GO).")]
        [SerializeField] private float _countdownStep = 0.7f;
        [Tooltip("Run the full 3-2-1 at the opening kickoff.")]
        [SerializeField] private bool _openingCountdown = true;
        [Tooltip("Seconds the halftime card holds.")]
        [SerializeField] private float _halftimeSeconds = 3.2f;
        [Tooltip("Combined goals that trigger halftime. 0 = derive from the HUD target (matchGoals - 1).")]
        [SerializeField] private int _halftimeAtTotalGoals = 0;
        [SerializeField] private bool _enableHalftime = true;
        [SerializeField] private bool _enableGoldenGoal = true;

        sealed class Tally
        {
            public Agent_Soccer Agent;
            public int Goals;
            public int Assists;
            public int Touches;

            // Goals dominate, assists matter, touches only break ties - a player
            // who never scores should not win the award on possession alone.
            public float Score => Goals * 100f + Assists * 40f + Touches;
        }

        /// <summary>
        /// Distinct freeze tokens per phase.
        ///
        /// Agent_TimeFreeze tracks holders by identity, so passing `this` from
        /// every phase collapsed them into a single hold: if two phases ever
        /// overlapped, the second Acquire was a silent no-op and the first
        /// Release handed the clock back while the other still believed it held
        /// it. The sequencing guard makes that unreachable today, which is
        /// exactly why it would have been missed when the next phase is added.
        /// </summary>
        readonly object _openingToken = new();
        readonly object _goalToken = new();
        readonly object _halftimeToken = new();
        readonly object _goldenToken = new();
        readonly object _fullTimeToken = new();

        Agent_EnvController _env;
        Agent_HUD _hud;
        Agent_Audio _audio;
        Agent_Replay _replay;
        readonly List<Tally> _tallies = new();
        CancellationTokenSource _cts;
        bool _halftimeDone;
        bool _goldenGoalArmed;
        bool _sequencing;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindFirstObjectByType<Agent_HUD>();

            if (!Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _audio = GetComponent<Agent_Audio>();
            _replay = GetComponent<Agent_Replay>();

            // Statics survive a scene load in a player build; a REMATCH taken from
            // a frozen end panel would otherwise start already paused.
            Agent_TimeFreeze.ReleaseAll();

            for (int i = 0; i < _env.agents.Count; i++)
            {
                var agent = _env.agents[i];
                if (agent != null) _tallies.Add(new Tally { Agent = agent });
            }

            _hud.DeferEndPanel = true;
            _hud.AddEndPanelSection(BuildManOfTheMatchCard);

            _env.EpisodeEnded += OnEpisodeEnded;
            _env.BallTouched += OnBallTouched;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(
                this.GetCancellationTokenOnDestroy());

            // A countdown with no HUD to draw it is just the game refusing to
            // start for two and a half seconds with nothing on screen to explain
            // why. enableMatchFlow alone was not a sufficient gate.
            if (_openingCountdown && _hud.showHud) OpeningAsync(_cts.Token).Forget();
        }

        void OnDestroy()
        {
            if (_env != null)
            {
                _env.EpisodeEnded -= OnEpisodeEnded;
                _env.BallTouched -= OnBallTouched;
            }
            if (_hud != null) _hud.RemoveEndPanelSection(BuildManOfTheMatchCard);
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            ReleaseAllTokens();
        }

        void ReleaseAllTokens()
        {
            Agent_TimeFreeze.Release(_openingToken);
            Agent_TimeFreeze.Release(_goalToken);
            Agent_TimeFreeze.Release(_halftimeToken);
            Agent_TimeFreeze.Release(_goldenToken);
            Agent_TimeFreeze.Release(_fullTimeToken);
        }

        // -- Bookkeeping -----------------------------------------------------

        void OnBallTouched(Agent_Soccer toucher)
        {
            var tally = Find(toucher);
            if (tally != null) tally.Touches++;
        }

        Tally Find(Agent_Soccer agent)
        {
            if (agent == null) return null;
            for (int i = 0; i < _tallies.Count; i++)
                if (_tallies[i].Agent == agent) return _tallies[i];
            return null;
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null) return;

            // Mirrors Agent_EnvController.OnGoalScored's credit rules: the scorer
            // only counts when the last toucher was on the scoring side, so an own
            // goal is not filed as somebody's goal.
            var scorer = _env.LastToucher;
            if (scorer != null && scorer.team == winner.Value)
            {
                var tally = Find(scorer);
                if (tally != null) tally.Goals++;

                var assister = _env.PreviousToucher;
                if (assister != null && assister != scorer && assister.team == winner.Value)
                {
                    var assistTally = Find(assister);
                    if (assistTally != null) assistTally.Assists++;
                }
            }

            if (_sequencing || _cts == null) return;
            _sequencing = true;

            // Freeze HERE, not after the first await. GoalSequenceAsync yields a
            // frame before it would otherwise take its hold, and the replay only
            // freezes when it actually plays (it needs ~0.6s of captured
            // history, which a goal straight from kickoff does not have). That
            // left one or two frames in which the pitch resumed at full speed
            // immediately after a goal - visible as a jerk on the restart.
            Agent_TimeFreeze.Acquire(_goalToken);
            GoalSequenceAsync(_cts.Token).Forget();
        }

        // -- Sequencing ------------------------------------------------------

        async UniTaskVoid GoalSequenceAsync(CancellationToken token)
        {
            try
            {
                // One frame so the HUD's own EpisodeEnded handler has certainly
                // applied the score, whichever order the two subscribed in.
                await UniTask.Yield(PlayerLoopTiming.Update, token);

                if (_replay != null)
                    await UniTask.WaitUntil(() => _replay == null || !_replay.IsPlaying,
                        PlayerLoopTiming.Update, token);

                if (_hud.MatchOver)
                {
                    await FullTimeAsync(token);
                    return;
                }

                int total = _hud.BlueScore + _hud.RedScore;

                if (_enableHalftime && !_halftimeDone && total >= HalftimeTrigger)
                {
                    _halftimeDone = true;
                    await HalftimeAsync(token);
                }
                else if (_enableGoldenGoal && !_goldenGoalArmed && IsGoldenGoal())
                {
                    _goldenGoalArmed = true;
                    await GoldenGoalAsync(token);
                }
                else
                {
                    await RestartAsync(token);
                }
            }
            finally
            {
                // Released last: each phase below takes its own hold first, so the
                // clock never becomes free in the gap between them.
                Agent_TimeFreeze.Release(_goalToken);
                _sequencing = false;
            }
        }

        int HalftimeTrigger => _halftimeAtTotalGoals > 0
            ? _halftimeAtTotalGoals
            : Mathf.Max(1, _hud.matchGoals - 1);

        bool IsGoldenGoal()
        {
            int target = _hud.matchGoals;
            return target > 1 && _hud.BlueScore == target - 1 && _hud.RedScore == target - 1;
        }

        async UniTask HalftimeAsync(CancellationToken token)
        {
            Agent_TimeFreeze.Acquire(_halftimeToken);
            try
            {
                if (_audio != null) _audio.Whistle(0.6f);
                _hud.Toast("HALF TIME", Agent_UIStyle.TextPrimary, _halftimeSeconds);
                _hud.Say($"{_hud.BlueScore} - {_hud.RedScore}", Agent_UIStyle.TextMuted, _halftimeSeconds);

                await UniTask.Delay(System.TimeSpan.FromSeconds(_halftimeSeconds),
                    DelayType.UnscaledDeltaTime, cancellationToken: token);

                await CountdownAsync(token, 3);
            }
            finally
            {
                // try/finally throughout: a cancelled await (scene change mid
                // sequence) would otherwise skip the release and leave the next
                // scene's clock stopped with nothing in the console to say why.
                Agent_TimeFreeze.Release(_halftimeToken);
            }
        }

        async UniTask GoldenGoalAsync(CancellationToken token)
        {
            Agent_TimeFreeze.Acquire(_goldenToken);
            try
            {
                // Both sides are one goal from winning, so the next goal decides
                // the match under the existing first-to-N rule. Nothing about the
                // rules changes here - this is the staging that makes it legible.
                _hud.ShowBanner("● GOLDEN GOAL ●", new Color(1f, 0.84f, 0.2f));
                _hud.Toast("GOLDEN GOAL", new Color(1f, 0.84f, 0.2f), 2.0f);
                _hud.Say("Next goal wins it.", Agent_UIStyle.TextPrimary, 2.4f);

                if (_audio != null) _audio.Cheer(1f);
                if (Agent_Stadium.Instance != null)
                    Agent_Stadium.Instance.CelebrateGoal(new Color(1f, 0.84f, 0.2f));

                await UniTask.Delay(System.TimeSpan.FromSeconds(2.0f),
                    DelayType.UnscaledDeltaTime, cancellationToken: token);

                await CountdownAsync(token, 3);
            }
            finally
            {
                Agent_TimeFreeze.Release(_goldenToken);
            }
        }

        async UniTask RestartAsync(CancellationToken token)
        {
            // No token of its own: the goal hold already covers this phase, and
            // taking a second one would only add a way to leak it.
            _hud.Toast("GO!", Agent_UIStyle.Accent, 0.5f);
            if (_audio != null) _audio.Whistle(0.4f);
            await UniTask.Delay(System.TimeSpan.FromSeconds(0.5f),
                DelayType.UnscaledDeltaTime, cancellationToken: token);
        }

        async UniTask FullTimeAsync(CancellationToken token)
        {
            Agent_TimeFreeze.Acquire(_fullTimeToken);
            _hud.HideBanner();
            if (_audio != null)
            {
                _audio.Whistle(0.7f);
                _audio.Cheer(1f);
            }
            _hud.Toast("FULL TIME", Agent_UIStyle.TextPrimary, 1.6f);

            await UniTask.Delay(System.TimeSpan.FromSeconds(1.6f),
                DelayType.UnscaledDeltaTime, cancellationToken: token);

            // The end panel takes its own freeze; release ours after so the clock
            // never restarts in the gap between the two.
            _hud.ShowEndPanelNow();
            Agent_TimeFreeze.Release(_fullTimeToken);
        }

        async UniTaskVoid OpeningAsync(CancellationToken token)
        {
            Agent_TimeFreeze.Acquire(_openingToken);
            try
            {
                // A frame first, so the HUD has built its elements before we write to them.
                await UniTask.Yield(PlayerLoopTiming.Update, token);
                await CountdownAsync(token, 3);
            }
            finally
            {
                Agent_TimeFreeze.Release(_openingToken);
            }
        }

        async UniTask CountdownAsync(CancellationToken token, int from)
        {
            for (int n = from; n >= 1; n--)
            {
                _hud.Toast(n.ToString(), Agent_UIStyle.TextPrimary, _countdownStep);
                await UniTask.Delay(System.TimeSpan.FromSeconds(_countdownStep),
                    DelayType.UnscaledDeltaTime, cancellationToken: token);
            }
            _hud.Toast("GO!", Agent_UIStyle.Accent, 0.6f);
            if (_audio != null) _audio.Whistle(0.5f);
            await UniTask.Delay(System.TimeSpan.FromSeconds(0.35f),
                DelayType.UnscaledDeltaTime, cancellationToken: token);
        }

        // -- Man of the match ------------------------------------------------

        VisualElement BuildManOfTheMatchCard()
        {
            Tally best = null;
            for (int i = 0; i < _tallies.Count; i++)
            {
                var tally = _tallies[i];
                if (tally.Agent == null || tally.Agent.rewards == null) continue;
                if (best == null || tally.Score > best.Score) best = tally;
            }
            if (best == null || best.Score <= 0f) return null;

            var profile = best.Agent.rewards;
            var card = new VisualElement();
            card.style.alignItems = Align.Center;
            card.style.backgroundColor = Agent_UIStyle.PanelBg;
            card.style.marginBottom = 24;
            Agent_UIStyle.Round(card);
            Agent_UIStyle.PadAll(card, Agent_UIStyle.Pad + 8);

            var heading = new Label("MAN OF THE MATCH");
            heading.style.fontSize = Agent_UIStyle.FontS;
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.color = Agent_UIStyle.TextMuted;
            card.Add(heading);

            var badge = new Label(profile.playerName.Substring(0, 1));
            badge.style.width = 110; badge.style.height = 110;
            badge.style.marginTop = 12; badge.style.marginBottom = 12;
            badge.style.backgroundColor = profile.playerColor;
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.style.fontSize = Agent_UIStyle.FontL;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.color = Color.black;
            Agent_UIStyle.Round(badge, 16);
            badge.style.borderBottomWidth = 8;
            badge.style.borderBottomColor = best.Agent.team == Agent_Soccer.Team.Blue
                ? Agent_UIStyle.BlueTeam : Agent_UIStyle.RedTeam;
            card.Add(badge);

            var name = new Label(profile.playerName);
            name.style.fontSize = Agent_UIStyle.FontM;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.color = Agent_UIStyle.TextPrimary;
            card.Add(name);

            var line = new Label($"{best.Goals} G   ·   {best.Assists} A   ·   {best.Touches} touches");
            line.style.fontSize = Agent_UIStyle.FontS;
            line.style.color = Agent_UIStyle.TextMuted;
            line.style.marginTop = 6;
            card.Add(line);

            return card;
        }
    }
}
