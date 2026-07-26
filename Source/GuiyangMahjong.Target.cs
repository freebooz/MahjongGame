using UnrealBuildTool;

/// <summary>定义通用游戏目标；用于编辑器之外的常规客户端构建。</summary>
public class GuiyangMahjongTarget : TargetRules
{
    public GuiyangMahjongTarget(TargetInfo Target) : base(Target)
    {
        Type = TargetType.Game;
        DefaultBuildSettings = BuildSettingsVersion.V7;
        IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
        ExtraModuleNames.AddRange(["GuiyangMahjong", "GuiyangMahjongOnline", "GuiyangMahjongClient"]);
        DisablePlugins.AddRange(["Agones", "Landmass", "Water", "Volumetrics"]);
    }
}
