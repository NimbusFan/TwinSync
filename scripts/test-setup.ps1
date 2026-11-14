param([switch]$KeepPreviews, [string]$InstallerPackage = '')
$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage = Join-Path $appRoot ('.test-tmp/setup-' + [Guid]::NewGuid().ToString('N'))
$passed = $false
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fixture = Join-Path $stage 'payload'
    New-Item -ItemType Directory -Path (Join-Path $fixture 'public') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fixture 'TwinSync.exe') -Value 'Test fixture, never executed.'
    Set-Content -LiteralPath (Join-Path $fixture 'public/index.html') -Value '<html>Test fixture</html>'
    $payload = Join-Path $stage 'payload.zip'
    if ($InstallerPackage) {
        $package = (Resolve-Path -LiteralPath $InstallerPackage).Path
        $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($package))
        $input = $assembly.GetManifestResourceStream('TwinSyncPayload')
        if ($null -eq $input) { throw 'Installer payload was not found.' }
        $target = [IO.File]::Create($payload)
        try { $input.CopyTo($target) } finally { $input.Dispose(); $target.Dispose() }
    } else { [IO.Compression.ZipFile]::CreateFromDirectory($fixture, $payload) }
    $output = Join-Path $stage 'InstallerTests.exe'
    $compileArgs = @('/nologo','/target:exe','/optimize+','/main:TwinSync.Setup.InstallerTests',('/out:'+$output),('/win32manifest:'+(Join-Path $appRoot 'native/setup/app.manifest')),('/win32icon:'+(Join-Path $appRoot 'native/assets/TwinSync.ico')),('/resource:'+$payload+',TwinSyncPayload'),'/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll',(Join-Path $appRoot 'test/setup.test.cs'))
    $compileArgs += Get-ChildItem -LiteralPath (Join-Path $appRoot 'native/setup') -Filter '*.cs' -File | ForEach-Object FullName
    $compileArgs += Join-Path $appRoot 'native/core/StartupRegistration.cs'
    & 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' @compileArgs
    if ($LASTEXITCODE -ne 0) { throw 'Installer tests failed to compile.' }
    $testArgs = @((Join-Path $stage 'data'))
    if ($KeepPreviews) { $testArgs += Join-Path $stage 'previews' }
    & $output @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Installer tests failed.' }
    $passed = $true
    if ($KeepPreviews) { Write-Output ('Previews: ' + (Join-Path $stage 'previews')) }
} finally {
    $boundary = [IO.Path]::GetFullPath((Join-Path $appRoot '.test-tmp')).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($stage)
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup path.' }
    if ($passed -and -not $KeepPreviews -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    $parent = Join-Path $appRoot '.test-tmp'
    if ((Test-Path -LiteralPath $parent) -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0) { Remove-Item -LiteralPath $parent -Force }
}
