# KIM in a 30-minute budget: seed from NICK's finished policy, then fine-tune on
# KIM's own reward DNA.
#
# WHY NOT FROM SCRATCH. 30 minutes buys ~2.7M steps. Cold starts in this project
# need 5.45M (STANDARD) to 5.8M (MATT) just to clear Lesson0_Feeble, so a
# from-zero KIM would finish as an RL brain that cannot play - technically not a
# heuristic bot, practically worse than one.
#
# HOW THE SEED WORKS. mlagents' --initialize-from reads
# results/<run-id>/<behavior-name>/checkpoint.pt, keyed by BEHAVIOR name, so it
# cannot be pointed at soccer_p21_nick (behavior "NICK") from a run whose
# behavior is "KIM". The seed run below is a directory with NICK's weights filed
# under a KIM/ folder. That is legitimate here because all four brains share one
# network architecture and one obs/action contract - the .pt is a plain state
# dict, not something behavior-specific.
#
# WHAT THIS MEANS FOR THE RESULT. KIM is then NICK-derived rather than
# independently trained. Its personality still comes from its own reward table
# (goalConceded -1.2, defensivePositionScale 0.0006) applied during fine-tuning,
# but it is transfer learning, not a clean-room run, and should be described that
# way. A from-scratch KIM needs ~2 hours.
param(
    [int]$Minutes = 25,
    [int]$NumEnvs = 4
)

$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$trainer = Join-Path $root '.venv2\Scripts\mlagents-learn.exe'
$player  = Join-Path $root 'Builds\PoSoccer\PoSoccer.exe'
$config  = Join-Path $root 'config\TRAIN_KIM_p21.yaml'
$seedSrc = Join-Path $root 'results\soccer_p21_nick\NICK\checkpoint.pt'
foreach ($p in @($trainer, $player, $config, $seedSrc)) {
    if (-not (Test-Path $p)) { throw "missing: $p" }
}

# Build the seed run: NICK's weights filed under a KIM/ behavior folder.
$seedDir = Join-Path $root 'results\kim_seed\KIM'
if (Test-Path (Join-Path $root 'results\kim_seed')) {
    Remove-Item -Recurse -Force (Join-Path $root 'results\kim_seed')
}
New-Item -ItemType Directory -Force -Path $seedDir | Out-Null
Copy-Item $seedSrc (Join-Path $seedDir 'checkpoint.pt') -Force
Write-Host "seeded results/kim_seed/KIM/checkpoint.pt from NICK (7.0M steps)"

$env:POSOCCER_OPPONENT = 'bot'
$env:POSOCCER_PROFILE  = 'KIM'

Write-Host "fine-tuning KIM for $Minutes min at -NumEnvs $NumEnvs"
$proc = Start-Process -FilePath $trainer -PassThru -NoNewWindow -ArgumentList @(
    $config,
    '--run-id=soccer_p21_kim',
    '--initialize-from=kim_seed',
    '--force',
    '--base-port=5100',
    "--results-dir=$root\results",
    "--env=$player",
    '--no-graphics',
    "--num-envs=$NumEnvs"
)

$deadline = (Get-Date).AddMinutes($Minutes)
while (-not $proc.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 15 }
if (-not $proc.HasExited) {
    Write-Host 'budget reached - stopping trainer'
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 5
& (Join-Path $PSScriptRoot 'cleanup-training.ps1')
Remove-Item Env:\POSOCCER_PROFILE -ErrorAction SilentlyContinue
$env:POSOCCER_OPPONENT = ''

# A budget-stopped run never writes the run-root KIM.onnx update-model expects,
# so promote the highest checkpoint export by hand.
$ck = Get-ChildItem "$root\results\soccer_p21_kim\KIM" -Filter 'KIM-*.onnx' -ErrorAction SilentlyContinue |
      Sort-Object { [int]($_.BaseName -replace '^.*-', '') } | Select-Object -Last 1
if ($ck) {
    Copy-Item $ck.FullName "$root\results\soccer_p21_kim\KIM.onnx" -Force
    Write-Host "promoted checkpoint: $($ck.Name)"
    & (Join-Path $PSScriptRoot 'update-model.ps1') -RunId soccer_p21_kim -Profile KIM
} else {
    Write-Warning 'no KIM-*.onnx checkpoint found to promote'
}
Write-Host 'KIM RUN COMPLETE'
