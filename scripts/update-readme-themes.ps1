$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = [IO.File]::ReadAllText((Join-Path $project 'README.md'))
$images = [ordered]@{
    'overview-dark.png' = 'overview-light.png'
    'files.png' = 'files-light.png'
    'space.png' = 'space-light.png'
    'devices.png' = 'devices-light.png'
}
if (-not $source.Contains('href="README.light.md"')) { throw 'The dark README must link to its light counterpart.' }
$light = $source
foreach ($name in $images.Keys) {
    $darkPath = 'docs/images/' + $name
    $lightPath = 'docs/images/' + $images[$name]
    if (-not $source.Contains($darkPath)) { throw ('Missing dark screenshot reference: ' + $name) }
    if (-not (Test-Path -LiteralPath (Join-Path $project $lightPath))) { throw ('Missing light screenshot: ' + $lightPath) }
    $light = $light.Replace($darkPath, $lightPath)
}
$light = $light.Replace('深色', '浅色')
$light = $light.Replace('href="README.light.md"', 'href="README.md"').Replace('docs/images/theme-light.svg', 'docs/images/theme-dark.svg')
$light = $light.Replace('alt="查看浅色版本"', 'alt="查看深色版本"')
$target = Join-Path $project 'README.light.md'
if (-not (Test-Path -LiteralPath $target) -or [IO.File]::ReadAllText($target) -cne $light) {
    [IO.File]::WriteAllText($target, $light, (New-Object Text.UTF8Encoding($false)))
}
