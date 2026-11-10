<#
Copy to: UnityClient/Tools/Release/publish-patch.ps1

.SYNOPSIS
  Publishes Builds/Patch/UOClient (made by UO Client > Release > Build Patch) as an incremental patch.

.DESCRIPTION
  1. Refuses anything that must not ship (same rules as publish-release.ps1).
  2. Runs `patchtool publish` into the local patch-host mirror. Only new files/chunks are added.
  3. Uploads the new files with deploy-patches.ps1 (blobs first, channel.json last), unless -NoDeploy.

.EXAMPLE
  ./Tools/Release/publish-patch.ps1 -Version 0.4.0
  ./Tools/Release/publish-patch.ps1 -Version 0.4.1-test.1 -Channel test -NoDeploy
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Channel = 'live',
    [string] $BuildDir = (Join-Path $PSScriptRoot '..\..\Builds\Patch\UOClient'),
    [string] $HostMirror = 'G:\Ultima Online\PatchHost',
    [string] $Destination = '\\phoenix-server\Patches',        # decided in Updater/docs/hosting.md
    [string] $UpdaterRepo = 'G:\Ultima Online\Updater',
    [string] $Key = "$env:USERPROFILE\.uo-unity-updater\keys\patch.pem",
    [switch] $NoDeploy
)
$ErrorActionPreference = 'Stop'
$BuildDir = (Resolve-Path $BuildDir).Path
if (-not (Test-Path (Join-Path $BuildDir 'UOClient.exe'))) { throw "No patch build at $BuildDir. Run UO Client > Release > Build Patch." }

$bad = Get-ChildItem $BuildDir -Recurse -Force | Where-Object {
    $_.Name -match 'DoNotShip|DontShip|ContentKey|\.pdb$|\.fbx$|\.prefab$|\.mat$|\.unitypackage$|\.psd$|\.tga$'
}
if ($bad) { throw "Refusing to publish; these must not ship:`n$($bad.FullName -join "`n")" }

# patchtool: use the released one if it's on PATH, otherwise run from the updater repo.
$pt = Get-Command patchtool -ErrorAction SilentlyContinue
function PT { if ($pt) { & $pt.Source @args } else { dotnet run --project "$UpdaterRepo\src\PatchTool" -c Release -- @args }; if ($LASTEXITCODE) { throw "patchtool $($args[0]) failed" } }

PT publish --build $BuildDir --host $HostMirror --channel $Channel --version $Version --product UOClient --launch UOClient.exe --key $Key

if (-not $NoDeploy) {
    & "$UpdaterRepo\tools\deploy-patches.ps1" -HostRoot $HostMirror -Destination $Destination
}
