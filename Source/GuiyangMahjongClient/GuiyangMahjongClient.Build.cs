using UnrealBuildTool;

/// <summary>配置客户端表现、UI、音频及在线访问模块依赖。</summary>
public class GuiyangMahjongClient : ModuleRules
{
    public GuiyangMahjongClient(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[]
        {
            "Core", "CoreUObject", "Engine", "DeveloperSettings",
            "GuiyangMahjongCore", "GuiyangMahjongOnline", "GuiyangMahjong"
        });
        PrivateDependencyModuleNames.AddRange(new[]
        {
            "InputCore", "EnhancedInput", "UMG", "Slate", "SlateCore", "ApplicationCore",
            "HTTP", "Json", "JsonUtilities", "CinematicCamera", "Networking"
        });
    }
}
