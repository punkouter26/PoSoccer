# Captures TensorBoard chart cards as PNG by driving headless Edge over its debugging
# port. TensorBoard draws its lines on a WebGL canvas after async fetches, so the plain
# `--screenshot` flag returns empty cards; this waits in real time and then captures.
#   .\scripts\capture-tensorboard.ps1 -OutDir <dir> -RunFilter 'p32pass|p33team' `
#       -Tags @{ passes='PoSoccer/blue_passes$' }
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][hashtable]$Tags,
    [string]$RunFilter = '.*',
    [int]$Port = 6006,
    [double]$Smoothing = 0.9,
    [int]$WaitSeconds = 12
)
$ErrorActionPreference = 'Stop'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
          "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
          "$env:ProgramFiles\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'No Edge or Chrome found for headless capture.' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$profileDir = Join-Path $OutDir '_browser-profile'
$proc = Start-Process $edge -ArgumentList @('--headless=new', '--remote-debugging-port=9333', '--hide-scrollbars',
    '--window-size=1500,820', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist',
    "--user-data-dir=$profileDir", 'about:blank') -PassThru
try {
    Start-Sleep 4
    $target = (Invoke-RestMethod http://localhost:9333/json) | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $ws.ConnectAsync([uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    $script:cdpId = 0
    function Invoke-Cdp($method, $params) {
        $script:cdpId++
        $msg = @{ id = $script:cdpId; method = $method; params = $params } | ConvertTo-Json -Depth 8 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
        $ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).Wait()
        while ($true) {
            $ms = [IO.MemoryStream]::new(); $buf = [byte[]]::new(1MB)
            do {
                $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None).Result
                $ms.Write($buf, 0, $r.Count)
            } while (-not $r.EndOfMessage)
            $o = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
            if ($o.id -eq $script:cdpId) { return $o }
        }
    }
    $null = Invoke-Cdp 'Emulation.setDeviceMetricsOverride' @{ width = 1500; height = 820; deviceScaleFactor = 2; mobile = $false }
    foreach ($key in $Tags.Keys) {
        $q = "darkMode=false&smoothing=$Smoothing&runFilter=" + [uri]::EscapeDataString($RunFilter) +
             '&tagFilter=' + [uri]::EscapeDataString($Tags[$key])
        $null = Invoke-Cdp 'Page.navigate' @{ url = "http://localhost:$Port/?$q#timeseries" }
        Start-Sleep $WaitSeconds
        $shot = Invoke-Cdp 'Page.captureScreenshot' @{ format = 'png' }
        $path = Join-Path $OutDir "tb_$key.png"
        [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($shot.result.data))
        Write-Host "$key -> $path"
    }
    $ws.Dispose()
}
finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'msedge|chrome' -and $_.CommandLine -match [regex]::Escape($profileDir) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
