using UnrealBuildTool;

/// <summary>配置登录、鉴权与在线会话公共模块。</summary>
public class GuiyangMahjongOnline : ModuleRules
{
    public GuiyangMahjongOnline(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[]
        {
            "Core", "CoreUObject", "Engine", "GuiyangMahjongCore", "HTTP", "Json", "JsonUtilities"
        });
    }
}
