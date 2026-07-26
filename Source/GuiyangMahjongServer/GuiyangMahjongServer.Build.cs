using UnrealBuildTool;

/// <summary>配置独立服务器房间、调度和控制面通信模块。</summary>
public class GuiyangMahjongServer : ModuleRules
{
    public GuiyangMahjongServer(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[]
        {
            "Core", "CoreUObject", "Engine", "GuiyangMahjongCore", "GuiyangMahjong",
            "Networking", "Sockets", "NetCore", "HTTP", "Json", "JsonUtilities", "Agones"
        });
        PrivateDependencyModuleNames.AddRange(new[]
        {
            // GameNetDriver resolves /Script/OnlineSubsystemUtils.IpNetDriver at runtime.
            // Keep this in the dedicated-server module so the client stays free of
            // server-only lifecycle code while the server can actually open its port.
            "OnlineSubsystemUtils",

            // The target-neutral MahjongRoomMap contains NavigationSystemModuleConfig. Without this
            // runtime module the cooked map has an unresolved class dependency.
            "NavigationSystem"
        });
        AddEngineThirdPartyPrivateStaticDependencies(Target, "OpenSSL");
    }
}
