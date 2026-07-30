Set-StrictMode -Version Latest

<#
.SYNOPSIS
解析贵阳麻将仓库根目录。
.DESCRIPTION
显式路径优先；未提供时从本模块所在的 Scripts/lib 目录向上推导。函数会校验
`.uproject` 与 `Services`，防止后续构建或清理命令落到相邻仓库。
.PARAMETER ExplicitRoot
调用方显式传入的仓库根目录；允许为空。
.OUTPUTS
经过规范化且已验证的绝对路径。
#>
function Resolve-MahjongProjectRoot {
    [CmdletBinding()]
    param([string]$ExplicitRoot = '')

    $candidate = if ([string]::IsNullOrWhiteSpace($ExplicitRoot)) {
        Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    } else {
        $ExplicitRoot
    }
    $resolved = (Resolve-Path -LiteralPath $candidate -ErrorAction Stop).Path
    $projectFile = Join-Path $resolved 'GuiyangMahjong.uproject'
    $services = Join-Path $resolved 'Services'
    if (!(Test-Path -LiteralPath $projectFile -PathType Leaf) -or
        !(Test-Path -LiteralPath $services -PathType Container)) {
        throw "路径不是有效的贵阳麻将仓库根目录：$resolved"
    }
    return $resolved
}

<#
.SYNOPSIS
解析并验证 Unreal Engine 根目录。
.DESCRIPTION
优先使用显式参数，其次读取进程/用户/机器级 `UE_ROOT`。不再提供开发者盘符默认值，
因为静默选错引擎版本会产生难以诊断的二进制和资产差异。
.PARAMETER ExplicitRoot
调用方显式传入的引擎目录；允许为空。
.OUTPUTS
包含 RunUAT.bat 的 Unreal Engine 绝对根路径。
#>
function Resolve-UnrealEngineRoot {
    [CmdletBinding()]
    param([string]$ExplicitRoot = '')

    $candidate = $ExplicitRoot
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = $env:UE_ROOT
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [Environment]::GetEnvironmentVariable('UE_ROOT', 'User')
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [Environment]::GetEnvironmentVariable('UE_ROOT', 'Machine')
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw '未提供 Unreal Engine 根目录。请传入 -EngineRoot 或设置 UE_ROOT。'
    }

    $resolved = (Resolve-Path -LiteralPath $candidate -ErrorAction Stop).Path
    $runUat = Join-Path $resolved 'Engine\Build\BatchFiles\RunUAT.bat'
    if (!(Test-Path -LiteralPath $runUat -PathType Leaf)) {
        throw "Unreal Engine 根目录缺少 RunUAT.bat：$resolved"
    }
    return $resolved
}

<#
.SYNOPSIS
返回已验证的 `.uproject` 路径。
.DESCRIPTION
允许集成测试覆盖项目文件，但要求最终文件存在，避免 Unreal 命令把普通参数误解释成工程路径。
#>
function Resolve-MahjongProjectFile {
    [CmdletBinding()]
    param(
        [string]$ExplicitPath = '',
        [string]$ProjectRoot = ''
    )

    $root = Resolve-MahjongProjectRoot -ExplicitRoot $ProjectRoot
    $candidate = if ([string]::IsNullOrWhiteSpace($ExplicitPath)) {
        Join-Path $root 'GuiyangMahjong.uproject'
    } else {
        $ExplicitPath
    }
    return (Resolve-Path -LiteralPath $candidate -ErrorAction Stop).Path
}

Export-ModuleMember -Function Resolve-MahjongProjectRoot, Resolve-UnrealEngineRoot, Resolve-MahjongProjectFile
