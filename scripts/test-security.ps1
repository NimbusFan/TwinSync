$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runRoot = Join-Path $appRoot ('.test-tmp/security-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$passed = $false
try {
$output = Join-Path $runRoot 'NativeSecurityTests.exe'
$json = Join-Path $appRoot 'native/lib/Newtonsoft.Json.dll'
$compiler = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$compileArgs = @('/nologo', '/target:exe', '/optimize+', ('/out:' + $output), '/reference:System.Net.Http.dll', '/reference:System.Security.dll', ('/reference:' + $json))
$compileArgs += Get-ChildItem -LiteralPath (Join-Path $appRoot 'native/core') -Filter '*.cs' -File | Where-Object Name -ne 'Program.cs' | ForEach-Object FullName
$compileArgs += Join-Path $appRoot 'test/native-security.test.cs'
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Security test compilation failed.' }
Copy-Item -LiteralPath $json -Destination (Join-Path $runRoot 'Newtonsoft.Json.dll')
& $output $runRoot $appRoot
if ($LASTEXITCODE -ne 0) { throw 'Native security regression failed.' }
$passed = $true
} finally {
    $boundary = [IO.Path]::GetFullPath((Join-Path $appRoot '.test-tmp')).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($runRoot)
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup path.' }
    if ($passed -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    $parent = Join-Path $appRoot '.test-tmp'
    if ((Test-Path -LiteralPath $parent) -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0) { Remove-Item -LiteralPath $parent -Force }
}
