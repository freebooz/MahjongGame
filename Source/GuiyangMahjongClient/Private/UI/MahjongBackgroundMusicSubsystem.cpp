#include "UI/MahjongBackgroundMusicSubsystem.h"

#include "Components/AudioComponent.h"
#include "Kismet/GameplayStatics.h"
#include "Sound/SoundBase.h"
#include "UI/MahjongLocalSettings.h"

namespace
{
    /** 客户端背景音乐软路径；仅在首次播放时加载。 */
    constexpr TCHAR BackgroundMusicPath[] =
        TEXT("/Game/UI/Audio/BGM_FirstLightParticles.BGM_FirstLightParticles");
}

void UMahjongBackgroundMusicSubsystem::EnsurePlaying(const UObject* WorldContextObject)
{
    // 复用跨关卡 AudioComponent，避免每次切换 UI 重叠播放。
    if (!IsValid(MusicComponent))
    {
        USoundBase* Music = LoadObject<USoundBase>(nullptr, BackgroundMusicPath);
        if (!Music || !WorldContextObject)
        {
            return;
        }

        MusicComponent = UGameplayStatics::SpawnSound2D(
            WorldContextObject, Music, 0.0f, 1.0f, 0.0f, nullptr, true, false);
    }

    ApplyLocalSettings();
}

void UMahjongBackgroundMusicSubsystem::ApplyLocalSettings()
{
    if (!IsValid(MusicComponent))
    {
        return;
    }

    // 音乐开关通过零音量实现，保留播放位置以便再次开启时平滑继续。
    const FMahjongLocalSettings Settings = FMahjongLocalSettings::Load();
    MusicComponent->SetVolumeMultiplier(Settings.bMusicEnabled ? Settings.MusicVolume : 0.0f);
}

void UMahjongBackgroundMusicSubsystem::Deinitialize()
{
    if (IsValid(MusicComponent))
    {
        MusicComponent->Stop();
        MusicComponent = nullptr;
    }
    Super::Deinitialize();
}
