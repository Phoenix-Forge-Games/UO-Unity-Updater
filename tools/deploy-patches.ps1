<#
.SYNOPSIS
  Uploads a local patch-host mirror (made by `patchtool publish`) to the real host folder.

.DESCRIPTION
  The upload order keeps clients safe while it runs:
    1. blobs/            new blobs only; existing blobs never change, since they're named by hash
    2. */manifests, */history
    3. */channel.json.sig, then */channel.json   (the switch-over)
    4. with -Purge: delete host blobs that are no longer in the mirror (after `patchtool prune`)

  Destination is any path robocopy can write: a UNC share on phoenix-server, or a local folder.

.EXAMPLE
  ./tools/deploy-patches.ps1 -HostRoot "G:\Ultima Online\PatchHost" -Destination "\\phoenix-server\Patches"
#>
param(
    [Parameter(Mandatory)] [string] $HostRoot,
    [Parameter(Mandatory)] [string] $Destination,
    [switch] $Purge,
    [switch] $WhatIf
)
$ErrorActionPreference = 'Stop'
$HostRoot = (Resolve-Path $HostRoot).Path

function Robo([string]$src, [string]$dst, [string[]]$extra) {
    $flags = @('/E', '/R:3', '/W:5', '/NP', '/NFL', '/NDL', '/XF', '*.tmp') + $extra
    if ($WhatIf) { $flags += '/L' }
    & robocopy $src $dst @flags | Out-Host
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $src -> $dst" }
}

# 1. Blobs: copy only files the destination doesn't have.
if (Test-Path "$HostRoot\blobs") { Robo "$HostRoot\blobs" "$Destination\blobs" @('/XC', '/XN', '/XO') }

$channels = Get-ChildItem $HostRoot -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'channel.json') }
foreach ($c in $channels) {
    # 2. Manifests and history.
    foreach ($sub in 'manifests', 'history') {
        if (Test-Path "$($c.FullName)\$sub") { Robo "$($c.FullName)\$sub" "$Destination\$($c.Name)\$sub" @() }
    }
    # 3. Switch the channel over: signature first, then the pointer.
    if (-not $WhatIf) {
        New-Item -ItemType Directory -Force "$Destination\$($c.Name)" | Out-Null
        Copy-Item "$($c.FullName)\channel.json.sig" "$Destination\$($c.Name)\channel.json.sig.tmp" -Force
        Copy-Item "$($c.FullName)\channel.json" "$Destination\$($c.Name)\channel.json.tmp" -Force
        Move-Item "$Destination\$($c.Name)\channel.json.sig.tmp" "$Destination\$($c.Name)\channel.json.sig" -Force
        Move-Item "$Destination\$($c.Name)\channel.json.tmp" "$Destination\$($c.Name)\channel.json" -Force
    }
    Write-Host "Channel $($c.Name) -> $((Get-Content "$($c.FullName)\channel.json" -Raw | ConvertFrom-Json).version)"
}

# 4. Optional cleanup of blobs pruned from the mirror.
if ($Purge) { Robo "$HostRoot\blobs" "$Destination\blobs" @('/MIR') }
