$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage = Join-Path $appRoot ('.test-tmp/workflows-' + [Guid]::NewGuid().ToString('N'))
$passed = $false
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $output = Join-Path $stage 'WorkflowTests.exe'
    $json = Join-Path $appRoot 'native/lib/Newtonsoft.Json.dll'
    $compileArgs = @('/nologo','/target:exe','/optimize+',('/out:'+$output),'/reference:System.Net.Http.dll','/reference:System.Security.dll',('/reference:'+$json),(Join-Path $appRoot 'test/native-workflows.test.cs'))
    $compileArgs += Get-ChildItem -LiteralPath (Join-Path $appRoot 'native/core') -Filter '*.cs' -File | Where-Object Name -ne 'Program.cs' | ForEach-Object FullName
    $compileArgs += Join-Path $appRoot 'test/native-reliability.test.cs'
    $compileArgs += Join-Path $appRoot 'test/native-improvements.test.cs'
    & 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' @compileArgs
    if ($LASTEXITCODE -ne 0) { throw 'Workflow test compilation failed.' }
    Copy-Item -LiteralPath $json -Destination (Join-Path $stage 'Newtonsoft.Json.dll')
    & $output (Join-Path $stage 'data') $appRoot
    if ($LASTEXITCODE -ne 0) { throw 'Workflow regressions failed.' }
    $passed = $true
} finally {
    $resolved = [IO.Path]::GetFullPath($stage)
    $boundary = [IO.Path]::GetFullPath((Join-Path $appRoot '.test-tmp')).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup path.' }
    if ($passed -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    $parent = Join-Path $appRoot '.test-tmp'
    if ((Test-Path -LiteralPath $parent) -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0) { Remove-Item -LiteralPath $parent -Force }
}
