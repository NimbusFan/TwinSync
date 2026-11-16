$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage = Join-Path $appRoot ('.test-tmp/web-' + [Guid]::NewGuid().ToString('N'))
$process = $null
$passed = $false
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $output = Join-Path $stage 'WebFixture.exe'
    $json = Join-Path $appRoot 'native/lib/Newtonsoft.Json.dll'
    $compileArgs = @('/nologo','/target:exe','/optimize+',('/out:'+$output),'/reference:System.Net.Http.dll','/reference:System.Security.dll',('/reference:'+$json),(Join-Path $appRoot 'test/native-web-fixture.cs'))
    $compileArgs += Get-ChildItem -LiteralPath (Join-Path $appRoot 'native/core') -Filter '*.cs' -File | Where-Object Name -ne 'Program.cs' | ForEach-Object FullName
    & 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' @compileArgs
    if ($LASTEXITCODE -ne 0) { throw 'Web fixture compilation failed.' }
    Copy-Item -LiteralPath $json -Destination (Join-Path $stage 'Newtonsoft.Json.dll')
    $process = Start-Process -FilePath $output -ArgumentList @(('"'+$appRoot+'"'),('"'+$stage+'"')) -PassThru -WindowStyle Hidden
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $stage 'ready.json'))) {
        if ($process.HasExited -or $clock.ElapsedMilliseconds -gt 15000) { throw 'Web fixture did not start.' }
        Start-Sleep -Milliseconds 100
    }
    & node (Join-Path $appRoot 'test/web-contract.test.cjs') $stage
    if ($LASTEXITCODE -ne 0) { throw 'Browser contract regressions failed.' }
    $passed = $true
} finally {
    if ($process) {
        [IO.File]::WriteAllText((Join-Path $stage 'stop'), '')
        if (-not $process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
    }
    $resolved = [IO.Path]::GetFullPath($stage)
    $boundary = [IO.Path]::GetFullPath((Join-Path $appRoot '.test-tmp')).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid browser test cleanup path.' }
    if ($passed -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
