<#
.SYNOPSIS
  Builds the offline release bundle of the GameServer into artifacts/release/<version>/.

.DESCRIPTION
  Everything a game team needs without internet access:
    gameserver-<v>-<rid>.(zip|tar.gz)   the server for one OS (self-contained: .NET included), with sdk/ and modules/
    gameserver-<v>-docker/              docker-compose.yml without "build", .env.example, images.tar.gz (server + postgres)
    examples/HelloGame.Modules/         the sample module, published
    SHA256SUMS.txt
  Nothing is published to the internet. Building needs the NuGet packages in the local cache (or internet) and,
  for the Docker part, the base image mcr.microsoft.com/dotnet/aspnet:10.0 on this machine.

.EXAMPLE
  pwsh tools/release/build-release.ps1
  pwsh tools/release/build-release.ps1 -Rids win-x64,linux-x64 -SkipDocker
  pwsh tools/release/build-release.ps1 -IncludeBuildImages   # also saves the .NET base image, to rebuild images offline
#>
param(
    [string]$Version,
    [string[]]$Rids = @('linux-x64', 'linux-musl-x64', 'linux-arm64', 'win-x64', 'osx-arm64'),
    [string]$PostgresImage = 'postgres:17',
    [switch]$SkipDocker,
    [switch]$IncludeBuildImages,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

function Step($text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }

function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

# A text file from the repository with placeholders filled in. Shell scripts get LF line endings (they run on Linux).
function Write-Text([string]$from, [string]$to, [hashtable]$values) {
    $text = [IO.File]::ReadAllText((Join-Path $root $from))
    foreach ($key in $values.Keys) { $text = $text.Replace($key, $values[$key]) }
    if ($to.EndsWith('.sh')) { $text = $text.Replace("`r`n", "`n") }
    New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
    [IO.File]::WriteAllText($to, $text, [Text.UTF8Encoding]::new($false))
}

# docker load reads a gzipped image tar directly (the tar itself gzipped, not packed into another archive).
function Compress-Gzip([string]$from, [string]$to) {
    $in = [IO.File]::OpenRead($from)
    try {
        $out = [IO.File]::Create($to)
        try {
            $gz = [IO.Compression.GZipStream]::new($out, [IO.Compression.CompressionLevel]::Optimal)
            try { $in.CopyTo($gz) } finally { $gz.Dispose() }
        } finally { $out.Dispose() }
    } finally { $in.Dispose() }
}

# Release builds without this machine's paths in stack traces and with reproducible output.
$buildProps = @('-p:ContinuousIntegrationBuild=true', '-p:Deterministic=true')

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'src/GameServer.Abstractions/GameServer.Abstractions.csproj'))).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
if (-not $Output) { $Output = Join-Path $root "artifacts/release/$Version" }
$staging = Join-Path $root "artifacts/staging/$Version"
$values = @{ '__VERSION__' = $Version; '__POSTGRES__' = $PostgresImage }

Write-Host "GameServer release $Version -> $Output"
Remove-Item -Recurse -Force $Output, $staging -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Output, $staging | Out-Null

# ---- sdk/: the Abstractions package, the plain DLL and the module template (the same in every server archive) ----------
Step 'sdk: GameServer.Abstractions package and module template'
$sdk = Join-Path $staging 'sdk'
Run dotnet (@('pack', 'src/GameServer.Abstractions', '-c', 'Release', "-p:Version=$Version", '-o', (Join-Path $sdk 'packages'), '--nologo', '-v', 'q') + $buildProps)
$lib = Join-Path $sdk 'lib/net10.0'
New-Item -ItemType Directory -Force $lib | Out-Null
Copy-Item (Join-Path $root 'src/GameServer.Abstractions/bin/Release/net10.0/GameServer.Abstractions.dll'), `
          (Join-Path $root 'src/GameServer.Abstractions/bin/Release/net10.0/GameServer.Abstractions.xml') $lib
Copy-Item -Recurse (Join-Path $root 'templates/module') (Join-Path $sdk 'templates/module')
$templateJson = Join-Path $sdk 'templates/module/.template.config/template.json'
(Get-Content -Raw $templateJson).Replace('"defaultValue": "0.1.0"', "`"defaultValue`": `"$Version`"") | Set-Content -NoNewline $templateJson
Write-Text 'tools/release/bundle/SDK-README.md' (Join-Path $sdk 'README.md') $values

# ---- one archive per OS -----------------------------------------------------------------------------------------------
foreach ($rid in $Rids) {
    Step "server for $rid (self-contained)"
    $name = "gameserver-$Version-$rid"
    $dir = Join-Path $staging $name
    Run dotnet (@('publish', 'src/GameServer.Host', '-c', 'Release', '-r', $rid, '--self-contained', 'true',
                 "-p:Version=$Version", '-o', $dir, '--nologo', '-v', 'q') + $buildProps)
    Copy-Item -Recurse $sdk (Join-Path $dir 'sdk')
    New-Item -ItemType Directory -Force (Join-Path $dir 'modules') | Out-Null
    Write-Text 'tools/release/bundle/modules-README.txt' (Join-Path $dir 'modules/README.txt') $values
    Write-Text 'tools/release/bundle/INSTALL.md' (Join-Path $dir 'INSTALL.md') ($values + @{ '__RID__' = $rid })

    if ($rid -like 'win-*') {
        Compress-Archive -Path (Join-Path $dir '*') -DestinationPath (Join-Path $Output "$name.zip")
    } else {
        Run tar @('-czf', (Join-Path $Output "$name.tar.gz"), '-C', $dir, '.')
    }
}

# ---- the sample module ------------------------------------------------------------------------------------------------
Step 'examples: HelloGame.Modules'
Run dotnet (@('publish', 'samples/HelloGame.Modules', '-c', 'Release', '-o', (Join-Path $Output 'examples/HelloGame.Modules'), '--nologo', '-v', 'q') + $buildProps)

# ---- Docker: image built from a local publish (no NuGet in the container), saved with postgres -------------------------
if (-not $SkipDocker) {
    Step "docker: gameserver:$Version"
    $publish = Join-Path $staging 'docker-publish'
    Run dotnet (@('publish', 'src/GameServer.Host', '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'false',
                 "-p:Version=$Version", '-o', $publish, '--nologo', '-v', 'q') + $buildProps)
    Run docker @('build', '-f', 'tools/release/Dockerfile', '-t', "gameserver:$Version", $publish)

    $images = @("gameserver:$Version", $PostgresImage)
    & docker image inspect $PostgresImage *> $null
    if ($LASTEXITCODE -ne 0) { Run docker @('pull', $PostgresImage) }

    $dockerDir = Join-Path $Output "gameserver-$Version-docker"
    New-Item -ItemType Directory -Force (Join-Path $dockerDir 'modules') | Out-Null
    Write-Text 'tools/release/bundle/modules-README.txt' (Join-Path $dockerDir 'modules/README.txt') $values
    Write-Text 'tools/release/bundle/docker/INSTALL.md' (Join-Path $dockerDir 'INSTALL.md') $values
    Write-Text 'tools/release/bundle/docker/load-images.sh' (Join-Path $dockerDir 'load-images.sh') $values
    Write-Text 'tools/release/bundle/docker/load-images.ps1' (Join-Path $dockerDir 'load-images.ps1') $values
    Copy-Item (Join-Path $root '.env.example') (Join-Path $dockerDir '.env.example')

    # The repository's compose file, but with the saved image instead of "build: ." and the database not published.
    $compose = [IO.File]::ReadAllText((Join-Path $root 'docker-compose.yml')).Replace("`r`n", "`n")
    if (-not $compose.Contains("    build: .`n")) { throw 'docker-compose.yml: "build: ." not found' }
    $compose = $compose.Replace("    build: .`n", "    image: gameserver:$Version`n")
    $portsBlock = "    ports:`n      - `"127.0.0.1:5432:5432`"   # local access for development (e.g. ``dotnet run`` on your machine)`n"
    if (-not $compose.Contains($portsBlock)) { throw 'docker-compose.yml: the postgres ports block was not found' }
    $compose = $compose.Replace($portsBlock, "    # Not reachable from outside the containers. Add: ports: [`"127.0.0.1:5432:5432`"] to reach it from this machine.`n")
    $compose = $compose.Replace("    image: postgres:17`n", "    image: $PostgresImage`n")
    $compose = "# GameServer $Version, offline: run ./load-images.sh once, copy .env.example to .env, then: docker compose up -d`n" + $compose
    [IO.File]::WriteAllText((Join-Path $dockerDir 'docker-compose.yml'), $compose, [Text.UTF8Encoding]::new($false))

    Step "docker: saving $($images -join ', ')"
    $tar = Join-Path $staging 'images.tar'
    Run docker (@('save', '-o', $tar) + $images)
    Compress-Gzip $tar (Join-Path $dockerDir 'images.tar.gz')
    Remove-Item $tar

    if ($IncludeBuildImages) {
        Step 'docker: base image for building release images offline'
        $buildDir = Join-Path $dockerDir 'build'
        New-Item -ItemType Directory -Force $buildDir | Out-Null
        Copy-Item (Join-Path $root 'tools/release/Dockerfile') $buildDir
        # docker build may have used the base image from its build cache only; docker save needs it as an image.
        & docker image inspect 'mcr.microsoft.com/dotnet/aspnet:10.0' *> $null
        if ($LASTEXITCODE -ne 0) { Run docker @('pull', 'mcr.microsoft.com/dotnet/aspnet:10.0') }
        $baseTar = Join-Path $staging 'base.tar'
        Run docker @('save', '-o', $baseTar, 'mcr.microsoft.com/dotnet/aspnet:10.0')
        Compress-Gzip $baseTar (Join-Path $buildDir 'aspnet-10.0.tar.gz')
        Remove-Item $baseTar
    }
}

# ---- checksums -------------------------------------------------------------------------------------------------------
Step 'checksums'
$sums = Get-ChildItem -Recurse -File $Output | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($Output, $_.FullName).Replace('\', '/')
    "$((Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant())  $relative"
}
[IO.File]::WriteAllLines((Join-Path $Output 'SHA256SUMS.txt'), $sums)
Remove-Item -Recurse -Force $staging

Write-Host ''
Get-ChildItem $Output | ForEach-Object {
    $size = if ($_.PSIsContainer) { (Get-ChildItem -Recurse -File $_.FullName | Measure-Object Length -Sum).Sum } else { $_.Length }
    '{0,-45} {1,8:N1} MB' -f $_.Name, ($size / 1MB)
}
Write-Host "Done: $Output" -ForegroundColor Green
