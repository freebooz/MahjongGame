using UnrealBuildTool;

/// <summary>配置不依赖渲染的麻将规则核心模块。</summary>
public class GuiyangMahjongCore : ModuleRules
{
    public GuiyangMahjongCore(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine" });
    }
}
