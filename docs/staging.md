# Training staging manifest

Written 2026-10-10 for the 173-input contract (105 ray + 68 vector). No brain trained
before that date loads against it. Read `docs/training-history.md` before changing a
lever here; this page only says what runs, in what order, and how it is graded.

## Order

| # | Brain | Config | Algorithm | Steps | Starts from | Squad | Wall-clock estimate |
|---|---|---|---|---|---|---|---|
| 0 | bot vs bot | - | - | 350 episodes | - | 1, then 2 | minutes |
| 1 | STANDARD | `TRAIN_STANDARD_p30team.yaml` | MA-POCA vs the scripted bot | 10M | scratch | `1,2` | about 2 h or more |
| 2 | MATT | `TRAIN_MATT_p30team.yaml` | same | 10M | scratch | `1,2` | about 2 h or more |
| 3 | KIM | `TRAIN_KIM_p30team.yaml` | same | 10M | scratch | `1,2` | about 2 h or more |
| 4 | NICK | `TRAIN_NICK_p30team.yaml` | same | 10M | scratch | `1,2` | about 2 h or more |

The wall-clock figure is the 667 s per million steps measured on 1v1 at `-NumEnvs 4`.
Half the grid now plays 2v2, so expect it to be slower; nobody has measured by how much.

Rows 2 to 4 start from scratch because `train-all.ps1` has no warm-start option. Warm
starting them from row 1 (`train-phase1.ps1 -InitFrom soccer_p30team_standard` with
`POSOCCER_PROFILE` set) is the cheaper route and is untested.

## Commands

```powershell
.\scripts\tensorboard.ps1
.\scripts\evaluate.ps1 -Baseline -Episodes 350
.\scripts\evaluate.ps1 -Baseline -Episodes 350 -Squad 2
.\scripts\train-phase1.ps1 -RunId soccer_p30team_standard -Config TRAIN_STANDARD_p30team.yaml `
    -Squad "1,2" -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4
.\scripts\update-model.ps1 -RunId soccer_p30team_standard
# rebuild the player, then:
.\scripts\evaluate.ps1 -RunId soccer_p30team_standard -Episodes 350
.\scripts\evaluate.ps1 -RunId soccer_p30team_standard -Episodes 350 -Squad 2
```

## Checks during a run

- `PoSoccer/blue_squad` reads about 1.5 for `-Squad "1,2"`. A reading of 1.0 means the
  squad setting never reached the player: stop the run.
- `Reward/turn_churn` is negative and non-zero. Exactly 0 means the build predates the term.
- Mean reward is not a decision variable on this task. Judge on the eval and the probe.

## Gate for each brain

1. `Agent_PlayMode_MovementProbe` (blue trained side): it reaches the ball, and heading
   churn is nearer the bot's 83 degrees than the last trained brain's 174.
2. 1v1 eval at 350 episodes. The benchmark bar is at least 80% wins and at most 10%
   stalemates. The best graded brain so far is 26.6%.
3. 2v2 eval at 350 episodes, compared with the bot-vs-bot 2v2 baseline from row 0.
   This number has no history; never mix it with the 1v1 figure.

## Fixed choices

- Decision period 8 (12.5 Hz) and fixed timestep 0.01 s stay as they are.
- `threaded: false`. The `threaded: true` throughput A/B is `TRAIN_STANDARD_p28.yaml`,
  which still targets the old contract and needs porting before it can run.
- Superseded configs are in `config/archive/` and train the old 178-input contract.

## p34league: staged 2026-10-11, run and graded 2026-10-13 (52.3%, fails the bar)

| Brain | Config | Algorithm | Steps | Starts from | Opponent pool | Squad |
|---|---|---|---|---|---|---|
| STANDARD | `TRAIN_STANDARD_p34league.yaml` | PPO with `self_play` | 10M | `soccer_p33team_standard` | `-League`: past snapshots on half the pitches, bot v1 on the other half | 1v1 |

```powershell
# Rebuild the player first: the league seating is new code.
.\scripts\train-phase1.ps1 -RunId soccer_p34league_standard -Config TRAIN_STANDARD_p34league.yaml `
    -League -InitFrom soccer_p33team_standard -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4
```

- `PoSoccer/league_bot_pitch` reads about 0.5. A flat 0 means plain self-play: stop the run.
- `Self-play/ELO` is a liveness signal only here; the config header says why.
- Gate as for every brain: the probe, then 350 episodes at 1v1 against bot v1, compared
  with p33team's 37.1%.
- The run ends by deploying its brain over `STANDARD.onnx` (`update-model.ps1`), as every
  run does. p33team's export stays in `results/soccer_p33team_standard/`.
- Result: 183 wins, 71 losses, 96 stalemates at n=350 (52.3%), against p33team's 37.1%.
  Details and the missing control are in `docs/training-history.md`. Nothing is staged
  after this run.

## p35league: run 2026-10-13, graded 2026-10-14 (61.1%, fails the bar)

| Brain | Config | Algorithm | Steps | Starts from | Opponent pool | Squad |
|---|---|---|---|---|---|---|
| STANDARD | `TRAIN_STANDARD_p35league.yaml` | PPO with `self_play` | 10M | `soccer_p34league_standard` | `-League`, as p34league | 1v1 |

```powershell
.\scripts\train-phase1.ps1 -RunId soccer_p35league_standard -Config TRAIN_STANDARD_p35league.yaml `
    -League -InitFrom soccer_p34league_standard -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4
```

- The config is p34league's with a new starting brain and nothing else changed.
- Gate: the probe, then 350 episodes at 1v1 against bot v1, compared with p34league's
  52.3% wins and 27.4% stalemates.
- Result: 214 wins, 48 losses, 88 stalemates at n=350 (61.1%).

## p34control: run and graded 2026-10-14 (54.9%, level with p34league)

`TRAIN_STANDARD_p34control.yaml`: p34league's config without the `self_play` block, bot
on every pitch, warm-started from `soccer_p33team_standard`, 10M steps. It separates the
league pool from the extra steps in p34league's gain. Graded like every brain, against
p33team's 37.1% and p34league's 52.3%.
- Result: 192 wins, 68 losses, 90 stalemates (54.9%). The league pool added nothing over
  bot-only training from the same start.

## p36bot: run and graded 2026-10-14 (81.1% wins, 12.7% stalemates at n=1000)

| Brain | Config | Algorithm | Steps | Starts from | Opponent pool | Squad |
|---|---|---|---|---|---|---|
| STANDARD | `TRAIN_STANDARD_p36bot.yaml` | PPO | 10M | `soccer_p35league_standard` | bot v1 on every pitch | 1v1 |

```powershell
.\scripts\train-phase1.ps1 -RunId soccer_p36bot_standard -Config TRAIN_STANDARD_p36bot.yaml `
    -InitFrom soccer_p35league_standard -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4
```

- Gate: the probe, then 350 episodes against bot v1, compared with p35league's 61.1% wins
  and 25.1% stalemates.
- Result at n=1000: 811 wins, 62 losses, 127 stalemates. Wins pass the bar; stalemates
  fail it by 2.7 points.

## p37bot: run and graded 2026-10-14. PASSES THE BENCHMARK.

`TRAIN_STANDARD_p37bot.yaml`: the same bot-only recipe, warm-started from
`soccer_p36bot_standard`, 10M steps. Grade at n=1000 against p36bot's 81.1% / 12.7%.

```powershell
.\scripts\train-phase1.ps1 -RunId soccer_p37bot_standard -Config TRAIN_STANDARD_p37bot.yaml `
    -InitFrom soccer_p36bot_standard -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4
```
- Result over two 1,000-episode evals: 1,819 wins, 61 losses, 120 stalemates (91.0% wins,
  6.0% stalemates). Both bars met. Deployed as `STANDARD.onnx`. Nothing is staged after it.
