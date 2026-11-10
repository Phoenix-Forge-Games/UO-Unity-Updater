<#
End-to-end test of publishing and patching, run locally and in CI.

 1. Publishes two synthetic game builds (v1, v2) to a local patch host and serves it over HTTP.
 2. Syncs a fresh install to v1, then patches it to v2 and checks that only changed data downloaded:
    a small edited file, a 100 KB insertion inside a 24 MB file (chunk delta), a new bundle, a rename
    (reused from disk, no download), a deleted file, and that player files outside the manifest survive.
 3. Corrupts a blob and tampers with channel.json: the client must refuse and leave the install intact.
 4. (unless -SkipSelfUpdate) Publishes GameUpdater 1.0.0 and 1.0.1, runs 1.0.0, and checks it
    replaces itself with 1.0.1 and then patches the game.

Usage: pwsh tests/e2e.ps1 [-SkipSelfUpdate] [-Configuration Release]
#>
param(
    [switch]$SkipSelfUpdate,
    [string]$Configuration = "Release",
    [int]$Port = 18080,
    # Local runs: pin to a runtime pack already in the NuGet cache (e.g. 10.0.9) and restore offline.
    [string]$RuntimeFrameworkVersion = ""
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $PSScriptRoot ".work"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null

function Fail($msg) { throw "E2E FAILED: $msg" }
function Step($msg) { Write-Host "`n=== $msg" -ForegroundColor Cyan }

Step "Build patchtool"
dotnet build "$repo/src/PatchTool/PatchTool.csproj" -c $Configuration -o "$work/tool" --nologo -v q | Out-Host
if ($LASTEXITCODE) { Fail "build failed" }
$tool = Join-Path $work "tool/patchtool.exe"
if (-not (Test-Path $tool)) { $tool = Join-Path $work "tool/patchtool" }

function PT { & $tool @args; if ($LASTEXITCODE) { Fail "patchtool $($args[0]) exited $LASTEXITCODE" } }
function PTExit { & $tool @args | Out-Host; return $LASTEXITCODE }

function Write-Random($path, [int]$size, [int]$seed) {
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory $dir -Force | Out-Null }
    $bytes = New-Object byte[] $size
    (New-Object System.Random $seed).NextBytes($bytes)
    [IO.File]::WriteAllBytes($path, $bytes)
}

function Get-Tree($dir) {
    Get-ChildItem $dir -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/]\.patch[\\/]' } | ForEach-Object {
        $rel = $_.FullName.Substring((Resolve-Path $dir).Path.Length + 1).Replace('\', '/')
        "$rel|$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)"
    } | Sort-Object
}

function Assert-SameTree($a, $b, $label) {
    $ta = Get-Tree $a; $tb = Get-Tree $b
    $diff = Compare-Object $ta $tb
    if ($diff) { $diff | Out-Host; Fail "$label differs from the build" }
    Write-Host "OK: $label matches the build ($($ta.Count) files)" -ForegroundColor Green
}

function Sync-Result($output) {
    $line = $output | Where-Object { $_ -like "RESULT *" } | Select-Object -Last 1
    if (-not $line) { Fail "no RESULT line from sync" }
    return ($line.Substring(7) | ConvertFrom-Json)
}

Step "Keys"
PT keygen --out "$work/keys" --name patch | Out-Host
PT keygen --out "$work/keys" --name updater | Out-Host
$patchKey = "$work/keys/patch.pem"; $patchPub = "$work/keys/patch.pub"

Step "Synthetic builds"
$v1 = "$work/build-v1"
Write-Random "$v1/Game.exe" 200000 1
Write-Random "$v1/Game_Data/globalgamemanagers" 300000 2
Write-Random "$v1/Game_Data/Managed/Assembly-CSharp.dll" 500000 3
Write-Random "$v1/Game_Data/resources.assets" (24MB) 4        # large: chunked
Write-Random "$v1/Game_Data/StreamingAssets/aa/catalog.bin" 40000 5
Write-Random "$v1/Game_Data/StreamingAssets/aa/StandaloneWindows64/world_chunk_0_0.bundle" 900000 6
Write-Random "$v1/Game_Data/StreamingAssets/aa/StandaloneWindows64/world_chunk_0_1.bundle" 900000 7
Write-Random "$v1/Game_Data/StreamingAssets/aa/StandaloneWindows64/old_unused.bundle" 50000 8
Write-Random "$v1/Game_Data/StreamingAssets/aa/StandaloneWindows64/rename_me.bundle" 700000 9
Set-Content "$v1/Game_Data/text.json" ('{"motd":"' + ('hello ' * 5000) + '"}') -NoNewline   # compressible
Write-Random "$v1/Game_BurstDebugInformation_DoNotShip/junk.pdb" 1000 10                # must be excluded

$v2 = "$work/build-v2"
Copy-Item $v1 $v2 -Recurse
Write-Random "$v2/Game_Data/Managed/Assembly-CSharp.dll" 500000 33                       # changed small file
$big = [IO.File]::ReadAllBytes("$v1/Game_Data/resources.assets")                        # insert 100 KB in the middle
$ins = New-Object byte[] 102400; (New-Object System.Random 44).NextBytes($ins)
$mid = 12MB
$nb = New-Object byte[] ($big.Length + $ins.Length)
[Array]::Copy($big, 0, $nb, 0, $mid); [Array]::Copy($ins, 0, $nb, $mid, $ins.Length)
[Array]::Copy($big, $mid, $nb, $mid + $ins.Length, $big.Length - $mid)
[IO.File]::WriteAllBytes("$v2/Game_Data/resources.assets", $nb)
Write-Random "$v2/Game_Data/StreamingAssets/aa/catalog.bin" 41000 55                     # new catalog
Write-Random "$v2/Game_Data/StreamingAssets/aa/StandaloneWindows64/world_chunk_0_2.bundle" 800000 66  # new bundle
Remove-Item "$v2/Game_Data/StreamingAssets/aa/StandaloneWindows64/old_unused.bundle"    # deleted
Move-Item "$v2/Game_Data/StreamingAssets/aa/StandaloneWindows64/rename_me.bundle" "$v2/Game_Data/StreamingAssets/aa/StandaloneWindows64/renamed_7f3a.bundle"
Remove-Item "$v2/Game_BurstDebugInformation_DoNotShip" -Recurse

Step "Publish v1"
$hostDir = "$work/host"
$v1ship = "$work/build-v1-shipped"; Copy-Item $v1 $v1ship -Recurse; Remove-Item "$v1ship/Game_BurstDebugInformation_DoNotShip" -Recurse
PT publish --build $v1 --host $hostDir --channel live --version 1.0.0 --product TestGame --key $patchKey --launch Game.exe | Out-Host

$server = Start-Process $tool -ArgumentList "serve","--root","`"$hostDir`"","--port",$Port -PassThru -WindowStyle Hidden
try {
    $url = "http://127.0.0.1:$Port/live/channel.json"
    for ($i = 0; $i -lt 50; $i++) { try { Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep -Milliseconds 200 } }

    Step "Fresh install of 1.0.0"
    $install = "$work/install"
    $out = & $tool sync --channel-url $url --dir $install --pubkey $patchPub --json; $out | Out-Host
    if ($LASTEXITCODE) { Fail "fresh sync failed" }
    Assert-SameTree $install $v1ship "fresh install (1.0.0)"
    Write-Random "$install/Game_Data/player_settings.ini" 1234 99   # player files, not in any manifest
    Write-Random "$install/Saves/slot1.sav" 4321 98

    Step "No-op sync"
    $out = & $tool sync --channel-url $url --dir $install --pubkey $patchPub --json; $out | Out-Host
    if ((Sync-Result $out).bytesDownloaded -ne 0) { Fail "no-op sync downloaded data" }

    Step "Publish 1.1.0"
    $before = (Get-ChildItem "$hostDir/blobs" -Recurse -File | Measure-Object Length -Sum).Sum
    PT publish --build $v2 --host $hostDir --channel live --version 1.1.0 --product TestGame --key $patchKey --launch Game.exe | Out-Host
    $added = (Get-ChildItem "$hostDir/blobs" -Recurse -File | Measure-Object Length -Sum).Sum - $before
    Write-Host ("1.1.0 added {0:N2} MB of blobs to the host" -f ($added/1MB))
    if ($added -gt 6MB) { Fail "publishing 1.1.0 stored $added bytes; expected only the changed data" }
    if (Get-ChildItem $hostDir -Recurse -Filter "*.tmp") { Fail "temp files left on host" }

    Step "Upgrade 1.0.0 -> 1.1.0"
    $out = & $tool sync --channel-url $url --dir $install --pubkey $patchPub --json; $out | Out-Host
    if ($LASTEXITCODE) { Fail "upgrade sync failed" }
    $r2 = Sync-Result $out
    if (-not ((Test-Path "$install/Game_Data/player_settings.ini") -and (Test-Path "$install/Saves/slot1.sav"))) { Fail "player files outside the manifest were touched" }
    Remove-Item "$install/Game_Data/player_settings.ini"; Remove-Item "$install/Saves" -Recurse
    Assert-SameTree $install $v2 "upgraded install (1.1.0)"
    $full = (Get-ChildItem $v2 -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ("Upgrade downloaded {0:N2} MB of a {1:N2} MB game; reused {2:N2} MB already on disk" -f ($r2.bytesDownloaded/1MB), ($full/1MB), ($r2.bytesReused/1MB))
    # Changed: dll 0.5 MB + catalog 0.04 + new bundle 0.8 + the chunk(s) around the insertion. The rename must not download.
    if ($r2.bytesDownloaded -gt 6MB) { Fail "upgrade downloaded $($r2.bytesDownloaded) bytes; expected only the changes" }
    if (Test-Path "$install/Game_Data/StreamingAssets/aa/StandaloneWindows64/old_unused.bundle") { Fail "deleted file still present" }

    Step "Corrupt blob is rejected and the install is left intact"
    $v3 = "$work/build-v3"; Copy-Item $v2 $v3 -Recurse
    Write-Random "$v3/Game_Data/Managed/Assembly-CSharp.dll" 500000 777
    PT publish --build $v3 --host $hostDir --channel live --version 1.2.0 --product TestGame --key $patchKey --launch Game.exe | Out-Null
    $sha = (Get-FileHash "$v3/Game_Data/Managed/Assembly-CSharp.dll" -Algorithm SHA256).Hash.ToLower()
    $blob = Get-ChildItem "$hostDir/blobs/$($sha.Substring(0,2))" -Filter "$sha*" | Select-Object -First 1
    $bytes = [IO.File]::ReadAllBytes($blob.FullName); $bytes[100] = $bytes[100] -bxor 0xFF; [IO.File]::WriteAllBytes($blob.FullName, $bytes)
    $code = PTExit sync --channel-url $url --dir $install --pubkey $patchPub
    if ($code -eq 0) { Fail "sync accepted a corrupt blob" }
    Assert-SameTree $install $v2 "install after rejected corrupt patch"

    Step "Tampered channel.json is rejected"
    $cj = Get-Content "$hostDir/live/channel.json" -Raw
    Set-Content "$hostDir/live/channel.json" ($cj -replace '"version": "1.2.0"', '"version": "9.9.9"') -NoNewline
    $code = PTExit sync --channel-url $url --dir $install --pubkey $patchPub
    if ($code -eq 0) { Fail "sync accepted a tampered channel.json" }
    Assert-SameTree $install $v2 "install after rejected tampered channel"

    Step "Repair: fix host, damage a local file, --verify restores it"
    $bytes[100] = $bytes[100] -bxor 0xFF; [IO.File]::WriteAllBytes($blob.FullName, $bytes)
    PT publish --build $v3 --host $hostDir --channel live --version 1.2.0 --product TestGame --key $patchKey --launch Game.exe --force | Out-Null
    $out = & $tool sync --channel-url $url --dir $install --pubkey $patchPub --json; $out | Out-Host
    if ($LASTEXITCODE) { Fail "sync to 1.2.0 failed" }
    # Damage a file without changing its size or timestamp: a normal sync trusts the stamp, --verify must catch it.
    $stamp = (Get-Item "$install/Game_Data/globalgamemanagers").LastWriteTimeUtc
    Write-Random "$install/Game_Data/globalgamemanagers" 300000 1234
    (Get-Item "$install/Game_Data/globalgamemanagers").LastWriteTimeUtc = $stamp
    $out = & $tool sync --channel-url $url --dir $install --pubkey $patchPub --verify --json; $out | Out-Host
    if ($LASTEXITCODE) { Fail "verify sync failed" }
    Assert-SameTree $install $v3 "repaired install (1.2.0)"

    Step "Rollback to 1.1.0 and forward again"
    PT rollback --host $hostDir --channel live --version 1.1.0 | Out-Host
    $out = & $tool sync --channel-url $url --dir $install --pubkey $patchPub --json; $out | Out-Host
    if ($LASTEXITCODE) { Fail "rollback sync failed" }
    Assert-SameTree $install $v2 "rolled-back install (1.1.0)"
    PT rollback --host $hostDir --channel live --version 1.2.0 | Out-Host
    $out = & $tool sync --channel-url $url --dir $install --pubkey $patchPub --json; $out | Out-Host
    if ($LASTEXITCODE) { Fail "roll-forward sync failed" }
    Assert-SameTree $install $v3 "rolled-forward install (1.2.0)"
    if (-not $SkipSelfUpdate) {
        Step "Self-update: publish GameUpdater 1.0.0 and 1.0.1"
        foreach ($ver in "1.0.0", "1.0.1") {
            $extra = @()
            if ($RuntimeFrameworkVersion) { $empty = Join-Path $work "empty-src"; New-Item -ItemType Directory $empty -Force | Out-Null; $extra = @("-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion", "--source", $empty) }
            dotnet publish "$repo/src/GameUpdater/GameUpdater.csproj" -c $Configuration -p:Version=$ver -o "$work/gu-$ver" --nologo -v q @extra | Out-Host
            if ($LASTEXITCODE) { Fail "GameUpdater $ver publish failed" }
        }
        $feedDir = "$hostDir/updater"; New-Item -ItemType Directory $feedDir -Force | Out-Null
        Copy-Item "$work/gu-1.0.1/GameUpdater.exe" "$feedDir/GameUpdater.exe"
        PT updater-release --exe "$feedDir/GameUpdater.exe" --version 1.0.1 --url GameUpdater.exe --key "$work/keys/updater.pem" --out $feedDir | Out-Host

        $client = "$work/client"; New-Item -ItemType Directory $client | Out-Null
        Copy-Item "$work/gu-1.0.0/GameUpdater.exe" $client
        @{
            channelUrl     = $url
            updaterFeedUrl = "http://127.0.0.1:$Port/updater/updater.json"
            installDir     = "Game"
            extraPatchKeys = @([IO.File]::ReadAllText($patchPub))
            extraUpdaterKeys = @([IO.File]::ReadAllText("$work/keys/updater.pub"))
        } | ConvertTo-Json | Set-Content "$client/GameUpdater.json" -Encoding UTF8
        $log = "$client/run.log"
        $p = Start-Process "$client/GameUpdater.exe" -ArgumentList "--headless","--log","`"$log`"" -PassThru -WindowStyle Hidden
        $p.WaitForExit(120000) | Out-Null
        # The old process exits after starting the new one; wait for the new one to finish the game update.
        $deadline = (Get-Date).AddMinutes(3)
        while ((Get-Date) -lt $deadline) {
            if ((Test-Path $log) -and (Select-String -Path $log -Pattern "Ready to play|Update failed|ERROR" -Quiet)) { break }
            Start-Sleep -Milliseconds 500
        }
        Get-Content $log | Out-Host
        Start-Sleep -Seconds 2
        $ver = (Get-Item "$client/GameUpdater.exe").VersionInfo.ProductVersion
        if ($ver -notlike "1.0.1*") { Fail "updater did not replace itself (version $ver)" }
        if (Test-Path "$client/GameUpdater.update.exe") { Fail "staged updater executable was not cleaned up" }
        if (-not (Select-String -Path $log -Pattern "GameUpdater 1.0.1" -Quiet)) { Fail "new updater did not run after self-update" }
        if (-not (Select-String -Path $log -Pattern "Ready to play" -Quiet)) { Fail "new updater did not install the game" }
        Assert-SameTree "$client/Game" $v3 "game installed by the self-updated GameUpdater"
        Write-Host "OK: GameUpdater 1.0.0 updated itself to $ver and installed the game" -ForegroundColor Green
    }

    Write-Host "`nALL E2E CHECKS PASSED" -ForegroundColor Green
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process $server -Force }
}
