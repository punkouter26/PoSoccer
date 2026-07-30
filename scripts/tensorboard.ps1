# Observability (UNITY_RULES): restart TensorBoard, cleaning unused items first.
# "Unused" = result dirs with no checkpoints/events, plus any already-running TB session.
param([int]$Port = 6006)

$ErrorActionPreference = "SilentlyContinue"
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root "results"

# 1. Kill existing TensorBoard sessions (always restart clean).
# Match the tensorboard EXECUTABLE only - never this script's own shell
# (whose command line also contains the word "tensorboard").
Get-CimInstance Win32_Process |
    Where-Object { $_.ProcessId -ne $PID -and
        ($_.Name -match "^tensorboard" -or
         ($_.Name -match "^python" -and $_.CommandLine -match "tensorboard")) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -Confirm:$false }

# 2. Prune stale runs (no events and no model = dead weight) and archive old ones:
#    only the newest $KeepRuns stay visible in TensorBoard; older move to _archive/.
$KeepRuns = 3
if (Test-Path $results) {
    $runs = Get-ChildItem $results -Directory | Where-Object { $_.Name -notin @("eval", "_archive") }
    foreach ($run in $runs) {
        $hasEvents = Get-ChildItem $run.FullName -Recurse -Filter "events.out.tfevents.*" | Select-Object -First 1
        $hasModel = Get-ChildItem $run.FullName -Recurse -Include "*.onnx", "*.pt" | Select-Object -First 1
        if (-not $hasEvents -and -not $hasModel) {
            Write-Host "Pruning stale run: $($run.Name)"
            Remove-Item $run.FullName -Recurse -Force
        }
    }
    $live = Get-ChildItem $results -Directory | Where-Object { $_.Name -notin @("eval", "_archive") } |
        Sort-Object LastWriteTime -Descending
    if ($live.Count -gt $KeepRuns) {
        New-Item -ItemType Directory -Force (Join-Path $results "_archive") | Out-Null
        $live | Select-Object -Skip $KeepRuns | ForEach-Object {
            Write-Host "Archiving old run: $($_.Name)"
            Move-Item $_.FullName (Join-Path $results "_archive\$($_.Name)") -Force
        }
    }
}

# 3. Relaunch (venv exe called directly - PATH-independent, works detached).
$ErrorActionPreference = "Stop"
Write-Host "TensorBoard -> http://localhost:$Port (reward convergence / policy entropy / value loss)"
& "$root\.venv\Scripts\tensorboard.exe" --logdir $results --port $Port --bind_all
