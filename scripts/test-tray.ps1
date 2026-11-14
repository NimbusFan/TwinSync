param([switch]$KeepPreview)
$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage = Join-Path $appRoot ('.test-tmp/tray-' + [Guid]::NewGuid().ToString('N'))
$passed = $false
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $output = Join-Path $stage 'TrayTests.exe'
    $json = Join-Path $appRoot 'native/lib/Newtonsoft.Json.dll'
    $compileArgs = @('/nologo','/target:exe','/optimize+','/main:TwinSync.Native.TrayTests',('/out:'+$output),('/win32manifest:'+(Join-Path $appRoot 'native/core/app.manifest')),'/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Net.Http.dll','/reference:System.Security.dll',('/reference:'+$json),(Join-Path $appRoot 'test/native-tray.test.cs'))
    $compileArgs += Get-ChildItem -LiteralPath (Join-Path $appRoot 'native/core') -Filter '*.cs' -File | ForEach-Object FullName
    & 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' @compileArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tray tests failed to compile.' }
    Copy-Item -LiteralPath $json -Destination (Join-Path $stage 'Newtonsoft.Json.dll')
    Copy-Item -LiteralPath (Join-Path $appRoot 'native/core/app.config') -Destination ($output + '.config')
    $testArgs = @()
    if ($KeepPreview) { $testArgs += Join-Path $stage 'tray-menu.png' }
    & $output @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tray DPI tests failed.' }
    $passed = $true
    if ($KeepPreview) { Write-Output ('Preview: ' + (Join-Path $stage 'tray-menu.png')) }
} finally {
    $boundary = [IO.Path]::GetFullPath((Join-Path $appRoot '.test-tmp')).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($stage)
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup path.' }
    if ($passed -and -not $KeepPreview -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    $parent = Join-Path $appRoot '.test-tmp'
    if ((Test-Path -LiteralPath $parent) -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0) { Remove-Item -LiteralPath $parent -Force }
}
