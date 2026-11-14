param([string]$SourceDirectory = '', [string]$Label = 'current')
$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $SourceDirectory) { $SourceDirectory = Join-Path $appRoot 'native/core' }
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ($Label -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid test label.' }
$stage = Join-Path $appRoot ('.test-tmp/native-index-' + $Label + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$passed = $false
try {
$jsonDll = Join-Path $appRoot 'native/lib/Newtonsoft.Json.dll'
Copy-Item -LiteralPath $jsonDll -Destination (Join-Path $stage 'Newtonsoft.Json.dll')
$output = Join-Path $stage 'IndexTests.exe'
$compileArgs = @('/nologo','/target:exe','/optimize+',('/out:'+$output),('/reference:'+$jsonDll),(Join-Path $SourceDirectory 'IndexStore.cs'),(Join-Path $SourceDirectory 'JsonUtil.cs'),(Join-Path $appRoot 'test/native-index.test.cs'))
if ($Label -ne 'baseline') { $compileArgs += '/define:STREAMING' }
& 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Native tests failed to compile.' }
& $output (Join-Path $stage 'data')
if ($LASTEXITCODE -ne 0) { throw 'Native index tests failed.' }
$passed = $true
} finally {
    $boundary = [IO.Path]::GetFullPath((Join-Path $appRoot '.test-tmp')).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($stage)
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup path.' }
    if ($passed -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    $parent = Join-Path $appRoot '.test-tmp'
    if ((Test-Path -LiteralPath $parent) -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0) { Remove-Item -LiteralPath $parent -Force }
}
