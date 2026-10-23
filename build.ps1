# Builds UCHGamePerformance.dll into .\build using the .NET Framework compiler that ships with Windows.
# Usage: .\build.ps1                      (default Steam path)
#        .\build.ps1 -GamePath "D:\Games\Ultimate Chicken Horse"
#        .\build.ps1 -Install              (also copies the DLL into BepInEx\plugins\UCH Game Performance)
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Ultimate Chicken Horse',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GamePath 'UltimateChickenHorse_Data\Managed'
$core = Join-Path $GamePath 'BepInEx\core'
if (-not (Test-Path (Join-Path $managed 'Assembly-CSharp.dll'))) { throw "Game not found at '$GamePath'. Pass -GamePath." }
if (-not (Test-Path (Join-Path $core 'BepInEx.dll'))) { throw "BepInEx 5 not installed in '$GamePath'." }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outDir = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force $outDir | Out-Null
$output = Join-Path $outDir 'UCHGamePerformance.dll'
$arguments = @('/nologo','/noconfig','/langversion:5','/nostdlib+','/target:library','/optimize+','/debug-','/warn:4','/warnaserror+',('/out:"'+$output+'"'))
foreach ($reference in Get-ChildItem -LiteralPath $managed -Filter '*.dll') { $arguments += ('/reference:"'+$reference.FullName+'"') }
foreach ($name in @('BepInEx.dll','0Harmony.dll')) { $arguments += ('/reference:"'+(Join-Path $core $name)+'"') }
foreach ($source in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Source') -Filter '*.cs') { $arguments += ('"'+$source.FullName+'"') }
$result = & $compiler $arguments 2>&1
if ($LASTEXITCODE -ne 0) { throw ($result -join [Environment]::NewLine) }
Write-Output "Built $output"
if ($Install) {
    $target = Join-Path $GamePath 'BepInEx\plugins\UCH Game Performance'
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item $output $target -Force
    Write-Output "Installed to $target"
}
