using UnrealBuildTool;

/// <summary>配置客户端与服务端共享的游戏框架桥接模块依赖。</summary>
public class GuiyangMahjong : ModuleRules
{
    public GuiyangMahjong(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[]
        {
            "Core", "CoreUObject", "Engine", "GuiyangMahjongCore", "Networking", "Sockets", "NetCore"
        });
    }
}
