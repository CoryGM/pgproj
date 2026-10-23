<#
.SYNOPSIS
Runs the test suite against every supported PostgreSQL version (G-269, the user's decision of 2026-10-13).

.DESCRIPTION
Builds, runs the unit tests once, then runs the Docker-backed integration suite once per version, in parallel, at most three at a time, each with a
timeout, and prints a summary line per version. Logs go to artifacts/test-runs/<UTC time>/pg<version>.log. The exit code is 0 only when every run passed.
The test DLLs are shared by the runs, so do not rebuild while one is going.

.EXAMPLE
tools/test-all-versions.ps1
tools/test-all-versions.ps1 -Versions 15, 18
tools/test-all-versions.ps1 -MaxParallel 2 -TimeoutMinutes 45 -NoBuild
#>
param(
    [int[]] $Versions = @(14, 15, 16, 17, 18),
    [int] $MaxParallel = 3,
    [int] $TimeoutMinutes = 40,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$logs = Join-Path $repo ("artifacts/test-runs/" + [DateTime]::UtcNow.ToString('yyyy-MM-dd-HH-mm-ss'))
New-Item -ItemType Directory -Force $logs | Out-Null

if (-not $NoBuild) {
    Write-Host 'Building...'
    dotnet build --nologo -v quiet *> (Join-Path $logs 'build.log')
    if ($LASTEXITCODE -ne 0) { Write-Host "Build failed; see $logs/build.log"; exit 1 }
}

# The unit tests do not depend on the server, so they run once.
$unitLog = Join-Path $logs 'unit.log'
$unitFailed = $false
foreach ($project in Get-ChildItem src -Directory -Filter '*.Tests' | Where-Object Name -ne 'PgProj.Integration.Tests') {
    dotnet test $project.FullName --no-build --nologo *>> $unitLog
    if ($LASTEXITCODE -ne 0) { $unitFailed = $true }
}
$unitText = Get-Content $unitLog -Raw
Write-Host ("Unit tests: {0} project(s) passed, {1} failed (log: {2})" -f ([regex]::Matches($unitText, '(?m)^Passed!').Count), ([regex]::Matches($unitText, '(?m)^Failed!').Count), $unitLog)

Write-Host "Integration tests: $($Versions -join ' '), at most $MaxParallel at a time, $TimeoutMinutes minutes each..."
$results = $Versions | ForEach-Object -ThrottleLimit $MaxParallel -Parallel {
    $version = $_
    $log = Join-Path $using:logs "pg$version.log"
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet', "test src/PgProj.Integration.Tests --no-build --nologo")
    $start.WorkingDirectory = $using:repo
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['PGPROJ_TEST_POSTGRES_IMAGE'] = "postgres:$version-alpine"
    Write-Host "  started PostgreSQL $version"
    $process = [System.Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($using:TimeoutMinutes * 60000)
    if ($timedOut) { $process.Kill($true) }
    $process.WaitForExit()
    Set-Content -Path $log -Value ($output.Result + $errors.Result)
    [pscustomobject]@{ Version = $version; ExitCode = if ($timedOut) { 124 } else { $process.ExitCode }; Log = $log }
}

$status = if ($unitFailed) { 1 } else { 0 }
Write-Host ''
foreach ($result in $results | Sort-Object Version) {
    $summary = (Select-String -Path $result.Log -Pattern '^(Passed|Failed)!' | Select-Object -First 1).Line -replace ' - PgProj.*', '' -replace ',? *Duration:.*', ''
    if ($result.ExitCode -eq 124) { $summary = "TIMED OUT after $TimeoutMinutes minutes" }
    if ($result.ExitCode -ne 0) { $status = 1 }
    Write-Host ("PostgreSQL {0,-3} {1}  ({2})" -f $result.Version, ($summary ?? 'no result'), $result.Log)
}
exit $status
