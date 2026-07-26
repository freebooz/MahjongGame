using UnrealBuildTool;

/// <summary>配置只在编辑器中加载的资源生成、修复和自动化测试工具。</summary>
public class GuiyangMahjongEditorTools : ModuleRules
{
    public GuiyangMahjongEditorTools(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PublicDependencyModuleNames.AddRange(new[]
        {
            "Core", "CoreUObject", "Engine", "GuiyangMahjongCore", "GuiyangMahjongOnline",
            "GuiyangMahjong", "GuiyangMahjongClient", "GuiyangMahjongServer"
        });
        PrivateDependencyModuleNames.AddRange(new[]
        {
            "UMG", "Slate", "SlateCore", "UnrealEd", "UMGEditor", "AssetRegistry", "Kismet", "Json",
            "CinematicCamera", "ContentBrowser", "StaticMeshEditor"
        });
    }
}
