<#
.SYNOPSIS
  Register (or update) the Windows Task Scheduler entry that runs
  scripts/train-overnight.ps1 every night at 00:05.

.DESCRIPTION
  Run once from an ordinary PowerShell. Elevation is NOT required: nothing in
  the overnight pipeline needs admin rights (it kills only your own Unity
  process, runs the trainer as you, and writes inside the project). If you do
  happen to run it elevated it registers at RunLevel Highest instead, which is
  belt-and-braces rather than a requirement.
  Re-running it overwrites the existing task, so it doubles as the edit path.

  Deliberate choices:
    * Logon type Interactive, "run only when user is logged on". The headless
      player still needs a real desktop session for GPU access - a Session 0
      task gets software rendering and the run crawls.
    * WakeToRun + battery settings on, so a sleeping machine still trains.
    * ExecutionTimeLimit 8h30m as a backstop; train-overnight.ps1 enforces its
      own 08:00 stop well before that.
    * The task does NOT start if a previous instance is still running.

.EXAMPLE
  .\scripts\register-overnight-task.ps1
  .\scripts\register-overnight-task.ps1 -At 23:30 -Unregister:$false
  .\scripts\register-overnight-task.ps1 -Unregister
#>
param(
    [string]$TaskName = "PoSoccer Overnight Training",
    [string]$At = "00:05",
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$script = Join-Path $PSScriptRoot "train-overnight.ps1"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($Unregister) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Removed scheduled task '$TaskName'."
    return
}

if (-not (Test-Path $script)) { throw "Missing $script" }

$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`"" `
    -WorkingDirectory $root

$trigger = New-ScheduledTaskTrigger -Daily -At $At

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -WakeToRun -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 8 -Minutes 30) `
    -RestartCount 0

# Interactive = the logged-on desktop session, which is what gives the headless
# player a real GPU. RunLevel Highest needs an elevated shell to register, and
# the pipeline does not actually require it, so fall back rather than refuse.
$runLevel = if ($isAdmin) { "Highest" } else { "Limited" }
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive -RunLevel $runLevel

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $principal -Force `
    -Description "Runs the PoSoccer ML-Agents overnight experiment queue; hard stop at 08:00." | Out-Null

Write-Host "Registered '$TaskName' - fires daily at $At (RunLevel $runLevel)."
if (-not $isAdmin) {
    Write-Host "Registered without elevation. That is fine here - the overnight run needs no admin rights."
}
Write-Host ""
Write-Host "Verify:      Get-ScheduledTask -TaskName '$TaskName' | Get-ScheduledTaskInfo"
Write-Host "Dry run now: Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "Disable:     Disable-ScheduledTask -TaskName '$TaskName'"
Write-Host ""
Write-Host "NOTE: Windows must not be set to sleep in a way that blocks WakeToRun."
Write-Host "      Check with: powercfg /waketimers"
