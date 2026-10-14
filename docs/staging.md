# Training staging manifest

What to train next, how to launch it, and how it is graded. The run-by-run record, with
every grade, is `docs/training-history.md`; read it before changing a lever here. Which
brain is deployed is in each `Reward_*.asset` (`trainingRunId`, `evalWinRate`).

All of this is the 173-input contract (105 ray + 68 vector, 2026-10-10). No brain trained
before that date loads against it.

## Status

STANDARD passed the benchmark on 2026-10-14 (`soccer_p37bot_standard`: 91.0% wins, 6.0%
stalemates over 2,000 episodes against bot v1). **Nothing is staged.**

Candidates, none started:

- **MATT, KIM, NICK on the STANDARD line.** Their deployed brains are the p31 runs (10M
  steps). STANDARD reached the bar with about 40M more steps of 1v1 PPO against the bot
  (the last 20M bot-only). The p31 recipe is already 1v1, bot-only, no team terms, so the
  natural step is continuing each from its p31 checkpoint with a copy of its p31 config.
  Untested plumbing: `train-phase1.ps1` has no profile switch (`train-parallel.ps1` sets
  `POSOCCER_PROFILE`; neither takes a warm start), and the p31 runs sit in
  `results/_archive/`, where `--initialize-from` will not look; move them back first.
- **A 2v2 grade of p37bot** (`evaluate.ps1 -Squad 2`). There is no 2v2 bar; compare it
  with the bot-vs-bot 2v2 baseline, never with the 1v1 figure.

## The current recipe

`config/TRAIN_STANDARD_p37bot.yaml`: PPO, 1v1, bot v1 at full strength on every pitch, no
team terms, no self-play, 10M steps per run, warm-started from the previous run. It runs
at about 540 s per million steps at `-NumEnvs 4`.

```powershell
.\scripts\tensorboard.ps1
.\scripts\train-phase1.ps1 -RunId <run> -Config <config> -InitFrom <previous run> `
    -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4
# the script deploys over the profile's .onnx at the end; rebuild the player, then:
.\scripts\evaluate.ps1 -RunId <run> -Episodes 1000
```

## Checks during a run

- `PoSoccer/blue_squad` reads 1.0 on a 1v1 run and about 1.5 for `-Squad "1,2"`. On a
  squad run, 1.0 means the setting never reached the player: stop the run.
- On a `-League` run, `PoSoccer/league_bot_pitch` reads about 0.5; a flat 0 means plain
  self-play. (League training is archived: p34control showed it added nothing.)
- `Reward/turn_churn` is exactly 0 unless the config names the term.
- Mean reward is not a decision variable on this task. Judge on the eval and the probe.

## Gate for each brain

1. `Agent_PlayMode_MovementProbe`, blue trained side: it reaches the ball.
2. 1v1 eval against bot v1. The bar is at least 80% wins and at most 10% stalemates.
   Use 1,000 episodes once a brain is within about 5 points of either bar; at 350 the
   standard deviation is about 2.7 points.
3. Optional: bot v2 (`-BotVersion 2`, no bar) and 2v2 (`-Squad 2`, no bar).

## Configs

- `config/` holds only the recipes behind deployed brains: `TRAIN_STANDARD_p37bot.yaml`
  and `TRAIN_<MATT|KIM|NICK>_p31.yaml`.
- `config/archive/` holds everything superseded, including the league, control and team
  recipes. The training scripts look in `config/` first and then in `config/archive/`, so
  an archived recipe still runs by name.
- Decision period 8 (12.5 Hz) and fixed timestep 0.01 s stay as they are. `threaded:
  false`; the `threaded: true` A/B (`archive/TRAIN_STANDARD_p28.yaml`) targets the old
  contract and needs porting before it can run.
