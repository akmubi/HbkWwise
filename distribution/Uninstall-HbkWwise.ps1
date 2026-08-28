#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = (Get-ItemProperty -Path 'HKCU:\Software\HBK Wwise' -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
}
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = $PSScriptRoot
}
$target = [System.IO.Path]::GetFullPath($InstallDirectory)
$manifest = Join-Path $target 'install-manifest.txt'
if (-not (Test-Path -LiteralPath (Join-Path $target 'HbkWwise.exe')) -or
    -not (Test-Path -LiteralPath $manifest)) {
    throw "The selected directory is not a manifest-based HBK Wwise installation: $target"
}

Remove-Item -LiteralPath 'HKCU:\Software\Classes\Applications\HbkWwise.exe' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKCU:\Software\Classes\.hbkproj' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKCU:\Software\Classes\HbkWwise.Project' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKCU:\Software\HBK Wwise' -Recurse -Force -ErrorAction SilentlyContinue
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\HBK Wwise'
Remove-Item -LiteralPath $startMenu -Recurse -Force -ErrorAction SilentlyContinue

$files = Get-Content -LiteralPath $manifest | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
foreach ($relative in $files) {
    $path = [System.IO.Path]::GetFullPath((Join-Path $target $relative))
    if (-not $path.StartsWith($target + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe manifest entry: $relative"
    }
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath $manifest -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $target -Directory -Recurse -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    ForEach-Object {
        if ((Get-ChildItem -LiteralPath $_.FullName -Force | Measure-Object).Count -eq 0) {
            Remove-Item -LiteralPath $_.FullName -Force
        }
    }
if (Test-Path -LiteralPath $target) {
    $root = [System.IO.Path]::GetPathRoot($target)
    if ([string]::Equals($target.TrimEnd('\'), $root.TrimEnd('\'),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe install path: $target"
    }
    Set-Location -LiteralPath ([System.IO.Path]::GetTempPath())
    Remove-Item -LiteralPath $target -Recurse -Force
}

$userData = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'HbkWwise'))
$expectedParent = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'
if (-not $userData.StartsWith($expectedParent, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe user-data path: $userData"
}
Remove-Item -LiteralPath $userData -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'HBK Wwise uninstalled.'
