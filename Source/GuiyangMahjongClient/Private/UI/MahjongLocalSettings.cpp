#include "UI/MahjongLocalSettings.h"

#include "Misc/ConfigCacheIni.h"

namespace
{
/** GameUserSettings.ini 中的独立配置段，避免与引擎图形设置混用。 */
const TCHAR* SettingsSection = TEXT("/Script/GuiyangMahjong.MahjongLocalSettings");
}

FMahjongLocalSettings FMahjongLocalSettings::Load()
{
    FMahjongLocalSettings Settings;
    // 缺少字段时保留结构体默认值，兼容旧版本配置。
    if (GConfig)
    {
        GConfig->GetBool(SettingsSection, TEXT("MusicEnabled"), Settings.bMusicEnabled, GGameUserSettingsIni);
        GConfig->GetBool(SettingsSection, TEXT("SoundEnabled"), Settings.bSoundEnabled, GGameUserSettingsIni);
        GConfig->GetBool(SettingsSection, TEXT("VibrationEnabled"), Settings.bVibrationEnabled, GGameUserSettingsIni);
        GConfig->GetFloat(SettingsSection, TEXT("MusicVolume"), Settings.MusicVolume, GGameUserSettingsIni);
        GConfig->GetFloat(SettingsSection, TEXT("SoundVolume"), Settings.SoundVolume, GGameUserSettingsIni);
    }
    Settings.Sanitize();
    return Settings;
}

void FMahjongLocalSettings::Save() const
{
    if (!GConfig)
    {
        return;
    }

    // 写盘前复制并归一化，调用者内存中的值不被隐式修改。
    FMahjongLocalSettings Settings = *this;
    Settings.Sanitize();
    GConfig->SetBool(SettingsSection, TEXT("MusicEnabled"), Settings.bMusicEnabled, GGameUserSettingsIni);
    GConfig->SetBool(SettingsSection, TEXT("SoundEnabled"), Settings.bSoundEnabled, GGameUserSettingsIni);
    GConfig->SetBool(SettingsSection, TEXT("VibrationEnabled"), Settings.bVibrationEnabled, GGameUserSettingsIni);
    GConfig->SetFloat(SettingsSection, TEXT("MusicVolume"), Settings.MusicVolume, GGameUserSettingsIni);
    GConfig->SetFloat(SettingsSection, TEXT("SoundVolume"), Settings.SoundVolume, GGameUserSettingsIni);
    GConfig->Flush(false, GGameUserSettingsIni);
}

void FMahjongLocalSettings::Sanitize()
{
    // 所有音量统一限制在音频组件接受的标准区间。
    MusicVolume = FMath::Clamp(MusicVolume, 0.0f, 1.0f);
    SoundVolume = FMath::Clamp(SoundVolume, 0.0f, 1.0f);
}
