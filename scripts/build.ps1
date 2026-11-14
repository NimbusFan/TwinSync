$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'update-readme-themes.ps1')
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dist = Join-Path $root 'dist'
$workRoot = Join-Path $root '.build'
$work = Join-Path $workRoot ([Guid]::NewGuid().ToString('N'))
$app = Join-Path $work 'app'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$json = Join-Path $root 'native/lib/Newtonsoft.Json.dll'
$icon = Join-Path $root 'native/assets/TwinSync.ico'
$manifest = Join-Path $root 'native/setup/app.manifest'
$setupSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'native/setup') -Filter '*.cs' -File | ForEach-Object FullName)
$setupSources += Join-Path $root 'native/core/StartupRegistration.cs'
$setupReferences = @('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
New-Item -ItemType Directory -Force -Path $app,$dist | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-Archive([string]$file, [hashtable]$expected) {
    $archive = [IO.Compression.ZipFile]::OpenRead($file)
    try {
        if ($archive.Entries.Count -ne $expected.Count) { throw 'Archive file count mismatch.' }
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.Replace('\', '/')
            if (-not $expected.ContainsKey($relative)) { throw ('Unexpected archive file: ' + $relative) }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '')
                if ($actual -ne (Get-FileHash -LiteralPath $expected[$relative]).Hash) { throw ('Archive content mismatch: ' + $relative) }
            } finally { $stream.Dispose(); $sha.Dispose() }
        }
    } finally { $archive.Dispose() }
}

try {
    $binary = Join-Path $app 'TwinSync.exe'
    $compileArgs = @('/nologo','/target:winexe','/optimize+','/platform:anycpu',('/out:'+$binary),('/win32icon:'+$icon),('/win32manifest:'+(Join-Path $root 'native/core/app.manifest')),'/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Net.Http.dll','/reference:System.Security.dll',('/reference:'+$json))
    $compileArgs += Get-ChildItem -LiteralPath (Join-Path $root 'native/core') -Filter '*.cs' -File | ForEach-Object FullName
    & $compiler @compileArgs
    if ($LASTEXITCODE -ne 0) { throw 'Native application build failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'native/core/app.config') -Destination (Join-Path $app 'TwinSync.exe.config')
    Copy-Item -LiteralPath $json -Destination (Join-Path $app 'Newtonsoft.Json.dll')
    Copy-Item -LiteralPath $icon -Destination (Join-Path $app 'TwinSync.ico')
    Copy-Item -LiteralPath (Join-Path $root 'public') -Destination (Join-Path $app 'public') -Recurse
    foreach ($name in @('LICENSE','THIRD_PARTY_NOTICES.md','CONTRIBUTING.md','SECURITY.md')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $app $name) }
    Copy-Item -LiteralPath (Join-Path $root 'docs/usage.md') -Destination (Join-Path $app 'README.md')
    New-Item -ItemType Directory -Path (Join-Path $app 'docs') | Out-Null
    foreach ($name in @('security.md','performance.md','publishing.md','release-notes.md','usage.md')) { Copy-Item -LiteralPath (Join-Path $root ('docs/'+$name)) -Destination (Join-Path $app ('docs/'+$name)) }
    Copy-Item -LiteralPath (Join-Path $root 'native/lib/LICENSE.Newtonsoft.Json.md') -Destination (Join-Path $app 'LICENSE.Newtonsoft.Json.md')
    $uninstallArgs = @('/nologo','/target:winexe','/optimize+','/platform:anycpu','/define:UNINSTALL',('/out:'+(Join-Path $app 'TwinSyncUninstall.exe')),('/win32icon:'+$icon),('/win32manifest:'+$manifest)) + $setupReferences + $setupSources
    & $compiler @uninstallArgs
    if ($LASTEXITCODE -ne 0) { throw 'Uninstaller build failed.' }

    # The application payload is internal to the installer; it is never a separate delivery.
    $payload = Join-Path $work 'payload.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($app,$payload,[IO.Compression.CompressionLevel]::Optimal,$false)
    $payloadFiles = @{}
    foreach ($file in Get-ChildItem -LiteralPath $app -Recurse -File) { $payloadFiles[$file.FullName.Substring($app.Length+1).Replace('\','/')] = $file.FullName }
    Assert-Archive $payload $payloadFiles
    $setup = Join-Path $work 'TwinSyncSetup.exe'
    $setupArgs = @('/nologo','/target:winexe','/optimize+','/platform:anycpu',('/out:'+$setup),('/win32icon:'+$icon),('/win32manifest:'+$manifest),('/resource:'+$payload+',TwinSyncPayload')) + $setupReferences + $setupSources
    & $compiler @setupArgs
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($setup))
    $resource = $assembly.GetManifestResourceStream('TwinSyncPayload')
    $sha = [Security.Cryptography.SHA256]::Create()
    try { if ([BitConverter]::ToString($sha.ComputeHash($resource)).Replace('-','') -ne (Get-FileHash -LiteralPath $payload).Hash) { throw 'Installer payload mismatch.' } }
    finally { $resource.Dispose(); $sha.Dispose() }

    # Source is an explicit allow-list; local configuration, caches and build outputs cannot enter it.
    $sourceFiles = @{}
    foreach ($name in @('README.md','README.light.md','LICENSE','.gitignore','.gitattributes','THIRD_PARTY_NOTICES.md','CONTRIBUTING.md','SECURITY.md','docs/security.md','docs/performance.md','docs/publishing.md','docs/release-notes.md','docs/usage.md','native/assets/TwinSync.ico','native/lib/Newtonsoft.Json.dll','native/lib/LICENSE.Newtonsoft.Json.md','native/setup/app.manifest','native/core/app.manifest','native/core/app.config')) { $sourceFiles[$name] = Join-Path $root $name }
    foreach ($directory in @('native/core','native/setup','test')) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root $directory) -Filter '*.cs' -File) { $sourceFiles[$directory+'/'+$file.Name] = $file.FullName }
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.ps1' -File) { $sourceFiles['scripts/'+$file.Name] = $file.FullName }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'public') -Recurse -File) { $sourceFiles[$file.FullName.Substring($root.Length+1).Replace('\','/')] = $file.FullName }
    # README gallery assets belong to the source repository, outside the runtime payload.
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'docs/images') -Recurse -File) { $sourceFiles[$file.FullName.Substring($root.Length+1).Replace('\','/')] = $file.FullName }
    $sourceZip = Join-Path $work 'TwinSync-source.zip'
    $archive = [IO.Compression.ZipFile]::Open($sourceZip,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in ($sourceFiles.Keys | Sort-Object)) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$sourceFiles[$relative],$relative,[IO.Compression.CompressionLevel]::Optimal) | Out-Null }
    } finally { $archive.Dispose() }
    Assert-Archive $sourceZip $sourceFiles
    Copy-Item -LiteralPath $setup -Destination (Join-Path $dist 'TwinSyncSetup.exe') -Force
    Copy-Item -LiteralPath $sourceZip -Destination (Join-Path $dist 'TwinSync-source.zip') -Force
    Write-Output 'Built and verified: dist/TwinSyncSetup.exe and dist/TwinSync-source.zip'
} finally {
    $resolved = [IO.Path]::GetFullPath($work)
    $boundary = [IO.Path]::GetFullPath($workRoot).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to clean outside the build workspace.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    if ((Test-Path -LiteralPath $workRoot) -and @(Get-ChildItem -LiteralPath $workRoot -Force).Count -eq 0) { Remove-Item -LiteralPath $workRoot -Force }
}
