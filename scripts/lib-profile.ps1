# Shared helper: stamp a scalar field onto a Reward_Settings .asset (YAML text).
#
# Unity serializes ScriptableObjects as flat YAML, and a field that is absent from
# the file simply keeps its C# initializer value. So writing a field means either
# replacing its existing line or inserting a new one - both at the 2-space
# indentation the MonoBehaviour block uses.
#
# Only scalar fields are supported (int / float / string). Values are written raw,
# so anything needing YAML quoting must arrive already quoted.

function Set-ProfileField {
    param(
        [Parameter(Mandatory = $true)][string]$AssetPath,
        [Parameter(Mandatory = $true)][string]$Field,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value
    )

    if (-not (Test-Path $AssetPath)) {
        Write-Warning "Set-ProfileField: no asset at $AssetPath"
        return
    }

    $lines = [System.Collections.Generic.List[string]](Get-Content $AssetPath)
    $pattern = "^  $([regex]::Escape($Field)):"
    $index = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $pattern) { $index = $i; break }
    }

    if ($index -ge 0) {
        $lines[$index] = "  ${Field}: $Value"
    }
    else {
        # Anchor on playerName - it is the first personality field and always
        # present, so the insert lands inside the MonoBehaviour body.
        $anchor = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^  playerName:') { $anchor = $i; break }
        }
        if ($anchor -lt 0) {
            Write-Warning "Set-ProfileField: $AssetPath has no playerName anchor; skipped $Field"
            return
        }
        $lines.Insert($anchor + 1, "  ${Field}: $Value")
    }

    Set-Content -Path $AssetPath -Value $lines -Encoding UTF8
}
