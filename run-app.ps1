# Builds the web app (if it is not built yet) and runs the whole application as one program on http://localhost:5080, then opens the browser once the engine answers
# (-NoBrowser leaves the browser alone; -Rebuild rebuilds the page).
# Needs the .NET 10 SDK, and Node.js 20+ only the first time (to build web/dist).
param([switch]$Rebuild, [switch]$NoBrowser)
$root = $PSScriptRoot
$portable = Get-ChildItem "$env:USERPROFILE\tools" -Directory -Filter 'node-v*-win-x64' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
if ($portable -and -not (Get-Command node -ErrorAction SilentlyContinue)) { $env:Path = "$($portable.FullName);$env:Path" }

if ($Rebuild -or -not (Test-Path "$root\web\dist\index.html")) {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Write-Error 'Node.js is needed once, to build the web app. Install it from https://nodejs.org (LTS).'; exit 1 }
    Push-Location "$root\web"
    if (-not (Test-Path node_modules)) { npm install }
    npm run build
    Pop-Location
}

if (-not $NoBrowser) {
    # open the page only when the engine is answering, so the first look is never a "can't connect" error
    Start-Job { for ($i = 0; $i -lt 180; $i++) { try { Invoke-WebRequest 'http://localhost:5080/api/health' -UseBasicParsing -TimeoutSec 2 | Out-Null; Start-Process 'http://localhost:5080'; break } catch { Start-Sleep -Seconds 1 } } } | Out-Null
}
dotnet run -c Release --project "$root\src\RoomAcoustics.Api"
