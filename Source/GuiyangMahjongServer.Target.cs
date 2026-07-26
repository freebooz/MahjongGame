using UnrealBuildTool;

/// <summary>定义无渲染独立服务器目标，严格排除客户端表现模块。</summary>
public class GuiyangMahjongServerTarget : TargetRules
{
    public GuiyangMahjongServerTarget(TargetInfo Target) : base(Target)
    {
        Type = TargetType.Server;
        DefaultBuildSettings = BuildSettingsVersion.V7;
        IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
        ExtraModuleNames.AddRange(["GuiyangMahjong", "GuiyangMahjongServer"]);
        bUsesSlate = false;
        bBuildDeveloperTools = false;
        // A headless production server still needs lifecycle and NetDriver diagnostics.
        // Without Shipping logs an allocated-but-not-listening process is indistinguishable
        // from a healthy room process to the local allocator.
        bUseLoggingInShipping = true;
        DisablePlugins.AddRange([
            "Landmass", "Water", "Volumetrics", "NNERuntimeORT", "NNEDenoiser", "MsQuic"
        ]);
    }
}
