#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Version,
    [string]$InnoSetupCompiler,
    [switch]$NoRestore,
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    if (-not $fullPath.StartsWith($fullParent + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe release path outside $fullParent`: $fullPath"
    }

    return $fullPath
}

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$buildProperties = Get-Content (Join-Path $repository 'Directory.Build.props')
    $Version = [string]$buildProperties.Project.PropertyGroup.Version
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid release version '$Version'."
}

$iscc = $null
if (-not $SkipInstaller) {
    $isccCandidates = @(
        $InnoSetupCompiler,
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $iscc = $isccCandidates | Select-Object -First 1
    if ($null -eq $iscc) {
        throw 'Inno Setup 6 compiler (ISCC.exe) was not found. Install Inno Setup 6, add ISCC.exe to PATH, pass -InnoSetupCompiler, or explicitly request a portable-only build with -SkipInstaller.'
    }
    $iscc = [System.IO.Path]::GetFullPath([string]$iscc)
}

$packageName = "HbkWwise-$Version-win-x64"
$stage = Assert-ChildPath (Join-Path $artifacts $packageName) $artifacts
$zip = Assert-ChildPath (Join-Path $artifacts "$packageName.zip") $artifacts
$setup = Assert-ChildPath (Join-Path $artifacts "HbkWwise-$Version-Setup.exe") $artifacts
$releaseChecksums = Assert-ChildPath (Join-Path $artifacts "HbkWwise-$Version-SHA256SUMS.txt") $artifacts

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
foreach ($oldFile in @($zip, $setup, $releaseChecksums)) {
    if (Test-Path -LiteralPath $oldFile) {
        Remove-Item -LiteralPath $oldFile -Force
    }
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Push-Location $repository
try {
    if (-not $NoRestore -and -not $SkipTests) {
        Invoke-DotNet @('restore', 'tests/HbkWwise.Core.Tests/HbkWwise.Core.Tests.csproj')
    }
    if (-not $SkipTests) {
        Invoke-DotNet @('test', 'tests/HbkWwise.Core.Tests/HbkWwise.Core.Tests.csproj',
            '-c', 'Release', '--no-restore')
    }
    if (-not $NoRestore) {
        # Restore the runtime target last. A generic test restore can replace the Core
        # assets file and make the following win-x64 publish fail with NETSDK1047.
        Invoke-DotNet @('restore', 'src/HbkWwise.Gui/HbkWwise.Gui.csproj', '--runtime', 'win-x64')
    }

    $commonPublish = @(
        '-c', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $stage,
        '--no-restore',
        "-p:Version=$Version",
        '-p:DebugSymbols=false',
        '-p:DebugType=None',
        '-p:PublishTrimmed=false'
    )
    Invoke-DotNet (@('publish', 'src/HbkWwise.Gui/HbkWwise.Gui.csproj') + $commonPublish)
}
finally {
    Pop-Location
}

Copy-Item -LiteralPath (Join-Path $repository 'README.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE.txt') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repository 'THIRD-PARTY-NOTICES.txt') -Destination $stage

$licenseRoot = Join-Path $stage 'licenses'
$dotnetLicenseRoot = Join-Path $licenseRoot 'dotnet'
$nugetLicenseRoot = Join-Path $licenseRoot 'nuget'
New-Item -ItemType Directory -Path $dotnetLicenseRoot, $nugetLicenseRoot -Force | Out-Null

$dotnetRoot = Split-Path (Get-Command dotnet -ErrorAction Stop).Source
foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $source = Join-Path $dotnetRoot $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "The .NET distribution notice is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $dotnetLicenseRoot
}

$assetsPath = Join-Path $repository 'src/HbkWwise.Gui/obj/project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$packageFolder = @($assets.packageFolders.PSObject.Properties.Name)[0]
if ([string]::IsNullOrWhiteSpace($packageFolder)) {
    throw 'NuGet package folder could not be resolved from project.assets.json.'
}

$packageMetadata = New-Object System.Collections.Generic.List[string]
foreach ($libraryProperty in $assets.libraries.PSObject.Properties) {
    if ([string]$libraryProperty.Value.type -ne 'package') {
        continue
    }

    $parts = $libraryProperty.Name -split '/', 2
    $id = $parts[0]
    $packageVersion = $parts[1]
    $packagePath = Join-Path $packageFolder (Join-Path $id.ToLowerInvariant() $packageVersion)
    $nuspec = Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' -File | Select-Object -First 1
    $licenseDescription = 'not declared in package metadata'
    if ($null -ne $nuspec) {
        [xml]$nuspecXml = Get-Content -LiteralPath $nuspec.FullName
        $licenseNode = $nuspecXml.package.metadata.license
        if ($null -ne $licenseNode) {
            $licenseDescription = "$($licenseNode.type): $($licenseNode.InnerText)"
        }
    }
    $packageMetadata.Add("$id $packageVersion - $licenseDescription")

    $destination = Join-Path $nugetLicenseRoot "$id-$packageVersion"
    $notices = @(Get-ChildItem -LiteralPath $packagePath -File -Recurse | Where-Object {
        $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD-PARTY-NOTICES)(\..*)?$'
    })
    if ($notices.Count -gt 0) {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($notice in $notices) {
            $relative = $notice.FullName.Substring($packagePath.Length).TrimStart('\', '/')
            $safeName = $relative -replace '[\\/:*?"<>|]', '_'
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $destination $safeName)
        }
    }
}
$packageMetadata | Sort-Object | Set-Content -LiteralPath (Join-Path $nugetLicenseRoot 'PACKAGE-LICENSES.txt') -Encoding UTF8

$forbiddenFiles = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object {
    $_.Name -match '(?i)^(oo2core.*\.dll|WwiseConsole\.exe|wwiser\.(pyz|py)|vgmstream.*\.exe)$' -or
    $_.Extension -match '(?i)^\.(pak|bnk|wem)$'
})
if ($forbiddenFiles.Count -gt 0) {
    throw "Forbidden redistributable content entered the release: $($forbiddenFiles.FullName -join '; ')"
}

$textExtensions = @('.txt', '.md', '.json', '.config', '.ps1', '.deps.json', '.runtimeconfig.json')
foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse) {
    if ($textExtensions -notcontains $file.Extension.ToLowerInvariant() -and
        $file.Name -notmatch '\.(deps|runtimeconfig)\.json$') {
        continue
    }
    $text = [System.IO.File]::ReadAllText($file.FullName)
    if ($text -match '(?i)0x[0-9a-f]{64}') {
        throw "A value shaped like an AES-256 key was found in $($file.FullName)."
    }
}

$checksums = Get-ChildItem -LiteralPath $stage -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
$checksums | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding ASCII

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $icon = Join-Path $repository 'src/HbkWwise.Gui/Assets/HbkWwise.ico'
    & $iscc "/DSourceDir=$stage" "/DAppVersion=$Version" "/DOutputDir=$artifacts" "/DIconFile=$icon" (Join-Path $PSScriptRoot 'HbkWwise.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
        throw "Inno Setup completed without creating the expected installer: $setup"
    }
}

$releaseFiles = @($zip)
if (-not $SkipInstaller) {
    $releaseFiles += $setup
}
$releaseFiles |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([System.IO.Path]::GetFileName($_))"
    } |
    Set-Content -LiteralPath $releaseChecksums -Encoding ASCII

Write-Host "Release folder: $stage"
Write-Host "Portable ZIP:   $zip"
if (-not $SkipInstaller) {
    Write-Host "Installer:      $setup"
}
else {
    Write-Host 'Installer:      skipped explicitly'
}
Write-Host "Checksums:      $releaseChecksums"
