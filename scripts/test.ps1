$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-native.ps1')
& (Join-Path $PSScriptRoot 'test-security.ps1')
& (Join-Path $PSScriptRoot 'test-setup.ps1')
& (Join-Path $PSScriptRoot 'test-tray.ps1')
& (Join-Path $PSScriptRoot 'test-workflows.ps1')
Write-Output 'All native regressions passed.'
