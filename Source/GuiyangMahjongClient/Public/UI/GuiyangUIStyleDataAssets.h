#pragma once

#include "CoreMinimal.h"
#include "Engine/DataAsset.h"
#include "Engine/Texture2D.h"
#include "Styling/SlateTypes.h"
#include "GuiyangUIStyleDataAssets.generated.h"

/** PC 与 Android UMG 共享的主题令牌；只保存视觉数据，不包含运行时状态。 */
UCLASS(BlueprintType)
class GUIYANGMAHJONGCLIENT_API UGuiyangUIThemeDataAsset : public UDataAsset
{
    GENERATED_BODY()
public:
    /** 语义颜色、圆角和边框宽度映射；键名必须与 UI 视觉规范保持一致。 */
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="颜色") TMap<FName, FLinearColor> Colors;
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="圆角") TMap<FName, float> CornerRadii;
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="边框") TMap<FName, float> BorderWidths;
    /** 两类目标平台的设计基准尺寸，单位为逻辑像素，不代表实际设备分辨率。 */
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="尺寸") FVector2D PCDesignSize = FVector2D(1920.0, 1080.0);
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="尺寸") FVector2D AndroidWideSize = FVector2D(2400.0, 1080.0);
};

/** 按语义键集中提供按钮状态样式，供 UMG 页面复用而不是逐页复制 Brush。 */
UCLASS(BlueprintType)
class GUIYANGMAHJONGCLIENT_API UGuiyangUIButtonStylesDataAsset : public UDataAsset
{
    GENERATED_BODY()
public:
    /** 按钮样式值由资产拥有，运行时读取方不得原地修改共享实例。 */
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="按钮") TMap<FName, FButtonStyle> Styles;
};

/** 集中保存面板背景与边框 Brush，保证大厅、房间和对局界面视觉一致。 */
UCLASS(BlueprintType)
class GUIYANGMAHJONGCLIENT_API UGuiyangUIPanelStylesDataAsset : public UDataAsset
{
    GENERATED_BODY()
public:
    /** 语义面板键到 Slate Brush 的只读注册表。 */
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="面板") TMap<FName, FSlateBrush> Brushes;
};

/** 集中保存标题、正文、数值等文本层级的 Slate 字体样式。 */
UCLASS(BlueprintType)
class GUIYANGMAHJONGCLIENT_API UGuiyangUIFontStylesDataAsset : public UDataAsset
{
    GENERATED_BODY()
public:
    /** 字体样式由主题资产持久化，键名变化必须同步所有 UMG 使用方。 */
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="字体") TMap<FName, FTextBlockStyle> Styles;
};

/** UI 语义图标的软引用注册表，避免加载主题时同步加载全部纹理。 */
UCLASS(BlueprintType)
class GUIYANGMAHJONGCLIENT_API UGuiyangUIIconRegistryDataAsset : public UDataAsset
{
    GENERATED_BODY()
public:
    /** 图标资源由内容目录拥有；缺失键由控件层使用安全占位图降级。 */
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="图标") TMap<FName, TSoftObjectPtr<UTexture2D>> Icons;
};

/** 麻将牌规则索引到纹理的软引用注册表，同时提供牌背与交互状态贴图。 */
UCLASS(BlueprintType)
class GUIYANGMAHJONGCLIENT_API UGuiyangUITileTextureRegistryDataAsset : public UDataAsset
{
    GENERATED_BODY()
public:
    /** 规则索引必须与 FMahjongTile::GetRuleIndex 保持一致，值使用软引用控制移动端内存峰值。 */
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="麻将牌") TMap<int32, TSoftObjectPtr<UTexture2D>> RuleIndexToTexture;
    /** 通用牌背、空白正面、选中光效与禁用遮罩，由各牌控件按状态组合使用。 */
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="麻将牌") TSoftObjectPtr<UTexture2D> BackTexture;
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="麻将牌") TSoftObjectPtr<UTexture2D> FrontBlankTexture;
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="麻将牌") TSoftObjectPtr<UTexture2D> SelectedGlowTexture;
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="麻将牌") TSoftObjectPtr<UTexture2D> DisabledMaskTexture;
};
