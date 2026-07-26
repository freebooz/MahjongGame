[CmdletBinding()]
param(
    [string]$Distribution = 'Ubuntu',
    [string]$WslUser = 'root',
    [string]$RepositoryPath = '/home/administrator/src/MahjongGame',
    [int]$DockerTimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
$wsl = "$env:SystemRoot\System32\wsl.exe"
$logDirectory = Join-Path $env:LOCALAPPDATA 'GuiyangMahjong'
$logPath = Join-Path $logDirectory 'wsl-startup.log'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

function Write-StartupLog([string]$Message) {
    $line = '{0:o} {1}' -f [DateTimeOffset]::Now, $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding utf8
}

try {
    $keepAlive = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -eq 'wsl.exe' -and
            $_.CommandLine -match [regex]::Escape($Distribution) -and
            $_.CommandLine -match '/usr/bin/sleep infinity'
        } |
        Select-Object -First 1
    if ($null -eq $keepAlive) {
        Start-Process -FilePath $wsl -ArgumentList @(
            '-d', $Distribution, '--exec', '/usr/bin/sleep', 'infinity'
        ) -WindowStyle Hidden
        Write-StartupLog "Started WSL keepalive for $Distribution."
    }

    $deadline = [DateTimeOffset]::Now.AddSeconds($DockerTimeoutSeconds)
    do {
        & $wsl -d $Distribution -u $WslUser -- bash -lc 'docker info >/dev/null 2>&1'
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::Now -lt $deadline)
    if ($LASTEXITCODE -ne 0) {
        throw "Docker did not become ready within $DockerTimeoutSeconds seconds."
    }

    $statusCommand = "cd '$RepositoryPath' && ./Deploy/linux/deploy.sh status"
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & $wsl -d $Distribution -u $WslUser -- bash -lc $statusCommand 2>&1
    $statusExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    $output | ForEach-Object {
        $line = "$_"
        if (!$line.Contains([char]0) -and $line -notmatch 'WSL.*NAT' -and
            $line -notmatch 'localhost') {
            Write-StartupLog $line
        }
    }
    if ($statusExitCode -ne 0) {
        throw "Linux stack status failed with exit code $statusExitCode."
    }
    Write-StartupLog 'WSL Linux server stack is ready.'
} catch {
    Write-StartupLog "ERROR: $($_.Exception.Message)"
    throw
}
