#include "Config/GuiyangPlatformEndpointSettings.h"

#include "GuiyangMahjongOnline.h"
#include "HAL/PlatformProperties.h"
#include "Misc/CommandLine.h"
#include "Misc/ConfigCacheIni.h"
#include "Misc/Parse.h"

namespace GuiyangPlatformEndpointsPrivate
{
    constexpr const TCHAR* UnifiedSection =
        TEXT("/Script/GuiyangMahjongOnline.GuiyangPlatformEndpoints");
    constexpr const TCHAR* AuthSection =
        TEXT("/Script/GuiyangMahjongOnline.GuiyangLoginSubsystem");
    constexpr const TCHAR* LobbySection =
        TEXT("/Script/GuiyangMahjongClient.GuiyangLobbySubsystem");

    bool IsLoopbackHttp(const FString& Value)
    {
        return Value.StartsWith(
                   TEXT("http://127.0.0.1"),
                   ESearchCase::IgnoreCase)
            || Value.StartsWith(
                   TEXT("http://localhost"),
                   ESearchCase::IgnoreCase)
            || Value.StartsWith(
                   TEXT("http://[::1]"),
                   ESearchCase::IgnoreCase);
    }

    bool NormalizeRealtimeBaseUrl(
        const FString& Candidate,
        const bool bAllowLoopbackHttp,
        FString& OutBaseUrl)
    {
        if (FGuiyangPlatformEndpointSettings::NormalizeHttpBaseUrl(
                Candidate,
                bAllowLoopbackHttp,
                OutBaseUrl))
        {
            return true;
        }

        OutBaseUrl = Candidate.TrimStartAndEnd();
        while (OutBaseUrl.EndsWith(TEXT("/")))
        {
            OutBaseUrl.LeftChopInline(1);
        }
        if (OutBaseUrl.Contains(TEXT("@"))
            || OutBaseUrl.Contains(TEXT("?"))
            || OutBaseUrl.Contains(TEXT("#")))
        {
            return false;
        }
        if (OutBaseUrl.StartsWith(
                TEXT("wss://"),
                ESearchCase::IgnoreCase))
        {
            return OutBaseUrl.Len() > 8;
        }
        const bool bLoopbackWs =
            OutBaseUrl.StartsWith(
                TEXT("ws://127.0.0.1"),
                ESearchCase::IgnoreCase)
            || OutBaseUrl.StartsWith(
                TEXT("ws://localhost"),
                ESearchCase::IgnoreCase)
            || OutBaseUrl.StartsWith(
                TEXT("ws://[::1]"),
                ESearchCase::IgnoreCase);
#if UE_BUILD_SHIPPING
        return bAllowLoopbackHttp && bLoopbackWs;
#else
        return bLoopbackWs;
#endif
    }

    FString ReadLegacyBaseUrl(
        const EGuiyangLegacyEndpointRole Role)
    {
        FString Value;
        if (!GConfig)
        {
            return Value;
        }
        if (Role == EGuiyangLegacyEndpointRole::Auth)
        {
            GConfig->GetString(
                AuthSection,
                TEXT("AuthBaseUrl"),
                Value,
                GGameIni);
            FString CommandLineValue;
            if (FParse::Value(
                    FCommandLine::Get(),
                    TEXT("MahjongAuthBaseUrl="),
                    CommandLineValue))
            {
                Value = MoveTemp(CommandLineValue);
            }
        }
        else if (Role == EGuiyangLegacyEndpointRole::Lobby)
        {
            GConfig->GetString(
                LobbySection,
                TEXT("RemoteBaseUrl"),
                Value,
                GGameIni);
            FString CommandLineValue;
            if (FParse::Value(
                    FCommandLine::Get(),
                    TEXT("MahjongLobbyBaseUrl="),
                    CommandLineValue))
            {
                Value = MoveTemp(CommandLineValue);
            }
        }
        return Value;
    }
}

bool FGuiyangPlatformEndpointSettings::Load(
    const EGuiyangLegacyEndpointRole LegacyRole,
    FGuiyangPlatformEndpointSettings& OutSettings)
{
    using namespace GuiyangPlatformEndpointsPrivate;
    OutSettings = {};
    OutSettings.Platform =
        FPlatformProperties::PlatformName();
    if (GConfig)
    {
        GConfig->GetString(
            UnifiedSection,
            TEXT("ApiBaseUrl"),
            OutSettings.ApiBaseUrl,
            GGameIni);
        GConfig->GetString(
            UnifiedSection,
            TEXT("RealtimeBaseUrl"),
            OutSettings.RealtimeBaseUrl,
            GGameIni);
        GConfig->GetString(
            UnifiedSection,
            TEXT("PatchBaseUrl"),
            OutSettings.PatchBaseUrl,
            GGameIni);
        GConfig->GetString(
            UnifiedSection,
            TEXT("ClientVersion"),
            OutSettings.ClientVersion,
            GGameIni);
        GConfig->GetString(
            UnifiedSection,
            TEXT("ProtocolVersion"),
            OutSettings.ProtocolVersion,
            GGameIni);
        GConfig->GetString(
            UnifiedSection,
            TEXT("Platform"),
            OutSettings.Platform,
            GGameIni);
        GConfig->GetString(
            UnifiedSection,
            TEXT("Channel"),
            OutSettings.Channel,
            GGameIni);
    }

    FString CommandLineValue;
    if (FParse::Value(
            FCommandLine::Get(),
            TEXT("MahjongApiBaseUrl="),
            CommandLineValue))
    {
        OutSettings.ApiBaseUrl = MoveTemp(CommandLineValue);
    }
    if (FParse::Value(
            FCommandLine::Get(),
            TEXT("MahjongRealtimeBaseUrl="),
            CommandLineValue))
    {
        OutSettings.RealtimeBaseUrl = MoveTemp(CommandLineValue);
    }
    if (FParse::Value(
            FCommandLine::Get(),
            TEXT("MahjongPatchBaseUrl="),
            CommandLineValue))
    {
        OutSettings.PatchBaseUrl = MoveTemp(CommandLineValue);
    }
    if (FParse::Value(
            FCommandLine::Get(),
            TEXT("MahjongClientVersion="),
            CommandLineValue))
    {
        OutSettings.ClientVersion = MoveTemp(CommandLineValue);
    }
    if (FParse::Value(
            FCommandLine::Get(),
            TEXT("MahjongProtocolVersion="),
            CommandLineValue))
    {
        OutSettings.ProtocolVersion = MoveTemp(CommandLineValue);
    }
    if (FParse::Value(
            FCommandLine::Get(),
            TEXT("MahjongChannel="),
            CommandLineValue))
    {
        OutSettings.Channel = MoveTemp(CommandLineValue);
    }

    if (OutSettings.ApiBaseUrl.IsEmpty()
        && LegacyRole != EGuiyangLegacyEndpointRole::None)
    {
        OutSettings.ApiBaseUrl =
            ReadLegacyBaseUrl(LegacyRole);
        OutSettings.bUsingLegacyDirectEndpoint =
            !OutSettings.ApiBaseUrl.IsEmpty();
        if (OutSettings.bUsingLegacyDirectEndpoint)
        {
            // 只记录废弃配置名，不输出可能包含内部主机名的实际地址。
            UE_LOG(
                LogMahjongOnline,
                Warning,
                TEXT("检测到旧后端地址配置；AuthBaseUrl/RemoteBaseUrl 与对应命令行参数已废弃，请迁移到 ApiBaseUrl"));
        }
    }

    const bool bAllowLoopbackHttp =
        FParse::Param(
            FCommandLine::Get(),
            TEXT("MahjongAllowInsecureLoopbackApi"))
        || FParse::Param(
            FCommandLine::Get(),
            TEXT("MahjongAllowInsecureLoopbackAuth"));
    FString NormalizedApi;
    if (!NormalizeHttpBaseUrl(
            OutSettings.ApiBaseUrl,
            bAllowLoopbackHttp,
            NormalizedApi))
    {
        return false;
    }
    OutSettings.ApiBaseUrl = MoveTemp(NormalizedApi);

    if (OutSettings.RealtimeBaseUrl.IsEmpty())
    {
        OutSettings.RealtimeBaseUrl =
            OutSettings.ApiBaseUrl;
    }
    else
    {
        FString NormalizedRealtime;
        if (!NormalizeRealtimeBaseUrl(
                OutSettings.RealtimeBaseUrl,
                bAllowLoopbackHttp,
                NormalizedRealtime))
        {
            return false;
        }
        OutSettings.RealtimeBaseUrl =
            MoveTemp(NormalizedRealtime);
    }

    if (!OutSettings.PatchBaseUrl.IsEmpty())
    {
        FString NormalizedPatch;
        if (!NormalizeHttpBaseUrl(
                OutSettings.PatchBaseUrl,
                bAllowLoopbackHttp,
                NormalizedPatch))
        {
            return false;
        }
        OutSettings.PatchBaseUrl =
            MoveTemp(NormalizedPatch);
    }

    OutSettings.ClientVersion =
        OutSettings.ClientVersion.TrimStartAndEnd();
    OutSettings.ProtocolVersion =
        OutSettings.ProtocolVersion.TrimStartAndEnd();
    OutSettings.Platform =
        OutSettings.Platform.TrimStartAndEnd();
    if (OutSettings.Platform.IsEmpty())
    {
        OutSettings.Platform =
            FPlatformProperties::PlatformName();
    }
    OutSettings.Channel =
        OutSettings.Channel.TrimStartAndEnd();
    return !OutSettings.ClientVersion.IsEmpty()
        && !OutSettings.ProtocolVersion.IsEmpty()
        && !OutSettings.Platform.IsEmpty()
        && !OutSettings.Channel.IsEmpty();
}

FString FGuiyangPlatformEndpointSettings::BuildApiUrl(
    const FString& LegacyV1Path) const
{
    if (bUsingLegacyDirectEndpoint)
    {
        return ApiBaseUrl + LegacyV1Path;
    }

    // 现有 Lobby 的 reconnect/events 位于 /v1 根；外部契约把它们归入 /api/v1/game。
    if (LegacyV1Path.StartsWith(TEXT("/v1/reconnect/")))
    {
        return ApiBaseUrl
            + TEXT("/api/v1/game/reconnect/")
            + LegacyV1Path.RightChop(
                FCString::Strlen(TEXT("/v1/reconnect/")));
    }
    if (LegacyV1Path.Equals(TEXT("/v1/events")))
    {
        return ApiBaseUrl + TEXT("/api/v1/game/events");
    }
    return ApiBaseUrl + TEXT("/api") + LegacyV1Path;
}

void FGuiyangPlatformEndpointSettings::ApplyStandardHeaders(
    IHttpRequest& Request,
    const FString& RequestId) const
{
    const FString SafeRequestId =
        RequestId.IsEmpty()
            ? FGuid::NewGuid().ToString(
                EGuidFormats::DigitsLower)
            : RequestId;
    Request.SetHeader(
        TEXT("X-Request-Id"),
        SafeRequestId);
    Request.SetHeader(
        TEXT("X-Correlation-Id"),
        SafeRequestId);
    Request.SetHeader(
        TEXT("X-Client-Version"),
        ClientVersion);
    Request.SetHeader(
        TEXT("X-Protocol-Version"),
        ProtocolVersion);
    Request.SetHeader(
        TEXT("X-Platform"),
        Platform);
    Request.SetHeader(
        TEXT("X-Channel"),
        Channel);
}

bool FGuiyangPlatformEndpointSettings::NormalizeHttpBaseUrl(
    const FString& Candidate,
    const bool bAllowLoopbackHttp,
    FString& OutBaseUrl)
{
    using namespace GuiyangPlatformEndpointsPrivate;
    OutBaseUrl = Candidate.TrimStartAndEnd();
    while (OutBaseUrl.EndsWith(TEXT("/")))
    {
        OutBaseUrl.LeftChopInline(1);
    }
    if (OutBaseUrl.Contains(TEXT("@"))
        || OutBaseUrl.Contains(TEXT("?"))
        || OutBaseUrl.Contains(TEXT("#")))
    {
        return false;
    }
    if (OutBaseUrl.StartsWith(
            TEXT("https://"),
            ESearchCase::IgnoreCase))
    {
        return OutBaseUrl.Len() > 10;
    }
    const bool bLoopbackHttp =
        IsLoopbackHttp(OutBaseUrl);
#if UE_BUILD_SHIPPING
    return bAllowLoopbackHttp && bLoopbackHttp;
#else
    return bLoopbackHttp;
#endif
}
