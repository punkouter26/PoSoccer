# Training history: the run-by-run record (p4 → p26)

Split out of `CLAUDE.md` on 2026-09-13. That file is the operational brief — rules,
commands, architecture, landmines — and this is the **narrative of what was tried and
what it measured**. Nothing here is needed to work on the game; it is needed to avoid
re-running an experiment this project has already paid for.

**Read this before proposing any training lever.** Disproven here, with numbers:
"train it longer" (p4 vs p5, 4.3× the steps, zero gain), gamma 0.999 (p19, worse),
MA-POCA group credit (p15/p16, ELO fell), curiosity (p13/p14), imitation
(p26, **half** the win rate), and the train/eval opponent mismatch (p22 trained to
full bot strength and graded the same).

The standing headline: the benchmark bar is **≥80% wins with ≤10% stalemates**, and the
best graded brain sits at **26.6%** (`soccer_p22gain_standard`, n=350). The current
`STANDARD.onnx` is from `soccer_p25episode_standard` and is **ungraded**.

Every landmine and method rule that still governs day-to-day work stayed in `CLAUDE.md`.

---

## STAGED 2026-09-28 — p29, team training (ungraded, never run)

Every run above was **1v1**: `SCN_Training` has one agent per side, so the teammate
observation block and the second opponent slot were zero on every step any brain has
seen. The MA-POCA runs on the disproven list (p10, p15, p16) used a group of **one**,
where POCA's centralised critic has no teammate to split credit with, and all of them
predate the 2026-08-28 body-frame fix. Team credit assignment is therefore untested,
not disproven.

p29 (`config/TRAIN_STANDARD_p29team.yaml`) is p22's hyperparameters under `poca`, on a
`-Squad "1,2"` grid (half 1v1, half 2v2), with two new trainer-driven shaping terms:
`team_roles` (the ball-approach reward is paid toward each player's role target from
`Agent_TeamRoles` - ball for the presser, a post for goalie/defender/support attacker -
same scale, so the dense budget is redirected rather than grown)
and `team_spacing` (≤ −0.45 per episode ceiling, under conceding). Four levers at once on
purpose, because the other three mean nothing without the teammate; the ablation is the
same file with both team terms at 0. Grade at 1v1 (comparable to p22's 26.6%) **and** at
`-Squad 2`, which needs its own baselines first (the deployed brain and bot-vs-bot at
`-Squad 2`). Full reasoning is in the config header.

---

## State (2026-08-04)

**ROOT CAUSE FOUND 2026-08-28 — observations were world-frame while actions were body-frame, and that is why nine phases of training could not reach the ball.**

`OnActionReceived` has always built movement in the agent's own frame:

```csharp
Vector2 intent = Vector2.ClampMagnitude(transform.up * move + transform.right * lateral, 1f);
```

while `CollectObservations` emitted every relative position and velocity in **world** frame — `relBall`, ball velocity, self velocity, the eye axis, goals, teammate, both opponent slots. So "drive at the ball" was never a lookup the network could read off an input; it was a product it had to synthesise:

```
move    = dot(relBall_world, up_world)
lateral = dot(relBall_world, right_world)
```

An MLP approximates that bilinear rotation only piecewise, and the body turns through the full circle (**184 deg of heading churn** measured), so the policy had to learn a *different* linear map for every heading it ever held. Worse, `Sensor_Vision`'s ray sensors were egocentric the whole time — the network was handed **two contradictory coordinate systems for the same world** and had to reconcile them.

This is exactly the "misdirected, not collapsed" signature that `50d235f` probed and could not explain: confident action magnitudes pointed the wrong way. It is a **representation** defect, not a reward, perception, capacity or credit-assignment defect — which is why phases 6 through 17 each moved the win rate by less than noise while attacking every one of those instead:

| phase | hypothesis tested | result |
|---|---|---|
| 6 | opponent observations (18 -> 26 obs) | 17.1% vs 16.2% — noise |
| 7 | reward table (stalling was optimal) | 16.6% — noise |
| 8 | locomotion reward terms drifted in the assets | reward up 8x, probe still 0.99 m |
| 9/10 | domain randomization, 4-sensor perception split | graded a stale build; retracted |
| 13/14 | curiosity, more capacity | no change |
| 15/16 | MA-POCA group credit (really was broken, really was fixed) | ELO 1178 -> 579 |
| 17 | reward v3, arriving must out-pay aiming | "2.3x faster, still cannot reach the ball" |

Every one of those hypotheses was tested *on top of a policy that could not be told where the ball was in the frame it had to act in*.

**Fixed 2026-08-28** in `Agent_Soccer.CollectObservations` via `ToBodyFrame` (two dot products, zero alloc). Verified live before spending any compute: with the ball placed 3 units straight ahead, the observation now reads `relBall = (0.000, 0.111)` and `bearing = 0.000` at headings 0/90/180/-90 deg — **identical at every heading**, where before it produced four different vectors. Ball placed to the agent's right reads `(0.111, 0.000)` at every heading.

The two world eye-axis floats were redundant under a body frame (they are the constant `(0,1)`), so they now carry **yaw rate** and **signed bearing to the ball**, giving the turn channel proprioception it never had. Sign convention matches `AddTorque`: positive = counter-clockwise = left, so the turn head can approach the identity instead of learning a sign flip.

**MEASURED 2026-08-29 — what the body-frame fix actually bought, and what it did not.**

`soccer_p18bf_standard` (3M steps, body frame, otherwise identical to the p17 config):

| | p17 (world frame) | p18 (body frame) |
|---|---|---|
| mean reward @250k | -0.865 | **-0.573** |
| mean reward @1M | -0.674 | **-0.236** |
| mean reward @3M | **-0.509** | **-0.164** (peaked +0.117 @2.95M) |
| probe: distance of a 10.44 m chase in 4 s | 0.99 m (9%) | **3.61 m (35%)** |
| probe: max / mean speed | 0.58 / ~0.5 m/s | **1.49 / 0.90 m/s** |
| probe: reached the ball | never | **never** |
| eval blue wins (n=510) | 16-17% | 17.8% |
| eval red wins | 65-69% | **53.9%** |
| eval stalemates | 14-17% | 28.2% |

Read that honestly. The frame fix is real and large — roughly **12x sample efficiency** (p18 matched p17's 3M endpoint at 250k), 3.6x the ground covered, and the first positive mean reward in this project's history. It also stopped the bleeding: red's win rate fell 65-69% -> 53.9%. But **the win rate did not move** (17.8% vs a 16-17% plateau, inside noise at n=510), because those losses became draws, not wins.

Two reasons the win rate is still pinned, both now measured rather than assumed:

1. **The curriculum still never promotes.** p18 finished on `Lesson0_Feeble` (bot_strength 0.2), exactly like p17. Eval grades against strength **1.0**, an opponent no policy in this project has ever trained against. Training reward and eval win rate are measuring two different opponents; do not treat them as one axis.
2. **The policy will not commit to full throttle.** The probe reads `mean|move| = 0.320` with only 19 sign flips in 400 steps — that is the policy *mean* under inference, not exploration noise. 236 N x 0.32 x 1.6 gain against 0.7 damping gives ~2 m/s terminal, matching the measured 1.49. Nothing in the reward table paid for hurrying: the proximity term is differential, so it telescopes to `ballProximityScale * (dStart - dEnd)` and pays the same for a 2-second approach as a 20-second one.

**Rejected 2026-08-29 — gamma 0.999 (`soccer_p19gamma_standard`, single variable vs p18).** The hypothesis was sound on paper: at gamma 0.99 the horizon is `1/(1-0.99)` = 100 decisions, and at DecisionRequester period 8 on a 0.01 s timestep that is ~8 s of game time, so a terminal reward 560 decisions away arrives multiplied by ~0.004. Raising it to 0.999 (~80 s horizon) made things **worse**: mean reward over the final 1M steps was **-0.308 vs p18's -0.139**, behind at every checkpoint from 200k on. Longer horizons cost value-estimate variance and this task could not pay for it at 3M steps. The short horizon is real but it is not the binding constraint.

**RESULT 2026-08-29 — p20, stepPenalty restored at -0.00005 (`soccer_p20step_standard`, single variable vs p18; the change is in the reward assets, the trainer config is byte-identical).**

| | p17 world frame | p18 body frame | **p20 body + stepPenalty** |
|---|---|---|---|
| probe: distance of a 10.44 m chase in 4 s | 0.99 m (9%) | 3.61 m (35%) | **5.24 m (50%)** |
| probe: max speed | 0.58 m/s | 1.49 m/s | **2.20 m/s** |
| probe: mean speed | ~0.5 m/s | 0.90 m/s | **1.31 m/s** |
| probe: policy `mean\|move\|` | — | 0.320 | **0.388** |
| probe: reached the ball | never | never | **never** |
| eval blue wins | 16-17% | 17.8% (n=510) | 14.6% (n=350) |
| eval red wins | 65-69% | 53.9% | **53.7%** |
| eval stalemates | 14-17% | 28.2% | 31.7% |

The time cost did what it was predicted to do: the policy committed harder (`mean|move|` 0.320 -> 0.388) and covered 45% more ground. Across the two fixes locomotion is **5.3x** p17 on distance and **3.8x** on top speed.

**But the win rate still has not moved, and after three runs the reason is no longer a hypothesis.** Every run since p17 finishes on `Lesson0_Feeble` (bot_strength 0.2) while eval grades against strength **1.0**. No policy in this project has ever trained against a competent opponent. What both fixes bought was converting *losses* into *draws* (red 65-69% -> 53.7%, stalemates 14-17% -> 31.7%) — the agent learned not to lose long before it can learn to win, because not-losing is what a 0.2-strength curriculum rewards.

Do not read the 17.8% -> 14.6% step as a regression: at n=510 and n=350 the combined SD is ~2.7 pp, so a 3.2 pp gap is ~1.2 SD, exactly the kind of difference this file already warns never to treat as signal. p20 is deployed over p18 on the strength of the probe, which is the measurement that actually discriminates.

**The next lever is the curriculum, not the reward table and not the network.** The `bot_strength` ladder needs a promotion criterion the agent can actually satisfy at its current skill, or the eval opponent needs to match the training opponent. Restoring a *falling* threshold is not the answer (that is how p5 graduated on noise); the honest options are a longer run so a flat 0.50 can genuinely be reached, or grading against the strength actually trained on so training and eval stop measuring different opponents.

**RESULT 2026-08-29 — p21 BREAKS THE PLATEAU. 25.7% over 350 episodes against 16-17% for every run since 2026-08-04.**

`soccer_p21curric_standard`, 10M steps. Two changes, one of them a correction:

- **Correction: bot_strength thresholds 0.50 -> 0.21.** Promotion uses `measure: reward`, evaluated on the same reward p20's stepPenalty depresses. Eval measured `meanEpisodeSteps` 5784, so the time cost is `5784 * 0.00005 = 0.289` per episode: a 0.50 threshold on p20's scale silently demanded what 0.79 demanded on p18's. p20 made promotion *harder* while improving behaviour, which nobody intended. This restores the same effective bar. It is **not** the p5 falling ladder - that dropped the bar as difficulty rose; here every lesson keeps one flat value and `min_lesson_length` stays 1000.
- **Experiment: 3M -> 10M steps**, so the ladder has room to be climbed.

**The curriculum climbed for the first time in this project's history** - three promotions where p17, p18 and p20 all made zero:

| step | lesson | bot_strength | mean reward |
|---|---|---|---|
| 0 | Lesson0_Feeble | 0.2 | — |
| ~5.45M | **Lesson1_Weak** | 0.35 | +0.676 |
| ~6.6M | **Lesson2_Half** | 0.5 | +0.940 |
| ~9.05M | **Lesson3_Capable** | 0.65 | — |
| 10M final | | 0.65 | **+0.690** |

Lesson2 matters specifically: the bot's support positioning and corner craft switch on at 0.5, so that is the first genuinely competent opponent any policy here has trained against.

Full arc of the four fixes, all measured the same way:

| | p17 world | p18 body | p20 +step | **p21 +curriculum** |
|---|---|---|---|---|
| probe: 10.44 m chase in 4 s | 0.99 m (9%) | 3.61 m (35%) | 5.24 m (50%) | **8.48 m (81%)** |
| probe: max speed | 0.58 | 1.49 | 2.20 | **3.17 m/s** |
| probe: `mean\|move\|` | — | 0.320 | 0.388 | **0.658** |
| probe: sign flips / 400 | — | 15 | 15 | **1** |
| eval blue wins | 16-17% | 17.8% | 14.6% | **25.7%** |
| eval red wins | 65-69% | 53.9% | 53.7% | **49.7%** |
| eval stalemates | 14-17% | 28.2% | 31.7% | **24.6%** |

**8.6x the ground covered and +9 points of win rate.** At n=350 the SD is ~2.3 pp, so 25.7% against a 16-17% plateau is roughly 4 SD - this one is signal, unlike every <10-point gap this file warns about. One sign flip in 400 steps means the policy now picks a direction and commits, and `mean|move|` 0.658 against `mean|lat|` 0.343 means it finally drives rather than strafes.

**Still far from the bar** (>=80% wins, <=10% stalemates) and the probe still reads `arrival=-1.00s` - 8.48 m of a 10.44 m chase in the 4-second window, so it very nearly arrives but not quite. The obvious continuations, in order: let the ladder run past Lesson3 (it was still climbing at 10M), then revisit `ActionGain = 1.6`, which clamps after multiplying and denies the policy any magnitude between 0.625 and 1.0 - a far more costly restriction now that it actually wants to output 0.658.

**Rejected: gamma 0.999** — see p19 above. **Untested and next in line:** `ActionGain = 1.6` in `Agent_Soccer.OnActionReceived`, a band-aid added when the policy crept. It multiplies then clamps, so the policy cannot express any magnitude between 0.625 and 1.0; with the frame fixed it may now be costing resolution rather than buying force.

**RESULT 2026-09-07 — p22 ran both of those continuations and NEITHER moved the win rate. The curriculum explanation is now closed.**

`soccer_p22gain_standard`, 10M steps, `ActionGain` 1.6 → 1.0, everything else byte-identical to p21. It did what the paragraph above asked for: the ladder ran past Lesson3 all the way to **Lesson5_Full (`bot_strength` 1.0)** — the first policy in this project's history to train against the same opponent `evaluate.ps1` grades on.

| | p21 | p22 |
|---|---|---|
| ActionGain | 1.6 | **1.0** |
| curriculum reached | Lesson3_Capable (0.65) | **Lesson5_Full (1.0)** |
| blue wins (n=350) | 25.7% | **26.6%** |
| red wins | 49.7% | 46.0% |
| stalemates | 24.6% | 27.4% |

**+0.9 pp on a 3.3 pp SD of the difference — 0.26 SD, noise** by this file's own <10-point rule. The grade is sound: the eval JSON's `modelPath` is the deployed `STANDARD.onnx`, `modelWrittenUtc` precedes `playerBuiltUtc`, and `modelInputs` sums to 178.

Read the second row carefully, because it retires the thesis this section has argued since p17. "No policy in this project has ever trained against a competent opponent" was the stated reason two large locomotion wins converted losses into draws instead of wins. **A policy has now trained against the full-strength bot and grades the same.** Train/eval opponent mismatch is no longer an available explanation. Keep `ActionGain = 1.0` — the dead-band argument for it is still correct — but it is not the lever either.

**FOUND 2026-09-07 — the dense reward table could outrank the objective it shapes toward.** Nobody had ever computed what a dense term accumulates over a whole episode. `OnActionReceived` runs every physics step (ML-Agents repeats the last action between decisions), so each term is charged up to 9000 times:

| term | old | ceiling/episode | vs terminal |
|---|---|---|---|
| `ballToGoalVelocityScale` | 0.001 | **+9.0** | 7.5× a goal (+1.2) |
| `cornerBallPenalty` | 0.0006 | **−5.4** | 5.4× conceding (−1.0) |
| `wallProximityPenalty` | 0.0005 | **−4.5** | 4.5× conceding |
| `crossbarProximity` | 0.0005 | **+4.5** | 3.75× a goal |
| `possessionScale` (NICK) | 0.0006 | **+5.4** | 4.5× a goal |
| `defensivePositionScale` (KIM) | 0.0006 | **+5.4** | 4.2× KIM's goal |

NICK was paid up to 4.5× more for holding the ball than for scoring with it. These are maxima — real duty cycles are lower — but a reachable ceiling means the table does not guarantee the ordering of outcomes. **The method was already here and simply never generalised:** `stepPenalty`'s own comment does this exact arithmetic ("9000 steps, −0.9, near-identical to `goalConceded`"). Validated against an independent figure — CLAUDE.md's own `5784 × 0.00005 = 0.289` reproduces exactly.

Fixed: every per-step term ÷10 with trait ratios preserved, `facingAlignmentScale` and `crossbarProximity` retired as redundant (bearing to the ball has been an *observation* since the body-frame fix). Code defaults and all five assets moved together — a changed initializer never reaches an existing asset. `Agent_EditMode_RewardBudget` pins the **ordering property**, not a specific table, and fails if a `maxEnvironmentSteps` increase re-breaks it (the ceiling is linear in the cap). Full write-up: `docs/ml-audit-2026-09-07.md`.

**RESULT 2026-09-07 — p24 (potential-based shaping) is a WASH and p26 (imitation) is significantly WORSE.**

| run | lever | graded (n=350) | red | stalemates |
|---|---|---|---|---|
| `soccer_p22gain_standard` | ActionGain 1.0 + full curriculum | **26.6%** | 46.0% | 27.4% |
| `soccer_p24potential_standard` | reward budget caps + potential-based ball→goal | *ungraded*; **+0.049 vs p22** at matched 2.6–3.0M — wash | | |
| `soccer_p26imitate_standard` | BC 500k + GAIL 0.3 vs bot demos | **12.6%** | 53.7% | 33.7% |

p26 is −14.0 pp on a combined SD of ~3.0 — about **4.7 SD**. Imitation did not fail to help, it **halved the win rate**. Likely causes, all unmeasured hypotheses: covariate shift (demos are bot-vs-bot; the learner faces bot-vs-itself *while losing*, drifting into states the demonstrator never visited — the failure DAgger exists for), GAIL rewarding resemblance to a state distribution the agent cannot reach, and BC anchoring the policy RL then spends budget undoing. Separating them needs an ablation, not another combined run.

Full record, including the reward-budget audit and the sensor-geometry measurements: `docs/ml-audit-2026-09-07.md`.

**Measured 2026-08-04** (multi-run means, rebuilt player per model — `results/eval/*.json`):

| Agent / run | Steps | n×100 ep | Blue wins | Range | Stalemates |
|---|---|---|---|---|---|
| `baseline` (bot vs bot) | — | 40 ep | 42.5% | — | 15% |
| STANDARD `soccer_p5_paced_00` | 30.0M | 10 | **16.2%** | 11–24 | 14.6% |
| MATT `soccer_v2_matt` | 2.5M | 4 | 17.2% | 10–24 | 13.5% |
| NICK `soccer_v2_nick` | 2.5M | 4 | 17.2% | 12–21 | 12.2% |
| KIM `soccer_v2_kim` | 2.5M | 4 | 16.2% | 14–19 | 13.8% |

Bot-vs-bot is symmetric, so the harness is fair and the brains really are **worse than the rule-based bot**: parity is ~42.5%, every brain sits at 16–17%. **All four personalities are statistically indistinguishable from each other** despite 12× different step counts (2.5M vs 30M) — which is itself the finding. Bar 80%/≤10% badly unmet.

**Compute has stopped buying wins.** `p4` (7.0M) and `p5` (30.0M) both mean ~16–17%; 4.3× the steps bought nothing. p5 reached the top `bot_strength` lesson while its reward oscillated between −1.07 and +0.52 — it graduated on noise, because the phase-5 threshold ladder still *fell* as difficulty rose (0.40 → 0.25) despite its own header claiming it had inverted that. Treat "train it longer" as a disproven lever.

**Why the bot wins: the agent cannot see it.** `Agent_Soccer.CollectObservations` is Self(4) + Stamina(1) + Ball(4) + Goals(5) + Teammate(4) = 18 — **there is no opponent term at all**. The policy's only opponent channel is `Sensor_Vision`: 11 rays, 30° apart (300° arc ⇒ a 60° blind wedge behind), 24 range, spherecast radius 0.1, and every player is tagged `Agent` so the rays cannot even separate teammate from opponent. Agents are 0.8 units wide, so effective detection half-width is 0.4 + 0.1 = 0.5, while the blind gap between adjacent rays grows as `d·sin15° = 0.259d` — an opponent is **guaranteed visible only within ~1.9 units**, and beyond that can sit entirely between two rays. On the 36×54 pitch (both `SCN_Training` and `SCN_Exhibition`) that reliable disc is ~12 of 1944 sq units ≈ **0.6% of the pitch**; ray length 24 is itself shorter than the 54-unit pitch length. `Agent_HeuristicBot.ComputeActions` meanwhile receives `nearestOpponent` as a live `Rigidbody2D` — exact position *and* velocity, unlimited range, no angular quantization, no occlusion. The 120°→300° widening (commit `7e03878`) traded angular resolution for coverage and moved the result 0 points, which is consistent with this: the gap is opponent *state*, not arc.

**RESULT — phase 6 changed nothing, and the diagnostic found the real cause.** `soccer_p6_seeing_00` (20.0M steps, 26 obs) graded **17.1%** over 1000 episodes against the previous brain's **16.2%** over 1000 — a 0.9-point difference against ±1.8 combined uncertainty, i.e. no improvement. Stalemates rose 14.6% → 17.0%. Training reward went from −0.045 to +0.45 and **none of it transferred**.

**The decisive experiment** graded the *same* p6 policy against the half-strength bot it actually trained on:

| | bot 1.0 | bot 0.5 |
|---|---|---|
| Blue wins | 17.1% | **17.4%** |
| Red wins | 65.9% | 34.4% |
| Stalemates | 17.0% | **48.2%** |
| Mean steps | 4,524 | 6,358 |

Halving the opponent left the win rate **flat** and converted 31 points of losses into draws, one-for-one. The scoring rate is pinned near 17% regardless of opponent — an **offense** problem, not perception and not opponent strength.

**Cause: the reward table made stalling optimal.** With `goalScorer 0.7 / goalConceded -1.0 / stalemateTimeout -0.1`, EV(stall) = −0.1 while EV(attack, 50/50) = −0.15. A policy needed a **>53% win rate before attacking beat parking the bus**, and it wins ~17%. It learned not to lose because that is what the table paid for. Fixed 2026-08-04 in all five profiles: `goalScorer` 1.2 (MATT 1.4, KIM 1.3), `stalemateTimeout` −0.6 — attacking now beats stalling even at a 30% win rate (−0.34 vs −0.60). Config-only; every 26-obs `.onnx` stays valid. `config/STANDARD_phase7_scoring.yaml` is the single-variable test.

**Process lesson: the perception thesis below was diagnosed from code inspection and never tested before committing 3 hours of compute to it.** The 10-minute reduced-strength eval would have falsified it up front. Run the cheap discriminating experiment *before* the expensive fix.

**Fixes applied 2026-08-04 — the perception/reward/curriculum set that phase 6 tested and found insufficient:**
1. **Opponent observations** — obs 18 → 26 (`Agent_EnvController.GetOpponents`, zero-alloc, nearest-first). Removes the perception asymmetry above. Obsoletes every `.onnx`.
2. **`ballContact` 0.05 → 0.005** — at 0.05, **14 touches outscored a goal** (0.7), so over a ~4400-step episode the optimal policy was to poke the ball rather than finish. Matches the stubborn 12–18% stalemate rate. Now 140 touches per goal.
3. **Curriculum gate** — `config/STANDARD_phase6_seeing.yaml`: one flat mastery threshold (0.50) on every lesson and `min_lesson_length` 200 → 1000, replacing the falling ladder that let p5 graduate on noise.
4. **`evaluate.ps1 -Episodes` 100 → 1000** — see the variance landmine above.

Also available: `Agent_HeuristicBot.perceptionRadius` (env `POSOCCER_BOT_VISION`, default **0 = unlimited**, preserving every historical result) caps how far the bot perceives opponents — the knob for asking whether the 80% bar is reachable at all or is just the perfect-information gap.

Phase 2 POCA self-play is *not* indicated — runs `v2`/`v3`/`p3` were effectively symmetric self-play and scored 15–19%.

**RESULT — phase 7 also moved nothing, and the probe finally found the real cause.** `soccer_p7_scoring_00` (20.0M, reward table fixed, single-variable vs p6) graded **16.6%** over 1000 episodes against p6's 17.1% and the old brain's 16.2%. The reward fix did work behaviourally — stalemates 17.0% → 14.4% — but those draws became **losses** (red 65.9% → 69.0%), not wins. Three runs, three hypotheses, one flat ~16–17%.

**The agent never learned to drive.** `Agent_PlayMode_MovementProbe` measures the policy directly; nobody had run it against a trained brain before 2026-08-04 because everything was graded headless on aggregate counters:

| 4 s chase, 10.44 m away | trained policy | scripted bot | chassis capability |
|---|---|---|---|
| distance travelled | **0.99 m (9%)** | 15.08 m (144%) | — |
| reached the ball | **never** | 2.70 s | — |
| top speed | **0.58 m/s** | 5.16 m/s | 9.54 m/s |
| heading churn | 184° | 84° | — |

It creeps and spins at ~6% of the chassis' ability. Every "it can't finish / can't see / won't attack" theory was built on top of a policy that could not cross the pitch.

| term | code | STANDARD | KIM | effect |
|---|---|---|---|---|
| `ballProximityScale` (reward for closing on the ball) | 0.002 | 0.0004 | 0.0002 | 5–10× too weak |
| `actionJitterScale` (penalty for changing action) | 0.0004 | 0.001 | 0.002 | 2.5–5× too strong |
| `stepPenalty` (v2 zeroed it) | 0 | −0.00003 | −0.00003 | all five drifted |

Net **12× swing against moving** on STANDARD, **50× on KIM**. Standing still was the local optimum and 20M steps found it. Fixed in all five profiles 2026-08-04 and pinned by `RewardProfiles_MatchCodeDefaultsOnMechanics` (EditMode) — that test is the guard, keep it green. Personality lives in terminal rewards, trait scales and physique; the locomotion mechanics must match code.

- **Every eval graded the p9 brain**, whatever the filename said. The three phase-10 results (16.4 / 19.0 / 17.8 over 1000 ep) are three repeat samples of one unchanged model — mean 17.7%, spread 2.6 pp against SD ≈ 1.2. The published "3M POCA = 19.0%, first run to break the plateau, +2.6 pp real" compared two samples of the same thing. Retracted in `docs/eval-p10-poca.md`.
- **Every training run used the same stale env** (`env_path` in all three `configuration.yaml`), so the 4-sensor split and terminal-reward shaping that phase 10 exists to test **never executed once**. They are untested, not disproven.
- **Regraded 2026-08-12**: a player built from the 118-obs code the brain was actually trained on (`43e3385`) puts the real 3M POCA brain at **18.5%** (185/1000, 64.9% red, 16.6% stalemate) vs the p9 brain's 17.7% — **+0.8 pp on a combined SD of ~1.7. POCA did not break the plateau.**
- **ELO said so all along.** Per UNITY_RULES, judge self-play on ELO, not mean reward: `Self-play/ELO` was **1200.5 flat** from initial 1200.0 through 9.28M steps (and absent entirely from the 3M run), with `Mean Group Reward` identically 0.000 — MA-POCA's group-credit channel carried no signal. Mean reward climbed to 0.86 while the rating never moved.

Guards added the same day: `evaluate.ps1` hard-fails on a stale build and stamps `modelInputs` / `modelPath` / `modelWrittenUtc` / `playerBuiltUtc` into every eval JSON (the phase-10 JSONs recorded a run id and a win rate and nothing that could falsify them); `build-headless.ps1` no longer reports `OK` for a build that never ran and refuses to start while the editor holds the lock. **Verify a rebuild by `PoSoccer_Data/*.assets`, and confirm the editor compiled what you think** — reflecting on `PoSoccer.Sensor_Vision.RaysPerDirection` (present only in the pre-split version) is the cheap check.

**Process that now applies to every run: pilot then gate.** `config/STANDARD_phase8_pilot.yaml` is 3M steps (~25 min) instead of 20M (~2.9 h); export, run the probe, and only commit to a full run if the agent actually reaches the ball. p8 promoted a lesson at ~2M and reached **+0.648** where p7 never promoted in 20M and ended +0.281 — an 8×+ sample-efficiency gain from the locomotion fix. **Unresolved:** the p8 probe still reads 0.99 m / 0.58 m/s, so training reward and measured locomotion still disagree. Do not launch a full run until that contradiction is explained.

**RESULT — phase 16: MA-POCA group rewards never fired, and fixing that changed nothing (2026-08-28).** `Agent_EnvController.Start` built `SimpleMultiAgentGroup`s only when a side had >1 player. `SCN_Training` is 1v1, so the guard never tripped, `_blueGroup`/`_redGroup` stayed null, and the `AddGroupReward`/`EndGroupEpisode` branches in `OnGoalScored`/`OnStalemate` never ran. **Every POCA run in this project's history — p10, p15 — trained with an empty group-credit channel.** Groups are now always registered (`0dda102`).

Verified by running it: `soccer_p16_poca1v1` (720k steps) moved `Environment/Group Cumulative Reward` off zero for the first time, −0.55 → −0.23. **The result is still negative** — `Self-play/ELO` fell **1178 → 579**, and the self-play variant `soccer_p16b_poca_selfplay` emits no `Self-play/ELO` tag at all. Both fail the gate written into `STANDARD_phase2_poca.yaml`'s own header. Group credit was a real defect; fixing it bought no skill. That points back at the unresolved locomotion failure, not at credit assignment.

---

