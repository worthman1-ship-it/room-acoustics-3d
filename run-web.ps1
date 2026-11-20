# Starts the C# engine (http://localhost:5080) and the web front end (http://localhost:5173), and opens the browser.
# Needs the .NET SDK and Node.js 20+ (https://nodejs.org). Close both windows to stop.
$root = $PSScriptRoot
# a portable Node unpacked under ~\tools (no admin rights needed) is used when none is on the PATH
$portable = Get-ChildItem "$env:USERPROFILE\tools" -Directory -Filter 'node-v*-win-x64' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
if ($portable -and -not (Get-Command node -ErrorAction SilentlyContinue)) { $env:Path = "$($portable.FullName);$env:Path" }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Write-Error 'Node.js is not installed. Install it from https://nodejs.org (LTS) and run this again.'; exit 1 }

if (-not (Test-Path "$root\web\node_modules")) {
    Write-Host 'First run: installing the web packages...'
    Push-Location "$root\web"; npm install; Pop-Location
}

Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$root'; dotnet run --project src/RoomAcoustics.Api" -WindowStyle Normal
Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$root\web'; npm run dev" -WindowStyle Normal
Start-Sleep -Seconds 6
Start-Process 'http://localhost:5173'
