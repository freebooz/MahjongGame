[CmdletBinding()]
param(
    [string]$EngineRoot = '',
    [string]$Root = ''
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ProjectEnvironment.psm1') -Force
$Root = Resolve-MahjongProjectRoot -ExplicitRoot $Root
$EngineRoot = Resolve-UnrealEngineRoot -ExplicitRoot $EngineRoot
$project = Join-Path $Root 'GuiyangMahjong.uproject'
$ubt = Join-Path $EngineRoot 'Engine\Binaries\DotNET\UnrealBuildTool\UnrealBuildTool.dll'
$dotnet = Join-Path $EngineRoot 'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet) -or !(Test-Path -LiteralPath $ubt)) {
    throw 'UnrealBuildTool .NET 10 runtime was not found.'
}

$outputDirectory = Join-Path $Root 'Saved\Build\TargetGraphs'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$graphs = @{}
foreach ($target in @('GuiyangMahjongClient', 'GuiyangMahjongServer')) {
    $output = Join-Path $outputDirectory "$target.json"
    & $dotnet $ubt -Mode=JsonExport $target Win64 Development $project "-OutputFile=$output"
    if ($LASTEXITCODE -ne 0) { throw "Could not export target graph for $target" }
    $graphs[$target] = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
}

$clientModules = @($graphs.GuiyangMahjongClient.Binaries.Modules)
$serverModules = @($graphs.GuiyangMahjongServer.Binaries.Modules)
$clientForbidden = @('GuiyangMahjongServer', 'Agones')
$serverForbidden = @('GuiyangMahjongClient', 'GuiyangMahjongOnline', 'NNERuntimeORT', 'NNEDenoiser', 'MsQuic')
foreach ($module in $clientForbidden) {
    if ($clientModules -contains $module) { throw "Client target contains forbidden module: $module" }
}
foreach ($module in $serverForbidden) {
    if ($serverModules -contains $module) { throw "Server target contains forbidden module: $module" }
}
foreach ($module in @('GuiyangMahjong', 'GuiyangMahjongCore', 'GuiyangMahjongClient', 'GuiyangMahjongOnline')) {
    if ($clientModules -notcontains $module) { throw "Client target is missing required module: $module" }
}
foreach ($module in @('GuiyangMahjong', 'GuiyangMahjongCore', 'GuiyangMahjongServer', 'Agones')) {
    if ($serverModules -notcontains $module) { throw "Server target is missing required module: $module" }
}

Write-Host "TARGET_MODULE_GRAPH_OK clientModules=$($clientModules.Count) serverModules=$($serverModules.Count)"
