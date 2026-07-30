// 验证 Linux Dedicated Server 启动命令、允许路径、参数转义、进程观察和安全终止语义。
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Tests;

public sealed class LinuxProcessLauncherTests
{
    [Fact]
    [Trait("Category", "ProcessIntegration")]
    public async Task LaunchAsync_OnLinux_PutsConfiguredMapBeforeManagedArgumentsAndSkipsBlanks()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var directory = Path.Combine(Path.GetTempPath(), $"guiyang-arguments-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var argumentsPath = Path.Combine(directory, "arguments.txt");
        var scriptPath = Path.Combine(directory, "server.sh");
        await File.WriteAllTextAsync(scriptPath, $$"""
            #!/bin/sh
            printf '%s\n' "$@" > "{{argumentsPath}}"
            trap 'exit 0' TERM
            while :; do sleep 1; done
            """, timeout.Token);

        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new AllocatorOptions
            {
                AdvertisedIp = "127.0.0.1",
                GameServerExecutablePath = "/bin/sh",
                GameServerWorkingDirectory = directory,
                GameServerPrefixArguments =
                [
                    scriptPath,
                    "   ",
                    " /Game/Maps/MahjongRoomMap?game=/Script/GuiyangMahjongServer.GuiyangMahjongGameMode "
                ],
                MatchResultOutboxDirectory = Path.Combine(directory, "outbox"),
                StateFilePath = Path.Combine(directory, "state", "instances.json"),
                LobbyInternalUrl = "http://127.0.0.1:18080",
                ServiceToken = "linux-process-service-token-long-enough",
                LobbyCallbackToken = "linux-process-callback-token-long-enough",
                JoinTicketSigningKey = "linux-process-join-ticket-key-long-enough"
            });
            var launcher = new GameServerProcessLauncher(
                options, NullLogger<GameServerProcessLauncher>.Instance);
            var process = await launcher.LaunchAsync(new GameServerLaunchSpec(
                RoomId: "argument-room",
                MatchId: Guid.NewGuid().ToString(),
                ServerInstanceId: Guid.NewGuid().ToString(),
                Port: 19000,
                LobbyInternalUrl: "http://127.0.0.1:18080",
                RegistrationCredential: "registration-credential",
                JoinTicketSigningKey: "join-ticket-signing-key",
                BuildVersion: "linux-argument-test",
                AdvertisedIp: "127.0.0.1",
                MatchResultOutboxPath: Path.Combine(directory, "outbox", "result.json")), timeout.Token);

            while (!File.Exists(argumentsPath)) await Task.Delay(50, timeout.Token);
            var arguments = await File.ReadAllLinesAsync(argumentsPath, timeout.Token);

            Assert.Equal(
                "/Game/Maps/MahjongRoomMap?game=/Script/GuiyangMahjongServer.GuiyangMahjongGameMode?listen",
                arguments[0]);
            Assert.Equal("-MahjongManagedGameServer", arguments[1]);
            Assert.DoesNotContain(string.Empty, arguments);

            await process.StopAsync(TimeSpan.FromSeconds(5), timeout.Token);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "ProcessIntegration")]
    public async Task StopAsync_OnLinux_SendsSigTermBeforeForcedKill()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var directory = Path.Combine(Path.GetTempPath(), $"guiyang-signal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var markerPath = Path.Combine(directory, "sigterm-received");
        var scriptPath = Path.Combine(directory, "server.sh");
        await File.WriteAllTextAsync(scriptPath, $$"""
            #!/bin/sh
            trap 'touch "{{markerPath}}"; exit 0' TERM
            while :; do sleep 1; done
            """, timeout.Token);

        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new AllocatorOptions
            {
                AdvertisedIp = "127.0.0.1",
                GameServerExecutablePath = "/bin/sh",
                GameServerWorkingDirectory = directory,
                GameServerPrefixArguments = [scriptPath],
                MatchResultOutboxDirectory = Path.Combine(directory, "outbox"),
                StateFilePath = Path.Combine(directory, "state", "instances.json"),
                LobbyInternalUrl = "http://127.0.0.1:18080",
                ServiceToken = "linux-process-service-token-long-enough",
                LobbyCallbackToken = "linux-process-callback-token-long-enough",
                JoinTicketSigningKey = "linux-process-join-ticket-key-long-enough"
            });
            var launcher = new GameServerProcessLauncher(
                options, NullLogger<GameServerProcessLauncher>.Instance);
            var process = await launcher.LaunchAsync(new GameServerLaunchSpec(
                RoomId: "signal-room",
                MatchId: Guid.NewGuid().ToString(),
                ServerInstanceId: Guid.NewGuid().ToString(),
                Port: 19000,
                LobbyInternalUrl: "http://127.0.0.1:18080",
                RegistrationCredential: "registration-credential",
                JoinTicketSigningKey: "join-ticket-signing-key",
                BuildVersion: "linux-signal-test",
                AdvertisedIp: "127.0.0.1",
                MatchResultOutboxPath: Path.Combine(directory, "outbox", "result.json")), timeout.Token);

            await Task.Delay(250, timeout.Token);
            await process.StopAsync(TimeSpan.FromSeconds(5), timeout.Token);

            Assert.True(File.Exists(markerPath), "The child process did not observe SIGTERM.");
            Assert.True(process.HasExited);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
