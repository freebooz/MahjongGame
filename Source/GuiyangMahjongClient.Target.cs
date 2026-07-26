using UnrealBuildTool;

/// <summary>定义仅包含客户端运行模块的打包目标。</summary>
public class GuiyangMahjongClientTarget : TargetRules
{
    public GuiyangMahjongClientTarget(TargetInfo Target) : base(Target)
    {
        Type = TargetType.Client;
        DefaultBuildSettings = BuildSettingsVersion.V7;
        IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
        ExtraModuleNames.AddRange(["GuiyangMahjong", "GuiyangMahjongOnline", "GuiyangMahjongClient"]);
        DisablePlugins.AddRange(["Agones", "Landmass", "Water", "Volumetrics", "NNERuntimeORT", "NNEDenoiser"]);
    }
}
