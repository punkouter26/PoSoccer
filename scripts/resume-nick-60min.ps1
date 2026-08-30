# One-off: resume NICK from its existing checkpoint for a fixed wall-clock budget.
#
# Written as a FILE rather than an inline -Command string on purpose: passing a
# multi-line script through Start-Process -ArgumentList mangles quoting, which
# silently produced two dead launches on 2026-08-29.
#
# train-all.ps1 has no -Resume switch, so mlagents-learn is invoked directly here.
# The stale-build guard is deliberately bypassed: the only files newer than the
# player are Agent_MainMenu.cs, PoSoccerTheme.uss and Reward_MATT.asset, none of
# which SCN_Training references and none of which a NICK run reads.
param(
    [int]$Minutes = 57,
    [int]$NumEnvs = 4
)

$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$trainer = Join-Path $root '.venv2\Scripts\mlagents-learn.exe'
$player  = Join-Path $root 'Builds\PoSoccer\PoSoccer.exe'
$config  = Join-Path $root 'config\TRAIN_NICK_p21.yaml'

foreach ($p in @($trainer, $player, $config)) {
    if (-not (Test-Path $p)) { throw "missing: $p" }
}

# Base 5100 keeps the worker range clear of adb's 5037 (see train-all.ps1).
$env:POSOCCER_OPPONENT = 'bot'
$env:POSOCCER_PROFILE  = 'NICK'

Write-Host "resuming NICK for $Minutes min at -NumEnvs $NumEnvs"
$proc = Start-Process -FilePath $trainer -PassThru -NoNewWindow -ArgumentList @(
    $config,
    '--run-id=soccer_p21_nick',
    '--resume',
    '--base-port=5100',
    "--results-dir=$root\results",
    "--env=$player",
    '--no-graphics',
    "--num-envs=$NumEnvs"
)

$deadline = (Get-Date).AddMinutes($Minutes)
while (-not $proc.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 15 }

if (-not $proc.HasExited) {
    Write-Host "budget reached - stopping trainer"
    # checkpoint_interval is 1,000,000 in TRAIN_NICK_p21.yaml, so every whole
    # million already has a .pt AND a .onnx on disk. A hard stop therefore loses
    # at most the steps since the last million, never the run.
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}

Start-Sleep -Seconds 5
& (Join-Path $PSScriptRoot 'cleanup-training.ps1')

Remove-Item Env:\POSOCCER_PROFILE -ErrorAction SilentlyContinue
$env:POSOCCER_OPPONENT = ''

# Promote the highest checkpoint export into NICK's GUID-stable slot, since a
# budget-stopped run never writes the run-root NICK.onnx that update-model wants.
$ck = Get-ChildItem "$root\results\soccer_p21_nick\NICK" -Filter 'NICK-*.onnx' -ErrorAction SilentlyContinue |
      Sort-Object { [int]($_.BaseName -replace '^.*-', '') } | Select-Object -Last 1
if ($ck) {
    Copy-Item $ck.FullName "$root\results\soccer_p21_nick\NICK.onnx" -Force
    Write-Host "promoted checkpoint: $($ck.Name)"
    & (Join-Path $PSScriptRoot 'update-model.ps1') -RunId soccer_p21_nick -Profile NICK
} else {
    Write-Warning 'no NICK-*.onnx checkpoint found to promote'
}
Write-Host 'RESUME RUN COMPLETE'
