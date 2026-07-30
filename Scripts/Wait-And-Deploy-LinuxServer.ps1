[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$AutomationToolProcessId,
    [string]$EngineRoot = '',
    [ValidateSet('Development', 'Shipping')]
    [string]$Configuration = 'Development',
    [string]$Distribution = 'Ubuntu',
    [string]$WslUser = 'root',
    [string]$LinuxRepositoryPath = '',
    [string]$Version = '',
    [int]$PollSeconds = 15
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$logDirectory = Join-Path $root 'Saved\Logs'
$logPath = Join-Path $logDirectory 'LinuxServerCompletion.log'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

function Write-CompletionLog([string]$Message) {
    Add-Content -LiteralPath $logPath -Encoding utf8 -Value ('{0:o} {1}' -f [DateTimeOffset]::Now, $Message)
}

try {
    Write-CompletionLog "Waiting for AutomationTool PID $AutomationToolProcessId."
    while (Get-Process -Id $AutomationToolProcessId -ErrorAction SilentlyContinue) {
        Start-Sleep -Seconds $PollSeconds
    }
    Write-CompletionLog 'AutomationTool exited; validating and deploying its LinuxServer archive.'
    $parameters = @{
        EngineRoot = $EngineRoot
        Configuration = $Configuration
        Distribution = $Distribution
        WslUser = $WslUser
        LinuxRepositoryPath = $LinuxRepositoryPath
        ReuseExistingBuild = $true
    }
    if (![string]::IsNullOrWhiteSpace($Version)) { $parameters.Version = $Version }
    $output = & (Join-Path $PSScriptRoot 'Deploy-LinuxServerToWsl.ps1') @parameters 2>&1
    $output | ForEach-Object { Write-CompletionLog $_ }
    if ($LASTEXITCODE -ne 0) { throw "Deployment command exited with $LASTEXITCODE." }
    Write-CompletionLog 'WAIT_AND_DEPLOY_OK'
} catch {
    Write-CompletionLog "WAIT_AND_DEPLOY_FAILED $($_.Exception.Message)"
    exit 1
}
