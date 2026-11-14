param([switch]$Desktop)
$ErrorActionPreference = 'Stop'
if (-not $Desktop) { throw 'Pass -Desktop to open, activate and close isolated Explorer test windows.' }
$appRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage = Join-Path $appRoot ('.test-tmp/explorer-' + [Guid]::NewGuid().ToString('N'))
$passed = $false
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $output = Join-Path $stage 'ExplorerTests.exe'
    & 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' /nologo /target:exe /optimize+ ('/out:'+$output) (Join-Path $appRoot 'native/core/ExplorerIntegration.cs') (Join-Path $appRoot 'test/native-explorer.test.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Explorer integration test compilation failed.' }
    & $output (Join-Path $stage 'files')
    if ($LASTEXITCODE -ne 0) { throw 'Explorer desktop integration failed.' }
    $passed = $true
} finally {
    $boundary = [IO.Path]::GetFullPath((Join-Path $appRoot '.test-tmp')).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($stage)
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup path.' }
    if ($passed -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    $parent = Join-Path $appRoot '.test-tmp'
    if ((Test-Path -LiteralPath $parent) -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0) { Remove-Item -LiteralPath $parent -Force }
}
