using UnrealBuildTool;

/// <summary>定义虚幻编辑器目标，并加载项目专用编辑器工具模块。</summary>
public class GuiyangMahjongEditorTarget : TargetRules
{
    public GuiyangMahjongEditorTarget(TargetInfo Target) : base(Target)
    {
        Type = TargetType.Editor;
        DefaultBuildSettings = BuildSettingsVersion.V7;
        IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
        ExtraModuleNames.AddRange([
            "GuiyangMahjong", "GuiyangMahjongOnline", "GuiyangMahjongClient",
            "GuiyangMahjongServer", "GuiyangMahjongEditorTools"
        ]);
        DisablePlugins.AddRange(["Landmass", "Water", "Volumetrics"]);
    }
}
