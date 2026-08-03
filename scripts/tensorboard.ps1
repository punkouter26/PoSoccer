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
#    Backgrounded so the script can self-verify the port is bound before
#    returning. Stdout/stderr go to results/tensorboard.log so the user can
#    still tail it (`Get-Content results/tensorboard.log -Wait`).
$ErrorActionPreference = "Stop"
$tbExe   = "$root\.venv\Scripts\tensorboard.exe"
$tbLog   = Join-Path $root "results\tensorboard.log"
if (-not (Test-Path $tbExe)) { throw "TensorBoard not found at $tbExe. Run .\scripts\setup-training-env.ps1 first." }
$proc = Start-Process -FilePath $tbExe -ArgumentList @("--logdir", $results, "--port", "$Port", "--bind_all") `
                      -PassThru -RedirectStandardOutput $tbLog -RedirectStandardError "$tbLog.err" `
                      -WorkingDirectory $root

# 4. Self-verify reachability (UNITY_RULES 4: track active TensorBoard sessions).
#    Give TB up to 10 s to bind the port. Reachability surfaces bind failures
#    (port already in use, missing logdir) instead of letting training run blind.
$tbReady = $false
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 500
    try {
        $resp = Invoke-WebRequest -Uri "http://localhost:$Port/" -UseBasicParsing -TimeoutSec 2
        if ($resp.StatusCode -eq 200) { $tbReady = $true; break }
    } catch { /* still starting or failed */ }
}
if (-not $tbReady) {
    Write-Warning "TensorBoard did not respond on http://localhost:$Port/ within 10 s. Process $($proc.Id) left running for debugging. See $tbLog.err."
} else {
    Write-Host "TensorBoard -> http://localhost:$Port (PID $($proc.Id), log: $tbLog) - reward convergence / policy entropy / value loss"
}
