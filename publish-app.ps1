# Makes the deployable package: publish\RoomAcoustics3D-<version>-win-x64, a folder to copy to any 64-bit Windows 10 or 11 machine, and a zip of it.
# It holds RoomAcoustics.exe (the tray launcher) beside RoomAcoustics.Api.exe (the engine), the built page in wwwroot (with the user guide and theory pages),
# the example models and a README. Run RoomAcoustics.exe: it starts the engine, opens http://localhost:5080 and sits in the notification area (Open, Quit).
#   -FrameworkDependent   leave the .NET runtime out (about 130 MB smaller); the machine then needs the .NET 10 runtime. The name ends -framework-dependent.
# The version is Directory.Build.props's. An existing package of the same name is not overwritten: remove it, or raise the version, first.
param([switch]$FrameworkDependent)
$root = $PSScriptRoot
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version
$name = "RoomAcoustics3D-$version-win-x64" + $(if ($FrameworkDependent) { '-framework-dependent' } else { '' })
$out = "$root\publish\$name"
$zip = "$out.zip"
if ((Test-Path $out) -or (Test-Path $zip)) { throw "$name is there already (publish\$name, or its zip): remove it, or raise the version in Directory.Build.props, first." }

$portable = Get-ChildItem "$env:USERPROFILE\tools" -Directory -Filter 'node-v*-win-x64' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
if ($portable -and -not (Get-Command node -ErrorAction SilentlyContinue)) { $env:Path = "$($portable.FullName);$env:Path" }
Push-Location "$root\web"
try {
    if (-not (Test-Path node_modules)) { npm install; if ($LASTEXITCODE) { throw 'npm install failed.' } }
    npm run build; if ($LASTEXITCODE) { throw 'The page did not build.' }
} finally { Pop-Location }

$mode = if ($FrameworkDependent) { @('-r', 'win-x64', '--self-contained', 'false') } else { @('-r', 'win-x64', '--self-contained', 'true') }
dotnet publish "$root\src\RoomAcoustics.Api" -c Release -o $out @mode; if ($LASTEXITCODE) { throw 'The engine did not publish.' }
dotnet publish "$root\src\RoomAcoustics.Tray" -c Release -o $out @mode; if ($LASTEXITCODE) { throw 'The tray launcher did not publish.' }
Copy-Item "$root\web\dist" "$out\wwwroot" -Recurse -ErrorAction Stop
Copy-Item "$root\examples" "$out\examples" -Recurse -ErrorAction Stop
Copy-Item "$root\LICENSE.md" "$out\LICENSE.md" -ErrorAction Stop

$runtime = if ($FrameworkDependent) { 'This package needs the .NET 10 runtime (the ASP.NET Core and Windows Desktop runtimes), from https://dotnet.microsoft.com/download.' }
           else { 'Nothing else: the .NET runtime is included.' }
(Get-Content "$root\packaging\README.txt" -Raw).Replace('{VERSION}', $version).Replace('{RUNTIME}', $runtime) | Set-Content "$out\README.txt" -Encoding utf8 -ErrorAction Stop

foreach ($f in 'RoomAcoustics.exe', 'RoomAcoustics.Api.exe', 'wwwroot\index.html', 'wwwroot\guide.html', 'wwwroot\theory.html', 'README.txt') {
    if (-not (Test-Path "$out\$f")) { throw "The package is missing $f." }
}
Compress-Archive -Path "$out\*" -DestinationPath $zip -ErrorAction Stop
Write-Host "Done: $out\RoomAcoustics.exe and $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
