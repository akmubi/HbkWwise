#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\HBK Wwise')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$source = [System.IO.Path]::GetFullPath($PSScriptRoot)
$target = [System.IO.Path]::GetFullPath($InstallDirectory)
if ($source.Equals($target, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The extracted portable folder is already the requested install directory.'
}
if ($target.Length -lt 12 -or [System.IO.Path]::GetPathRoot($target).Equals($target,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe installation directory: $target"
}

New-Item -ItemType Directory -Path $target -Force | Out-Null
$packageFiles = Get-ChildItem -LiteralPath $source -File -Recurse
foreach ($file in $packageFiles) {
    $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/')
    $destination = Join-Path $target $relative
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
$packageFiles | ForEach-Object {
    $_.FullName.Substring($source.Length).TrimStart('\', '/').Replace('\', '/')
} | Set-Content -LiteralPath (Join-Path $target 'install-manifest.txt') -Encoding UTF8

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\HBK Wwise'
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $startMenu 'HBK Wwise.lnk'))
$shortcut.TargetPath = Join-Path $target 'HbkWwise.exe'
$shortcut.WorkingDirectory = $target
$shortcut.Save()
$uninstall = $shell.CreateShortcut((Join-Path $startMenu 'Uninstall HBK Wwise.lnk'))
$uninstall.TargetPath = 'powershell.exe'
$uninstall.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $target 'Uninstall-HbkWwise.ps1')`""
$uninstall.WorkingDirectory = $target
$uninstall.Save()

$applicationKey = 'HKCU:\Software\Classes\Applications\HbkWwise.exe'
New-Item -Path "$applicationKey\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$applicationKey\shell\open\command" -Name '(default)' -Value "`"$(Join-Path $target 'HbkWwise.exe')`" `"%1`""
New-Item -Path 'HKCU:\Software\Classes\.hbkproj' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\Classes\.hbkproj' -Name '(default)' -Value 'HbkWwise.Project'
New-Item -Path 'HKCU:\Software\Classes\HbkWwise.Project\shell\open\command' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\Classes\HbkWwise.Project' -Name '(default)' -Value 'HBK Wwise project'
Set-ItemProperty -Path 'HKCU:\Software\Classes\HbkWwise.Project\shell\open\command' -Name '(default)' -Value "`"$(Join-Path $target 'HbkWwise.exe')`" `"%1`""
New-Item -Path 'HKCU:\Software\HBK Wwise' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\HBK Wwise' -Name InstallLocation -Value $target

Write-Host "HBK Wwise installed in $target"
Write-Host 'Open Edit -> Preferences on first launch to confirm Wwise, Python, and the game paths.'
