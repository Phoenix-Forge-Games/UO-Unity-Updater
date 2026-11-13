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

    Step "Launcher skin pack (generated data, no art)"
    $skinDir = "$work/skin"; New-Item -ItemType Directory "$skinDir/img" -Force | Out-Null
    '{ "name": "e2e", "images": { "panel": { "file": "img/panel.png", "slice": [4, 4, 4, 4] } } }' | Set-Content "$skinDir/skin.json" -Encoding UTF8
    $png = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) + [byte[]](1..2000 | ForEach-Object { $_ % 251 })
    [IO.File]::WriteAllBytes("$skinDir/img/panel.png", $png)
    PT pack-skin --dir $skinDir --out "$work/skin-a.pak" | Out-Host
    PT pack-skin --dir $skinDir --out "$work/skin-b.pak" | Out-Host
    if ((Get-FileHash "$work/skin-a.pak").Hash -ne (Get-FileHash "$work/skin-b.pak").Hash) { Fail "packing the same skin twice gave different bytes" }
    $packed = [IO.File]::ReadAllBytes("$work/skin-a.pak")
    $text = [Text.Encoding]::ASCII.GetString($packed)
    if ($text.Contains("PNG") -or $text.Contains("skin.json") -or $text.Contains("panel")) { Fail "skin pack contents are readable without decrypting" }
    New-Item -ItemType Directory "$work/skin-empty" -Force | Out-Null
    if ((PTExit pack-skin --dir "$work/skin-empty" --out "$work/skin-c.pak") -eq 0) { Fail "pack-skin accepted a folder without skin.json" }
    Write-Host "OK: skin pack is encrypted, reproducible and readable by the launcher's reader" -ForegroundColor Green

    if (-not $SkipSelfUpdate) {
        Step "Self-update: publish GameUpdater 1.0.0 and 1.0.1"
        foreach ($ver in "1.0.0", "1.0.1") {
            $extra = @()
            if ($RuntimeFrameworkVersion) { $empty = Join-Path $work "empty-src"; New-Item -ItemType Directory $empty -Force | Out-Null; $extra = @("-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion", "--source", $empty) }
            dotnet publish "$repo/src/GameUpdater/GameUpdater.csproj" -c $Configuration -p:Version=$ver -o "$work/gu-$ver" --nologo -v q @extra | Out-Host
            if ($LASTEXITCODE) { Fail "GameUpdater $ver publish failed" }
        }
        $feedDir = "$hostDir/updater"; New-Item -ItemType Directory $feedDir -Force | Out-Null
        Copy-Item "$work/gu-1.0.1/BritanniaReborn.exe" "$feedDir/BritanniaReborn.exe"
        PT updater-release --exe "$feedDir/BritanniaReborn.exe" --version 1.0.1 --url BritanniaReborn.exe --key "$work/keys/updater.pem" --out $feedDir | Out-Host

        $client = "$work/client"; New-Item -ItemType Directory $client | Out-Null
        Copy-Item "$work/gu-1.0.0/BritanniaReborn.exe" $client
        @{
            channelUrl     = $url
            updaterFeedUrl = "http://127.0.0.1:$Port/updater/updater.json"
            installDir     = "Game"
            extraPatchKeys = @([IO.File]::ReadAllText($patchPub))
            extraUpdaterKeys = @([IO.File]::ReadAllText("$work/keys/updater.pub"))
        } | ConvertTo-Json | Set-Content "$client/BritanniaReborn.json" -Encoding UTF8
        $log = "$client/run.log"
        $p = Start-Process "$client/BritanniaReborn.exe" -ArgumentList "--headless","--log","`"$log`"" -PassThru -WindowStyle Hidden
        $p.WaitForExit(120000) | Out-Null
        # The old process exits after starting the new one; wait for the new one to finish the game update.
        $deadline = (Get-Date).AddMinutes(3)
        while ((Get-Date) -lt $deadline) {
            if ((Test-Path $log) -and (Select-String -Path $log -Pattern "Ready to play|Update failed|ERROR" -Quiet)) { break }
            Start-Sleep -Milliseconds 500
        }
        Get-Content $log | Out-Host
        Start-Sleep -Seconds 2
        $ver = (Get-Item "$client/BritanniaReborn.exe").VersionInfo.ProductVersion
        if ($ver -notlike "1.0.1*") { Fail "updater did not replace itself (version $ver)" }
        if (Test-Path "$client/BritanniaReborn.update.exe") { Fail "staged updater executable was not cleaned up" }
        if (-not (Select-String -Path $log -Pattern "Britannia Reborn launcher 1.0.1" -Quiet)) { Fail "new updater did not run after self-update" }
        if (-not (Select-String -Path $log -Pattern "Ready to play" -Quiet)) { Fail "new updater did not install the game" }
        Assert-SameTree "$client/Game" $v3 "game installed by the self-updated GameUpdater"
        Write-Host "OK: GameUpdater 1.0.0 updated itself to $ver and installed the game" -ForegroundColor Green

        Step "Launcher window opens without a skin, and with a bad one (UI self-test)"
        # v0.4.0 crashed with a WinForms error dialog on a fresh install (no Game folder, so no skin).
        $ui = "$work/ui"; New-Item -ItemType Directory $ui | Out-Null
        Copy-Item "$work/gu-1.0.1/BritanniaReborn.exe" $ui
        '{ "selfUpdate": false }' | Set-Content "$ui/BritanniaReborn.json" -Encoding UTF8
        [IO.File]::WriteAllBytes("$ui/garbage.pak", [byte[]](1..500 | ForEach-Object { $_ % 256 }))
        Add-Type -AssemblyName System.Drawing
        $okSkin = "$work/skin-ok"; New-Item -ItemType Directory $okSkin | Out-Null
        $bmp = New-Object Drawing.Bitmap 24, 24; $bmp.Save("$okSkin/panel.png", [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        '{ "name": "e2e ok", "images": { "panel": { "file": "panel.png", "slice": [4, 4, 4, 4] } } }' | Set-Content "$okSkin/skin.json" -Encoding UTF8
        PT pack-skin --dir $okSkin --out "$ui/ok.pak" | Out-Host
        $cases = @(
            @{ Name = "fresh install, no skin"; Args = @(); Theme = "built-in" },
            @{ Name = "--skin none"; Args = @("--skin", "none"); Theme = "built-in" },
            @{ Name = "garbage skin pack"; Args = @("--skin", "`"$ui/garbage.pak`""); Theme = "built-in" },
            @{ Name = "skin with a broken image"; Args = @("--skin", "`"$work/skin-a.pak`""); Theme = "built-in" },
            @{ Name = "valid skin"; Args = @("--skin", "`"$ui/ok.pak`""); Theme = "e2e ok" }
        )
        foreach ($c in $cases) {
            $log = "$ui/selftest-$([guid]::NewGuid().ToString('N').Substring(0,6)).log"
            $p = Start-Process "$ui/BritanniaReborn.exe" -ArgumentList (@("--ui-selftest", "--log", "`"$log`"") + $c.Args) -PassThru
            if (-not $p.WaitForExit(60000)) { Stop-Process $p -Force; Fail "UI self-test hung ($($c.Name))" }
            $text = Get-Content $log -Raw -ErrorAction SilentlyContinue
            if ($p.ExitCode -ne 0 -or $text -notmatch "UI SELFTEST OK") { $text | Out-Host; Fail "launcher window failed ($($c.Name)), exit $($p.ExitCode)" }
            if ($text -notmatch "Theme: $([regex]::Escape($c.Theme))") { $text | Out-Host; Fail "expected the '$($c.Theme)' look ($($c.Name))" }
            Write-Host "OK: window opened and drew with the $($c.Theme) look ($($c.Name))" -ForegroundColor Green
        }
        if (Test-Path "$ui/Game") { Fail "UI self-test created a Game folder" }

        Step "Launcher install folder: --install-dir, saved choice, --move-install, interrupted move, game in use"
        $loc = "$work/loc"; New-Item -ItemType Directory $loc | Out-Null
        Copy-Item "$work/gu-1.0.1/BritanniaReborn.exe" $loc
        @{   # no installDir: the folder comes from the argument / saved choice
            channelUrl     = $url
            selfUpdate     = $false
            extraPatchKeys = @([IO.File]::ReadAllText($patchPub))
        } | ConvertTo-Json | Set-Content "$loc/BritanniaReborn.json" -Encoding UTF8
        $saved = "$loc/BritanniaReborn.install.json"
        function Run-Launcher([string[]]$extra) {
            $log = "$loc/run-$([guid]::NewGuid().ToString('N').Substring(0,6)).log"
            $quoted = @("--headless", "--log", "`"$log`"") + ($extra | ForEach-Object { if ($_ -match ' ') { "`"$_`"" } else { $_ } })
            $p = Start-Process "$loc/BritanniaReborn.exe" -ArgumentList $quoted -PassThru -WindowStyle Hidden
            if (-not $p.WaitForExit(180000)) { Stop-Process $p -Force; Fail "launcher timed out ($extra)" }
            Get-Content $log -ErrorAction SilentlyContinue | Out-Host
            return @{ Code = $p.ExitCode; Log = (Get-Content $log -Raw -ErrorAction SilentlyContinue) }
        }
        $custom = [IO.Path]::GetFullPath("$work/custom dir/Britannia Reborn")
        $r = Run-Launcher @("--install-dir", $custom)
        if ($r.Code -ne 0) { Fail "install with --install-dir exited $($r.Code)" }
        Assert-SameTree $custom $v3 "install in --install-dir folder"
        if (Test-Path "$loc/Game") { Fail "--install-dir still created the default Game folder" }
        if (Test-Path $saved) { Fail "--install-dir must not be saved as the player's choice" }

        # What the window's Install button writes; later runs must go straight there.
        @{ installDir = $custom } | ConvertTo-Json | Set-Content $saved -Encoding UTF8
        $r = Run-Launcher @()
        if ($r.Code -ne 0 -or $r.Log -notmatch [regex]::Escape("Game folder: $custom")) { Fail "saved install folder was not used" }
        if ($r.Log -notmatch "Game is up to date") { Fail "saved install folder was re-downloaded" }
        if (Test-Path "$loc/Game") { Fail "default Game folder created despite a saved choice" }

        $moved = [IO.Path]::GetFullPath("$work/moved/BR")
        $r = Run-Launcher @("--move-install", $moved)
        if ($r.Code -ne 0) { Fail "--move-install exited $($r.Code)" }
        Assert-SameTree $moved $v3 "moved install"
        if (Test-Path $custom) { Fail "old install folder still exists after the move" }
        if ((Get-Content $saved -Raw | ConvertFrom-Json).installDir -ne $moved) { Fail "move did not save the new folder" }
        if ($r.Log -notmatch "Game is up to date") { Fail "moved install was not recognised (files re-downloaded)" }

        # A move cut off after parking the old folder: the next start must put it back.
        Rename-Item $moved "BR.moving"
        $r = Run-Launcher @()
        if ($r.Code -ne 0 -or -not (Test-Path "$moved/.patch/installed.json")) { Fail "interrupted move was not recovered" }
        Assert-SameTree $moved $v3 "install recovered after an interrupted move"

        # Game running (a file held open): the move must refuse and leave everything where it was.
        $lock = [IO.File]::Open("$moved/Game.exe", 'Open', 'Read', 'None')
        try { $r = Run-Launcher @("--move-install", "$work/elsewhere/BR") } finally { $lock.Dispose() }
        if ($r.Code -eq 0) { Fail "--move-install succeeded while the game was in use" }
        Assert-SameTree $moved $v3 "install left intact after a refused move"
        if (Test-Path "$work/elsewhere/BR") { Fail "refused move left files at the target" }
        if ((Get-Content $saved -Raw | ConvertFrom-Json).installDir -ne $moved) { Fail "refused move changed the saved folder" }
        Write-Host "OK: install folder chosen, remembered, moved, recovered, and protected while in use" -ForegroundColor Green

        Step "Launcher log file: every run is logged, a failure names its cause and keeps full details"
        # v0.5.0 showed a bare "ERROR:" for a failure without a message, and kept no log file.
        $blocker = "$work/blocked-by-a-file"; Set-Content $blocker "not a folder"
        $r = Run-Launcher @("--install-dir", $blocker)
        if ($r.Code -eq 0) { Fail "update into a path that is a file succeeded" }
        if ($r.Log -notmatch "ERROR: \S") { Fail "the error line has no reason" }
        $file = "$loc/BritanniaReborn.log"
        if ($r.Log -notmatch [regex]::Escape("Full details are in $([IO.Path]::GetFullPath($file))")) { Fail "the error does not point at the log file" }
        $text = Get-Content $file -Raw -ErrorAction SilentlyContinue
        if ($text -notmatch "Game folder: $([regex]::Escape($moved))") { Fail "earlier runs are missing from the log file" }
        if ($text -notmatch "Details: System\.IO\.IOException" -or $text -notmatch "   at ") { Fail "the log file lacks the exception type and stack" }
        Write-Host "OK: runs logged to BritanniaReborn.log; the failure gave a reason and full details" -ForegroundColor Green
    }

    Write-Host "`nALL E2E CHECKS PASSED" -ForegroundColor Green
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process $server -Force }
}
