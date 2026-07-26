#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "MobileSettingsWidget.generated.h"

class UButton;
class UCheckBox;
class USlider;
class UTextBlock;

/** 大厅本地设置弹窗，设置即时生效并写入设备本地配置。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileSettingsWidget : public UUserWidget
{
    GENERATED_BODY()

protected:
    /** 绑定控件事件并从本地配置加载初始值。 */
    virtual void NativeConstruct() override;

    /** 音乐、音效、振动开关及对应音量控件。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_MusicEnabled;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_SoundEnabled;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_VibrationEnabled;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<USlider> Slider_MusicVolume;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<USlider> Slider_SoundVolume;
    /** 格式化显示当前音量百分比。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_MusicVolumeValue;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_SoundVolumeValue;
    /** 恢复默认、离开游戏和关闭弹窗按钮。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Reset;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_ExitGame;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Close;

    /** 控件变化后立即保存并应用到音频子系统。 */
    UFUNCTION() void HandleToggleChanged(bool bIsChecked);
    UFUNCTION() void HandleMusicVolumeChanged(float Value);
    UFUNCTION() void HandleSoundVolumeChanged(float Value);
    UFUNCTION() void HandleReset();
    UFUNCTION() void HandleExitGame();
    UFUNCTION() void HandleClose();

private:
    /** 防止程序刷新控件时反向触发保存事件。 */
    bool bUpdatingControls = false;
    /** 本地配置与可视控件之间的双向同步。 */
    void ApplySettingsToControls();
    void SaveSettingsFromControls();
    void UpdateVolumeLabels();
};
