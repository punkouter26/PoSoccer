# Toolchain bootstrap (UNITY_RULES 5): build a .venv whose Python mlagents is
# pinned to the SAME upstream commit as the embedded C# package, so the two
# never drift. Idempotent - safe to re-run.
#
#   .\scripts\setup-training-env.ps1
#   .\scripts\setup-training-env.ps1 -Force      # rebuild the venv from scratch
param(
    [switch]$Force,
    [string]$ClonePath
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$req = Join-Path $root "requirements-training.txt"

# Read the pinned anchors out of requirements-training.txt so there is exactly
# one place to bump them.
function Get-Anchor([string]$key) {
    $line = Select-String -Path $req -Pattern "^#\s*$key=(.+)$" | Select-Object -First 1
    if (-not $line) { throw "Anchor '$key' missing from requirements-training.txt" }
    return $line.Matches[0].Groups[1].Value.Trim()
}

$repoUrl      = Get-Anchor "ML_AGENTS_REPO"
$commit       = Get-Anchor "ML_AGENTS_COMMIT"
$expectedPy   = Get-Anchor "ML_AGENTS_PY_VERSION"
$expectedCs   = Get-Anchor "CSHARP_PACKAGE_VERSION"
$pythonMax    = Get-Anchor "PYTHON_MAX"

# 1. Verify the C# side still matches what this file claims to pin against.
$pkgJson = Join-Path $root "Packages\com.unity.ml-agents\package.json"
$csVersion = (Get-Content $pkgJson -Raw | ConvertFrom-Json).version
if ($csVersion -ne $expectedCs) {
    throw "PARITY BREAK: embedded C# package is $csVersion but requirements-training.txt pins $expectedCs. Update both together."
}
Write-Host "C# package com.unity.ml-agents $csVersion matches the pin."

# 2. Interpreter check - ml-agents does not support Python > $pythonMax.
$pyVersion = (& python -c "import sys;print('.'.join(map(str,sys.version_info[:3])))").Trim()
if ([version]$pyVersion -gt [version]$pythonMax) {
    throw "Python $pyVersion is newer than the supported maximum $pythonMax. Install 3.10.x and re-run."
}
Write-Host "Python $pyVersion is within the supported range (<= $pythonMax)."

# 3. Clone (or fetch) ml-agents at the pinned commit.
if (-not $ClonePath) { $ClonePath = Join-Path $root ".tooling\ml-agents" }
if (-not (Test-Path $ClonePath)) {
    New-Item -ItemType Directory -Force (Split-Path $ClonePath -Parent) | Out-Null
    Write-Host "Cloning ml-agents into $ClonePath ..."
    git clone --filter=blob:none $repoUrl $ClonePath
}
git -C $ClonePath fetch --all --quiet
git -C $ClonePath checkout --quiet $commit
$head = (git -C $ClonePath rev-parse --short HEAD).Trim()
Write-Host "ml-agents clone pinned at $head."

# 4. Create the venv.
$venv = Join-Path $root ".venv"
if ($Force -and (Test-Path $venv)) {
    Write-Host "Removing existing .venv (-Force) ..."
    Remove-Item $venv -Recurse -Force
}
if (-not (Test-Path $venv)) { & python -m venv $venv }
$py = Join-Path $venv "Scripts\python.exe"

& $py -m pip install --upgrade pip --quiet

# 5. Editable installs, envs first (ml-agents depends on it).
Write-Host "Installing mlagents-envs and mlagents (editable, from the pinned commit) ..."
& $py -m pip install -e (Join-Path $ClonePath "ml-agents-envs") --quiet
& $py -m pip install -e (Join-Path $ClonePath "ml-agents") --quiet

# 6. Pinned extras. Protobuf last: the editable installs can drag in a version
#    that mismatches the embedded Google.Protobuf.dll 3.21.12 (see CLAUDE.md landmines).
& $py -m pip install -r $req --quiet

# 7. Verify parity end to end.
$actualPy = (& $py -c "import mlagents;print(mlagents.__version__)").Trim()
if ($actualPy -ne $expectedPy) {
    throw "PARITY BREAK: installed mlagents is $actualPy, expected $expectedPy from commit $commit."
}
Write-Host ""
Write-Host "OK - parity verified:"
Write-Host "  C# com.unity.ml-agents : $csVersion (embedded)"
Write-Host "  Python mlagents        : $actualPy (editable @ $head)"
Write-Host "  Interpreter            : $pyVersion"
Write-Host ""
Write-Host "Next: .\scripts\train-phase1.ps1 -RunId <run> -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4"
