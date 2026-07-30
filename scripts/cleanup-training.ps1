# Lifecycle guardrail (UNITY_RULES): terminate orphaned training processes/sessions.
# Kills stray mlagents-learn trainers and headless env players, never the interactive editor.
$ErrorActionPreference = "SilentlyContinue"

Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -match "mlagents-learn|mlagents\.trainers" } |
    ForEach-Object {
        Write-Host "Killing orphaned trainer PID $($_.ProcessId)"
        Stop-Process -Id $_.ProcessId -Force
    }

# Headless env players are launched with -batchmode by mlagents-learn.
Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq "PoSoccer.exe" -and $_.CommandLine -match "-batchmode|--mlagents" } |
    ForEach-Object {
        Write-Host "Killing orphaned env player PID $($_.ProcessId)"
        Stop-Process -Id $_.ProcessId -Force
    }
