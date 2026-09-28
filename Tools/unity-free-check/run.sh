#!/usr/bin/env bash
# Compile the Unity-free parts of PoSoccer and run their tests with plain .NET.
# Usage: Tools/unity-free-check/run.sh      (installs a local .NET 8 SDK if none is found)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"

dotnet_bin="$(command -v dotnet || true)"
if [ -z "$dotnet_bin" ]; then
    dotnet_bin="$here/.dotnet/dotnet"
    if [ ! -x "$dotnet_bin" ]; then
        echo "No dotnet on PATH - installing .NET 8 SDK into $here/.dotnet"
        curl -sSL https://dot.net/v1/dotnet-install.sh -o "$here/.dotnet-install.sh"
        bash "$here/.dotnet-install.sh" --channel 8.0 --install-dir "$here/.dotnet" >/dev/null
    fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

python3 "$here/extract.py"
"$dotnet_bin" test "$here/UnityFreeCheck.csproj" --nologo -v quiet "$@"
