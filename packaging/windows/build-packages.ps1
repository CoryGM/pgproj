<#
.SYNOPSIS
  Builds the Windows artifacts of one runtime (G-81): pgproj-<version>-<rid>.zip and pgproj-<version>-<rid>.msi.
.DESCRIPTION
  Publishes pgproj self-contained, signs the executables when a code-signing certificate is configured, then makes the zip
  and the MSI installer (WiX 5: `dotnet tool install --global wix --version 5.0.2`) and signs the MSI.
  Signing is on only when WINDOWS_SIGNING_CERT_FILE (a .pfx path) and WINDOWS_SIGNING_CERT_PASSWORD are set; without them the
  artifacts are unsigned and Windows SmartScreen will warn on first run.
.EXAMPLE
  packaging/windows/build-packages.ps1 -Rid win-x64 -Version 0.1.0 -Output dist
#>
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path
$name = "pgproj-$Version-$Rid"
$publishDir = Join-Path $Output $name
$wixArch = $Rid -replace '^win-', ''

function Invoke-Native {
    & $args[0] $args[1..($args.Count - 1)]
    if ($LASTEXITCODE -ne 0) { throw "$($args[0]) failed with exit code $LASTEXITCODE" }
}

function Find-SignTool {
    $found = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw 'signtool.exe was not found (install the Windows SDK).' }
    $found.FullName
}

function Sign-File([string[]]$Path) {
    if (-not $env:WINDOWS_SIGNING_CERT_FILE) { return }
    $signtool = Find-SignTool
    Invoke-Native $signtool sign /fd SHA256 /td SHA256 /tr 'http://timestamp.digicert.com' `
        /f $env:WINDOWS_SIGNING_CERT_FILE /p $env:WINDOWS_SIGNING_CERT_PASSWORD $Path
}

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
Invoke-Native dotnet publish (Join-Path $root 'src\PgProj.Cli\PgProj.Cli.csproj') `
    --configuration Release --runtime $Rid --self-contained true `
    -p:DebugType=none -p:DebugSymbols=false "-p:MinVerVersion=$Version" --output $publishDir

foreach ($doc in 'README.md', 'pgproj.json.md', 'documentation.md', 'CHANGELOG.md', 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.txt') {
    $path = Join-Path $root $doc
    if (Test-Path $path) { Copy-Item $path $publishDir }
}
# The README's notes, at the same relative path as in the repository so its links work (pgproj.wxs installs the whole folder).
$docsDir = Join-Path $publishDir 'docs'
New-Item -ItemType Directory -Force $docsDir | Out-Null
Copy-Item -Recurse (Join-Path $root 'docs\notes') $docsDir
Copy-Item (Join-Path $root 'docs\renaming-columns.md') $docsDir

# Our own binaries only; the .NET runtime files are already signed by Microsoft.
Sign-File (Get-ChildItem $publishDir -Include 'pgproj.exe', 'PgProj.*.dll' -Recurse | ForEach-Object FullName)

$zip = Join-Path $Output "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $publishDir -DestinationPath $zip -CompressionLevel Optimal

# An MSI version is major.minor.patch (a pre-release suffix cannot be expressed).
$msiVersion = ($Version -split '[-+]')[0]
$msi = Join-Path $Output "$name.msi"
Invoke-Native wix build -arch $wixArch -d "Version=$msiVersion" -d "PublishDir=$publishDir" `
    -o $msi (Join-Path $PSScriptRoot 'pgproj.wxs')
Sign-File $msi

Get-ChildItem $Output -File | Format-Table Name, Length
