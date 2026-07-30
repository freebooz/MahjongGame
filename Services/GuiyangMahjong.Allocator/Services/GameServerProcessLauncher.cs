// 本机游戏服进程启动器：构造受控命令行、启动 Dedicated Server 并观察退出码。
// 可执行文件和工作目录必须位于允许根目录；不得通过未验证输入拼接命令或把凭据写入日志。
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Services;

/// <summary>
/// 不经 Shell 解析直接启动本机 Dedicated Server，避免参数注入并防止凭据进入日志。
/// 可执行文件和工作目录必须通过允许根目录与平台可执行性检查。
/// </summary>
public sealed class GameServerProcessLauncher(
    IOptions<AllocatorOptions> options,
    ILogger<GameServerProcessLauncher> logger) : IGameServerProcessLauncher
{
    private readonly AllocatorOptions options = options.Value;

    /// <inheritdoc/>
    public Task<IManagedGameServerProcess> LaunchAsync(
        GameServerLaunchSpec spec,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executablePath = ResolveExecutablePath(options.GameServerExecutablePath);
        EnsureExecutable(executablePath);
        var workingDirectory = ResolveWorkingDirectory(executablePath);
        Directory.CreateDirectory(Path.GetDirectoryName(spec.MatchResultOutboxPath)
            ?? throw new InvalidOperationException("MatchResultOutboxPath has no parent directory."));

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsLinux() ? ResolveSetSidPath() : executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        if (OperatingSystem.IsLinux()) startInfo.ArgumentList.Add(executablePath);
        var isUnrealDedicatedServer = Path.GetFileNameWithoutExtension(executablePath)
            .StartsWith("GuiyangMahjongServer", StringComparison.OrdinalIgnoreCase);
        foreach (var argument in options.GameServerPrefixArguments)
        {
            if (string.IsNullOrWhiteSpace(argument)) continue;
            var normalizedArgument = argument.Trim();
            if (isUnrealDedicatedServer
                && normalizedArgument.StartsWith("/Game/", StringComparison.Ordinal)
                && !normalizedArgument.Contains("?listen", StringComparison.OrdinalIgnoreCase))
            {
                normalizedArgument += "?listen";
            }
            startInfo.ArgumentList.Add(normalizedArgument);
        }
        if (isUnrealDedicatedServer)
        {
            startInfo.ArgumentList.Add("-unattended");
            startInfo.ArgumentList.Add("-stdout");
            startInfo.ArgumentList.Add("-FullStdOutLogOutput");
        }
        startInfo.ArgumentList.Add("-MahjongManagedGameServer");
        startInfo.ArgumentList.Add($"-RoomId={spec.RoomId}");
        startInfo.ArgumentList.Add($"-MatchId={spec.MatchId}");
        startInfo.ArgumentList.Add($"-ServerInstanceId={spec.ServerInstanceId}");
        startInfo.ArgumentList.Add($"-Port={spec.Port}");
        startInfo.ArgumentList.Add($"-LobbyInternalUrl={spec.LobbyInternalUrl}");
        startInfo.ArgumentList.Add($"-BuildVersion={spec.BuildVersion}");
        startInfo.ArgumentList.Add($"-AdvertisedIp={spec.AdvertisedIp}");
        startInfo.Environment["MAHJONG_REGISTRATION_CREDENTIAL"] = spec.RegistrationCredential;
        startInfo.Environment["MAHJONG_JOIN_TICKET_SIGNING_KEY"] = spec.JoinTicketSigningKey;
        startInfo.Environment["MAHJONG_MATCH_RESULT_OUTBOX_PATH"] = spec.MatchResultOutboxPath;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("GameServer process failed to start.");
        logger.LogInformation(
            "GameServer started InstanceId={InstanceId} RoomId={RoomId} Port={Port} ProcessId={ProcessId}",
            spec.ServerInstanceId,
            spec.RoomId,
            spec.Port,
            process.Id);
        return Task.FromResult<IManagedGameServerProcess>(new ManagedGameServerProcess(process));
    }

    /// <inheritdoc/>
    public Task<IManagedGameServerProcess?> TryAttachAsync(
        int processId,
        DateTimeOffset expectedStartedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var process = Process.GetProcessById(processId);
            var managed = new ManagedGameServerProcess(process);
            if (managed.HasExited
                || Math.Abs((managed.StartedAtUtc - expectedStartedAtUtc).TotalSeconds) > 2)
            {
                process.Dispose();
                return Task.FromResult<IManagedGameServerProcess?>(null);
            }

            try
            {
                var configured = CanonicalizeExistingPath(ResolveExecutablePath(options.GameServerExecutablePath));
                var actual = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(actual)
                    || !string.Equals(
                        configured,
                        CanonicalizeExistingPath(actual),
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    process.Dispose();
                    return Task.FromResult<IManagedGameServerProcess?>(null);
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                               or InvalidOperationException
                                               or NotSupportedException
                                               or IOException
                                               or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    exception,
                    "Refusing to recover GameServer because its executable path could not be verified ProcessId={ProcessId}",
                    processId);
                process.Dispose();
                return Task.FromResult<IManagedGameServerProcess?>(null);
            }

            return Task.FromResult<IManagedGameServerProcess?>(managed);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult<IManagedGameServerProcess?>(null);
        }
    }

    /// <summary>返回经绝对化和允许根目录验证的服务端可执行文件路径。</summary>
    public string GetResolvedExecutablePath() => ResolveExecutablePath(options.GameServerExecutablePath);

    /// <summary>返回经验证的进程工作目录；未配置时使用可执行文件所在目录。</summary>
    public string GetResolvedWorkingDirectory() => ResolveWorkingDirectory(GetResolvedExecutablePath());

    /// <summary>按当前操作系统检查文件是否具有可执行资格；Windows 依据扩展名，Unix 依据权限位。</summary>
    public static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (!OperatingSystem.IsLinux()) return true;
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode executeBits = UnixFileMode.UserExecute
                                         | UnixFileMode.GroupExecute
                                         | UnixFileMode.OtherExecute;
        return (mode & executeBits) != 0;
    }

    private string ResolveWorkingDirectory(string executablePath)
    {
        var configured = options.GameServerWorkingDirectory.Trim();
        var path = string.IsNullOrEmpty(configured)
            ? Path.GetDirectoryName(executablePath)
            : Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(AppContext.BaseDirectory, configured);
        path = Path.GetFullPath(path
            ?? throw new InvalidOperationException("GameServer executable has no parent directory."));
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"GameServer working directory does not exist: {path}");
        return path;
    }

    private static void EnsureExecutable(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("GameServer executable does not exist.", path);
        if (!IsExecutable(path))
            throw new InvalidOperationException($"GameServer file is not executable: {path}");
    }

    private static string ResolveExecutablePath(string configured)
    {
        configured = configured.Trim();
        if (string.IsNullOrEmpty(configured))
            throw new InvalidOperationException("GameServerExecutablePath is not configured.");
        if (Path.IsPathRooted(configured)) return Path.GetFullPath(configured);
        if (configured.Contains(Path.DirectorySeparatorChar)
            || configured.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, configured);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            if (OperatingSystem.IsWindows() && Path.GetExtension(candidate).Length == 0)
            {
                foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
                {
                    if (File.Exists(candidate + extension)) return Path.GetFullPath(candidate + extension);
                }
            }
        }
        throw new FileNotFoundException($"GameServer executable was not found on PATH: {configured}");
    }

    private static string CanonicalizeExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var target = new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);
        return target is null ? fullPath : Path.GetFullPath(target.FullName);
    }

    private static string ResolveSetSidPath()
    {
        foreach (var candidate in new[] { "/usr/bin/setsid", "/bin/setsid" })
            if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException("Linux GameServer launch requires util-linux setsid.");
    }

    private sealed class ManagedGameServerProcess : IManagedGameServerProcess
    {
        private readonly Process process;

        /// <summary>接管已验证进程并冻结 PID 与 UTC 启动时间，供后续防复用检查。</summary>
        public ManagedGameServerProcess(Process process)
        {
            this.process = process;
            ProcessId = process.Id;
            StartedAtUtc = process.StartTime.ToUniversalTime();
        }

        /// <inheritdoc/>
        public int ProcessId { get; }

        /// <inheritdoc/>
        public DateTimeOffset StartedAtUtc { get; }

        /// <inheritdoc/>
        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        /// <inheritdoc/>
        public async ValueTask StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
        {
            if (HasExited)
            {
                process.Dispose();
                return;
            }
            if (OperatingSystem.IsLinux()) SendLinuxProcessGroupSignal(ProcessId, 15);
            if (gracePeriod > TimeSpan.Zero)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(gracePeriod);
                    await process.WaitForExitAsync(timeout.Token);
                    process.Dispose();
                    return;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            if (!HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
            process.Dispose();
        }
    }

    private static void SendLinuxProcessGroupSignal(int processId, int signal)
    {
        if (kill(-processId, signal) == 0) return;
        var error = Marshal.GetLastPInvokeError();
        if (error == 3) return; // ESRCH: the process already exited.
        throw new Win32Exception(error, $"Could not signal GameServer process group {processId}.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
