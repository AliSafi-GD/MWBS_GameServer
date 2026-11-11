# One-click test run for GameServer (Windows PowerShell 5.1 or PowerShell 7).
# From the project folder:   powershell -ExecutionPolicy Bypass -File .\run-tests.ps1
# It builds everything, starts Postgres (Docker) if it can, starts the server, runs tests/GameServer.SmokeTest and
# sdk/unity/SdkSmokeTest (the Unity SDK sources against the server), stops the server and writes ONE file:
# logs\REPORT.txt  <- send me this file.
param(
    [string]$Url = "http://localhost:5000",
    [string]$Transports = "websocket,tcp",
    [string]$MatchTransports = "websocket,udp",
    [switch]$SkipDocker
)

$ErrorActionPreference = "Continue"
Set-Location -Path $PSScriptRoot
$logs = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Force -Path $logs | Out-Null
Get-ChildItem $logs -File | Remove-Item -Force -ErrorAction SilentlyContinue

function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }

$report = New-Object System.Collections.Generic.List[string]
$report.Add("GameServer test report  $(Get-Date -Format s)")
$report.Add("dotnet: $(dotnet --version 2>&1)")
$report.Add("os: $([System.Environment]::OSVersion.VersionString)")
$report.Add("")

# ---- 1. Postgres ---------------------------------------------------------------------------------------
Step "Postgres"
if (-not (Test-Path ".env") -and (Test-Path ".env.example")) { Copy-Item ".env.example" ".env"; Write-Host "created .env from .env.example" }
if (-not $SkipDocker -and (Get-Command docker -ErrorAction SilentlyContinue)) {
    docker compose up -d postgres *> (Join-Path $logs "docker.log")
    $report.Add("docker compose up -d postgres: exit $LASTEXITCODE")
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        docker compose exec -T postgres pg_isready -U game -d game *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 2
    }
    $report.Add("postgres ready: $ready")
    Write-Host "postgres ready: $ready"
} else {
    $report.Add("docker not used - make sure Postgres is running (see appsettings.Development.json)")
    Write-Host "docker not used - Postgres must already be running"
}

# ---- 2. Build ------------------------------------------------------------------------------------------
# A server left running from an earlier run (or started from Rider) locks the DLLs and the build fails with MSB3021/MSB3027.
Get-Process -Name "GameServer.Host" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Step "dotnet build GameServer.slnx"
dotnet build GameServer.slnx -c Debug *> (Join-Path $logs "build.log")
$buildExit = $LASTEXITCODE
dotnet build tests/GameServer.SmokeTest -c Debug *>> (Join-Path $logs "build.log")
if ($LASTEXITCODE -ne 0) { $buildExit = $LASTEXITCODE }
dotnet build sdk/unity/SdkSmokeTest -c Debug *>> (Join-Path $logs "build.log")
if ($LASTEXITCODE -ne 0) { $buildExit = $LASTEXITCODE }
dotnet build tests/GameServer.ArenaBots -c Debug *>> (Join-Path $logs "build.log")
if ($LASTEXITCODE -ne 0) { $buildExit = $LASTEXITCODE }
$report.Add("dotnet build exit code: $buildExit")
Write-Host "build exit code: $buildExit"

$buildLog = Get-Content (Join-Path $logs "build.log") -ErrorAction SilentlyContinue
$errors = $buildLog | Where-Object { $_ -match "error (CS|MSB|NU|NETSDK)\d+" } | Sort-Object -Unique
if ($errors) {
    $report.Add("")
    $report.Add("---- BUILD ERRORS ($($errors.Count)) ----")
    $errors | ForEach-Object { $report.Add($_.Trim()) }
}

if ($buildExit -ne 0) {
    $report.Add("")
    $report.Add("Build failed, tests were not run.")
    $report | Set-Content (Join-Path $logs "REPORT.txt")
    Write-Host ""
    Write-Host "BUILD FAILED. Send logs\REPORT.txt" -ForegroundColor Red
    exit 1
}

# ---- 3. Sample module ----------------------------------------------------------------------------------
Step "publish sample module"
dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules *> (Join-Path $logs "module.log")
$report.Add("publish sample module exit code: $LASTEXITCODE")

# ---- 4. Start server -----------------------------------------------------------------------------------
Step "start server ($Url, realtime: $Transports, match: $MatchTransports)"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = $Url
$env:GameServer__Realtime__Transports = $Transports
$env:GameServer__Match__Transports = $MatchTransports
$serverOut = Join-Path $logs "server.log"
$serverErr = Join-Path $logs "server-error.log"
$server = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--project", "src/GameServer.Host", "--no-build", "--no-launch-profile") `
    -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr -PassThru -WindowStyle Hidden

$up = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    if ($server.HasExited) { break }
    try { $r = Invoke-WebRequest -Uri "$Url/healthz" -UseBasicParsing -TimeoutSec 2; if ($r.StatusCode -eq 200) { $up = $true; break } } catch { }
}
$report.Add("server started: $up")
Write-Host "server started: $up"

$testExit = 1
if ($up) {
    # ---- 5. Tests --------------------------------------------------------------------------------------
    Step "run smoke tests"
    Push-Location $logs
    dotnet run --project (Join-Path $PSScriptRoot "tests/GameServer.SmokeTest") -c Debug -- $Url *> (Join-Path $logs "smoke-console.log")
    $testExit = $LASTEXITCODE
    Pop-Location
    $report.Add("smoke test exit code: $testExit")
    Get-Content (Join-Path $logs "smoke-console.log") -ErrorAction SilentlyContinue | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }

    Step "run SDK tests (Unity SDK sources)"
    Push-Location $logs
    dotnet run --project (Join-Path $PSScriptRoot "sdk/unity/SdkSmokeTest") -c Debug --no-build -- $Url *> (Join-Path $logs "sdk-console.log")
    $sdkExit = $LASTEXITCODE
    Pop-Location
    $report.Add("sdk test exit code: $sdkExit")
    if ($sdkExit -ne 0) { $testExit = $sdkExit }
    Get-Content (Join-Path $logs "sdk-console.log") -ErrorAction SilentlyContinue | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }

    Step "run Tank Arena bots (8 players, full matches; takes up to ~2 minutes)"
    Push-Location $logs
    dotnet run --project (Join-Path $PSScriptRoot "tests/GameServer.ArenaBots") -c Debug --no-build -- $Url "dev-admin-key-do-not-use-in-production-0123456789" 8 *> (Join-Path $logs "arena-console.log")
    $arenaExit = $LASTEXITCODE
    Pop-Location
    $report.Add("arena bots exit code: $arenaExit")
    if ($arenaExit -ne 0) { $testExit = $arenaExit }
    Get-Content (Join-Path $logs "arena-console.log") -ErrorAction SilentlyContinue | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
} else {
    $report.Add("The server did not start - see server.log / server-error.log below.")
}

# ---- 6. Stop server ------------------------------------------------------------------------------------
Step "stop server"
if (-not $server.HasExited) { & taskkill /PID $server.Id /T /F *> $null }
Start-Sleep -Seconds 1

# ---- 7. Report -----------------------------------------------------------------------------------------
$report.Add("")
$report.Add("==================== SMOKE TEST LOG ====================")
$smoke = Join-Path $logs "smoke-test.log"
if (Test-Path $smoke) { Get-Content $smoke | ForEach-Object { $report.Add($_) } } else {
    $report.Add("(no smoke-test.log) console output:")
    Get-Content (Join-Path $logs "smoke-console.log") -ErrorAction SilentlyContinue | Select-Object -Last 80 | ForEach-Object { $report.Add($_) }
}

$report.Add("")
$report.Add("==================== SDK TEST LOG ====================")
$sdkLog = Join-Path $logs "sdk-test.log"
if (Test-Path $sdkLog) { Get-Content $sdkLog | ForEach-Object { $report.Add($_) } } else {
    $report.Add("(no sdk-test.log) console output:")
    Get-Content (Join-Path $logs "sdk-console.log") -ErrorAction SilentlyContinue | Select-Object -Last 80 | ForEach-Object { $report.Add($_) }
}

$report.Add("")
$report.Add("==================== ARENA BOTS LOG ====================")
$arenaLog = Join-Path $logs "arena-bots.log"
if (Test-Path $arenaLog) { Get-Content $arenaLog | ForEach-Object { $report.Add($_) } } else {
    $report.Add("(no arena-bots.log) console output:")
    Get-Content (Join-Path $logs "arena-console.log") -ErrorAction SilentlyContinue | Select-Object -Last 80 | ForEach-Object { $report.Add($_) }
}

$report.Add("")
$report.Add("==================== SERVER LOG (last 400 lines) ====================")
if (Test-Path $serverOut) { Get-Content $serverOut | Select-Object -Last 400 | ForEach-Object { $report.Add($_) } }
if ((Test-Path $serverErr) -and (Get-Item $serverErr).Length -gt 0) {
    $report.Add("---- stderr ----")
    Get-Content $serverErr | Select-Object -Last 200 | ForEach-Object { $report.Add($_) }
}

$report.Add("")
$report.Add("==================== BUILD WARNINGS (unique, first 60) ====================")
$buildLog | Where-Object { $_ -match "warning (CS|CA)\d+" } | Sort-Object -Unique | Select-Object -First 60 | ForEach-Object { $report.Add($_.Trim()) }

$reportPath = Join-Path $logs "REPORT.txt"
$report | Set-Content $reportPath -Encoding UTF8
Write-Host ""
if ($testExit -eq 0) { Write-Host "ALL TESTS PASSED" -ForegroundColor Green } else { Write-Host "SOME TESTS FAILED" -ForegroundColor Red }
Write-Host "Report: $reportPath  (send me this file)"
exit $testExit
