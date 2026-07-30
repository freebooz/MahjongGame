<#
配置 Windows/WSL 开发网络所需的最小防火墙规则，支持 ShouldProcess 预览。
只允许项目声明的端口范围；修改系统规则需要管理员权限，脚本不得关闭整个防火墙。
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$RemoteAddresses = 'LocalSubnet',
    [string]$WslVmCreatorId = '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}'
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}
if (!(Get-Command New-NetFirewallHyperVRule -ErrorAction SilentlyContinue)) {
    throw 'Hyper-V firewall cmdlets are unavailable; update Windows/WSL before using mirrored networking.'
}

$rules = @(
    @{ Name = 'GuiyangMahjong-Auth'; Protocol = 'TCP'; Ports = '18082' },
    @{ Name = 'GuiyangMahjong-Lobby'; Protocol = 'TCP'; Ports = '18080' },
    @{ Name = 'GuiyangMahjong-GameServer'; Protocol = 'UDP'; Ports = '19000-19099' }
)

foreach ($rule in $rules) {
    $parameters = @{
        PolicyStore = 'PersistentStore'
        Name = $rule.Name
        DisplayName = $rule.Name
        Direction = 'Inbound'
        VMCreatorId = $WslVmCreatorId
        Protocol = $rule.Protocol
        LocalPorts = $rule.Ports
        RemoteAddresses = $RemoteAddresses
        Action = 'Allow'
        Enabled = $true
    }
    $existing = Get-NetFirewallHyperVRule -PolicyStore PersistentStore -Name $rule.Name -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        if ($PSCmdlet.ShouldProcess($rule.Name, 'Create WSL Hyper-V firewall rule')) {
            New-NetFirewallHyperVRule @parameters | Out-Null
        }
    } elseif ($PSCmdlet.ShouldProcess($rule.Name, 'Update WSL Hyper-V firewall rule')) {
        Set-NetFirewallHyperVRule @parameters | Out-Null
    }
}

Get-NetFirewallHyperVRule -PolicyStore ActiveStore |
    Where-Object Name -In $rules.Name |
    Select-Object Name, Direction, Action, Protocol, LocalPorts, RemoteAddresses, Enabled

Write-Host 'WSL_FIREWALL_OK allocatorPort=internal-only'
