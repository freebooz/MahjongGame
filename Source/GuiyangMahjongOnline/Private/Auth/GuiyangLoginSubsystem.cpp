#include "Auth/GuiyangLoginSubsystem.h"

#include "Auth/GuiyangLoginSaveGame.h"
#include "Dom/JsonObject.h"
#include "Engine/World.h"
#include "GuiyangMahjongOnline.h"
#include "HttpModule.h"
#include "Kismet/GameplayStatics.h"
#include "Misc/CommandLine.h"
#include "Misc/ConfigCacheIni.h"
#include "Misc/Parse.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "TimerManager.h"

namespace GuiyangAuthPrivate
{
    constexpr const TCHAR* ConfigSection = TEXT("/Script/GuiyangMahjongOnline.GuiyangLoginSubsystem");

    FString SerializeObject(const TSharedRef<FJsonObject>& Object)
    {
        FString Json;
        const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Json);
        FJsonSerializer::Serialize(Object, Writer);
        return Json;
    }
}

#if 0 // Implemented by GuiyangMahjongCore so server builds do not link the client auth module.
FString FGuiyangLoginProfile::GetProviderDisplayName() const
{
    switch (Provider)
    {
    case EGuiyangLoginProvider::Guest: return TEXT("游客登录");
    case EGuiyangLoginProvider::SimulatedWechat: return TEXT("模拟微信登录");
    case EGuiyangLoginProvider::Wechat: return TEXT("微信登录");
    default: return TEXT("未登录");
    }
}

#endif

void UGuiyangLoginSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
    // 从本地存档恢复匿名身份与自动登录偏好，但不把过期远程会话视为有效登录。
    Super::Initialize(Collection);
    if (UGameplayStatics::DoesSaveGameExist(SaveSlotName, 0))
    {
        LoginSettings = Cast<UGuiyangLoginSaveGame>(UGameplayStatics::LoadGameFromSlot(SaveSlotName, 0));
    }
    if (!LoginSettings)
    {
        LoginSettings = Cast<UGuiyangLoginSaveGame>(UGameplayStatics::CreateSaveGameObject(UGuiyangLoginSaveGame::StaticClass()));
    }
    if (LoginSettings && LoginSettings->InstallationId.IsEmpty())
    {
        LoginSettings->InstallationId = FString::Printf(
            TEXT("install-%s"), *FGuid::NewGuid().ToString(EGuidFormats::DigitsLower));
        UGameplayStatics::SaveGameToSlot(LoginSettings, SaveSlotName, 0);
    }

    FString AuthMode = TEXT("LocalDevelopment");
    if (GConfig)
    {
        GConfig->GetString(GuiyangAuthPrivate::ConfigSection, TEXT("AuthMode"), AuthMode, GGameIni);
    }
    FString CommandLineMode;
    if (FParse::Value(FCommandLine::Get(), TEXT("MahjongAuthMode="), CommandLineMode)) AuthMode = MoveTemp(CommandLineMode);
    bUseRemoteAuth = AuthMode.Equals(TEXT("RemoteAuth"), ESearchCase::IgnoreCase);
    if (bUseRemoteAuth
        && !FGuiyangPlatformEndpointSettings::Load(
            EGuiyangLegacyEndpointRole::Auth,
            PlatformEndpoints))
    {
        PlatformEndpoints = {};
        UE_LOG(
            LogMahjongOnline,
            Error,
            TEXT("RemoteAuth 的 ApiBaseUrl 无效；正式环境必须使用 HTTPS，本机开发仅允许 loopback HTTP"));
    }
    UE_LOG(LogMahjongOnline, Log, TEXT("登录子系统初始化完成，模式=%s，自动登录=禁止"),
        bUseRemoteAuth ? TEXT("RemoteAuth") : TEXT("LocalDevelopment"));
}

void UGuiyangLoginSubsystem::Deinitialize()
{
    if (UWorld* World = GetWorld())
    {
        World->GetTimerManager().ClearTimer(PendingLoginTimer);
        World->GetTimerManager().ClearTimer(RefreshTimer);
    }
    SessionToken.Reset();
    RefreshToken.Reset();
    CurrentProfile = {};
    LoginState = EGuiyangLoginState::LoggedOut;
    Super::Deinitialize();
}

void UGuiyangLoginSubsystem::LoginAsGuest()
{
    BeginLogin(EGuiyangLoginProvider::Guest);
}

void UGuiyangLoginSubsystem::LoginWithWechat()
{
#if PLATFORM_WINDOWS
    UE_LOG(LogMahjongOnline, Log, TEXT("PC 开发环境启用模拟微信授权，不代表正式微信开放平台登录"));
    // Auth currently exposes a real Guest endpoint but no WeChat OAuth endpoint.
    // PC manual review therefore obtains a server-issued guest session instead
    // of falling back to local auth when RemoteAuth is selected.
    if (bUseRemoteAuth)
    {
        BeginLogin(EGuiyangLoginProvider::Guest);
        return;
    }
    BeginLogin(EGuiyangLoginProvider::SimulatedWechat);
#else
    FailLogin(TEXT("当前安装包尚未配置正式微信开放平台授权"));
#endif
}

void UGuiyangLoginSubsystem::BeginLogin(const EGuiyangLoginProvider Provider, const FString& ExistingPlayerId, const FString& ExistingName)
{
    // 状态先切到 LoggingIn 并广播，防止 UI 重复点击产生并发登录。
    if (LoginState == EGuiyangLoginState::LoggingIn)
    {
        FailLogin(TEXT("登录请求正在处理中，请勿重复点击"));
        return;
    }
    if (Provider != EGuiyangLoginProvider::Guest && Provider != EGuiyangLoginProvider::SimulatedWechat)
    {
        FailLogin(TEXT("当前登录方式不可用"));
        return;
    }

    LoginState = EGuiyangLoginState::LoggingIn;
    OnLoginStateChanged.Broadcast(LoginState, CurrentProfile);
    UE_LOG(LogMahjongOnline, Log, TEXT("开始处理登录请求：Provider=%s"), Provider == EGuiyangLoginProvider::Guest ? TEXT("游客") : TEXT("模拟微信"));

    if (Provider == EGuiyangLoginProvider::Guest && bUseRemoteAuth)
    {
        BeginRemoteGuestLogin(ExistingName);
        return;
    }
#if UE_BUILD_SHIPPING
    FailLogin(TEXT("正式包必须配置 RemoteAuth"));
    return;
#else
    const FString NewPlayerId = ExistingPlayerId.IsEmpty()
        ? FString::Printf(TEXT("%s-%s"), Provider == EGuiyangLoginProvider::Guest ? TEXT("guest") : TEXT("wxsim"), *FGuid::NewGuid().ToString(EGuidFormats::Digits))
        : ExistingPlayerId;
    const FString NewDisplayName = ExistingName.IsEmpty()
        ? FString::Printf(TEXT("%s%s"), Provider == EGuiyangLoginProvider::Guest ? TEXT("游客") : TEXT("微信玩家"), *MakePlayerSuffix(NewPlayerId))
        : ExistingName.Left(24);

    if (UWorld* World = GetWorld())
    {
        World->GetTimerManager().SetTimer(PendingLoginTimer, FTimerDelegate::CreateUObject(this, &ThisClass::CompleteLogin, Provider, NewPlayerId, NewDisplayName), 0.15f, false);
    }
    else
    {
        CompleteLogin(Provider, NewPlayerId, NewDisplayName);
    }
#endif
}

void UGuiyangLoginSubsystem::CompleteLogin(const EGuiyangLoginProvider Provider, FString PlayerId, FString DisplayName)
{
    // 本地开发登录生成稳定访客身份，并只持久化允许自动恢复的非敏感资料。
    if (LoginState != EGuiyangLoginState::LoggingIn)
    {
        return;
    }
    CurrentProfile.PlayerId = MoveTemp(PlayerId);
    CurrentProfile.DisplayName = MoveTemp(DisplayName);
    CurrentProfile.Provider = Provider;
    SessionToken = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphens);
    SessionExpireAtUtc = FDateTime::UtcNow() + FTimespan::FromHours(12.0);
    LoginState = EGuiyangLoginState::LoggedIn;
    UE_LOG(LogMahjongOnline, Log, TEXT("登录成功：PlayerId=%s，昵称=%s，Provider=%s"), *CurrentProfile.PlayerId, *CurrentProfile.DisplayName, *CurrentProfile.GetProviderDisplayName());
    OnLoginStateChanged.Broadcast(LoginState, CurrentProfile);
}

void UGuiyangLoginSubsystem::BeginRemoteGuestLogin(const FString& ExistingName)
{
    if (PlatformEndpoints.ApiBaseUrl.IsEmpty()
        || !LoginSettings
        || LoginSettings->InstallationId.IsEmpty())
    {
        FailLogin(TEXT("远程登录服务尚未安全配置"));
        return;
    }

    const TSharedRef<FJsonObject> Payload = MakeShared<FJsonObject>();
    Payload->SetStringField(TEXT("installationId"), LoginSettings->InstallationId);
    if (!ExistingName.IsEmpty()) Payload->SetStringField(TEXT("displayName"), ExistingName.Left(24));
    const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request = FHttpModule::Get().CreateRequest();
    Request->SetURL(
        PlatformEndpoints.BuildApiUrl(
            TEXT("/v1/auth/guest")));
    Request->SetVerb(TEXT("POST"));
    Request->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
    PlatformEndpoints.ApplyStandardHeaders(Request.Get());
    Request->SetContentAsString(GuiyangAuthPrivate::SerializeObject(Payload));
    Request->SetTimeout(10.0f);
    Request->OnProcessRequestComplete().BindUObject(this, &ThisClass::CompleteRemoteGuestLogin);
    if (!Request->ProcessRequest()) FailLogin(TEXT("无法发起远程登录请求"));
}

void UGuiyangLoginSubsystem::CompleteRemoteGuestLogin(
    FHttpRequestPtr Request, FHttpResponsePtr Response, const bool bSucceeded)
{
    if (LoginState != EGuiyangLoginState::LoggingIn) return;
    if (!bSucceeded || !Response.IsValid() || !EHttpResponseCodes::IsOk(Response->GetResponseCode())
        || !ApplyRemoteSessionResponse(Response, true))
    {
        FailLogin(TEXT("登录服务暂时不可用或返回了无效会话"));
    }
}

void UGuiyangLoginSubsystem::RefreshRemoteSession()
{
    if (!bUseRemoteAuth || RefreshToken.IsEmpty() || LoginState != EGuiyangLoginState::LoggedIn) return;
    const TSharedRef<FJsonObject> Payload = MakeShared<FJsonObject>();
    Payload->SetStringField(TEXT("refreshToken"), RefreshToken);
    const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request = FHttpModule::Get().CreateRequest();
    Request->SetURL(
        PlatformEndpoints.BuildApiUrl(
            TEXT("/v1/auth/refresh")));
    Request->SetVerb(TEXT("POST"));
    Request->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
    PlatformEndpoints.ApplyStandardHeaders(Request.Get());
    Request->SetContentAsString(GuiyangAuthPrivate::SerializeObject(Payload));
    Request->SetTimeout(10.0f);
    Request->OnProcessRequestComplete().BindUObject(this, &ThisClass::CompleteRemoteRefresh);
    if (!Request->ProcessRequest()) ExpireSession(TEXT("无法刷新登录会话，请重新登录"));
}

void UGuiyangLoginSubsystem::CompleteRemoteRefresh(
    FHttpRequestPtr Request, FHttpResponsePtr Response, const bool bSucceeded)
{
    if (LoginState != EGuiyangLoginState::LoggedIn) return;
    if (!bSucceeded || !Response.IsValid() || !EHttpResponseCodes::IsOk(Response->GetResponseCode())
        || !ApplyRemoteSessionResponse(Response, false))
    {
        ExpireSession(TEXT("登录会话刷新失败，请重新登录"));
    }
}

bool UGuiyangLoginSubsystem::ApplyRemoteSessionResponse(
    const FHttpResponsePtr& Response, const bool bInitialLogin)
{
    // 远程响应必须同时包含玩家资料、会话令牌和合理过期时间。
    TSharedPtr<FJsonObject> Json;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Response->GetContentAsString());
    if (!FJsonSerializer::Deserialize(Reader, Json) || !Json.IsValid()) return false;

    FString PlayerId;
    FString DisplayName;
    FString Provider;
    FString AccessToken;
    FString NewRefreshToken;
    FString ExpiresAt;
    if (!Json->TryGetStringField(TEXT("playerId"), PlayerId)
        || !Json->TryGetStringField(TEXT("displayName"), DisplayName)
        || !Json->TryGetStringField(TEXT("provider"), Provider)
        || !Json->TryGetStringField(TEXT("accessToken"), AccessToken)
        || !Json->TryGetStringField(TEXT("refreshToken"), NewRefreshToken)
        || !Json->TryGetStringField(TEXT("accessTokenExpiresAtUtc"), ExpiresAt)
        || !Provider.Equals(TEXT("Guest"), ESearchCase::CaseSensitive)
        || PlayerId.IsEmpty() || PlayerId.Len() > 80
        || DisplayName.IsEmpty() || DisplayName.Len() > 24
        || AccessToken.Len() < 32 || AccessToken.Len() > 4096
        || NewRefreshToken.Len() < 64 || NewRefreshToken.Len() > 256
        || !FDateTime::ParseIso8601(*ExpiresAt, SessionExpireAtUtc)
        || SessionExpireAtUtc <= FDateTime::UtcNow())
    {
        return false;
    }

    SessionToken = MoveTemp(AccessToken);
    RefreshToken = MoveTemp(NewRefreshToken);
    CurrentProfile.PlayerId = MoveTemp(PlayerId);
    CurrentProfile.DisplayName = MoveTemp(DisplayName);
    CurrentProfile.Provider = EGuiyangLoginProvider::Guest;
    if (bInitialLogin)
    {
        LoginState = EGuiyangLoginState::LoggedIn;
        UE_LOG(LogMahjongOnline, Log, TEXT("RemoteAuth 登录成功：PlayerId=%s，昵称=%s"),
            *CurrentProfile.PlayerId, *CurrentProfile.DisplayName);
        OnLoginStateChanged.Broadcast(LoginState, CurrentProfile);
    }
    ScheduleRemoteRefresh();
    return true;
}

void UGuiyangLoginSubsystem::ScheduleRemoteRefresh()
{
    // 在过期前预留刷新窗口；旧定时器在每次新会话到达时被替换。
    if (UWorld* World = GetWorld())
    {
        const double SecondsUntilRefresh = FMath::Max(
            5.0, (SessionExpireAtUtc - FDateTime::UtcNow()).GetTotalSeconds() - 60.0);
        World->GetTimerManager().SetTimer(
            RefreshTimer, this, &ThisClass::RefreshRemoteSession, SecondsUntilRefresh, false);
    }
}

void UGuiyangLoginSubsystem::FailLogin(const FString& ChineseReason)
{
    SessionToken.Reset();
    RefreshToken.Reset();
    SessionExpireAtUtc = FDateTime();
    LoginState = EGuiyangLoginState::LoggedOut;
    UE_LOG(LogMahjongOnline, Warning, TEXT("登录失败：%s"), *ChineseReason);
    OnLoginFailed.Broadcast(ChineseReason);
    OnLoginStateChanged.Broadcast(LoginState, CurrentProfile);
}

void UGuiyangLoginSubsystem::Logout()
{
    // 先清除内存令牌和刷新计时器，再更新存档与 UI 状态。
    if (UWorld* World = GetWorld())
    {
        World->GetTimerManager().ClearTimer(PendingLoginTimer);
        World->GetTimerManager().ClearTimer(RefreshTimer);
    }
    if (bUseRemoteAuth
        && !PlatformEndpoints.ApiBaseUrl.IsEmpty()
        && !RefreshToken.IsEmpty())
    {
        const TSharedRef<FJsonObject> Payload = MakeShared<FJsonObject>();
        Payload->SetStringField(TEXT("refreshToken"), RefreshToken);
        const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request = FHttpModule::Get().CreateRequest();
        Request->SetURL(
            PlatformEndpoints.BuildApiUrl(
                TEXT("/v1/auth/logout")));
        Request->SetVerb(TEXT("POST"));
        Request->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
        PlatformEndpoints.ApplyStandardHeaders(Request.Get());
        Request->SetContentAsString(GuiyangAuthPrivate::SerializeObject(Payload));
        Request->SetTimeout(5.0f);
        Request->ProcessRequest();
    }
    SessionToken.Reset();
    RefreshToken.Reset();
    SessionExpireAtUtc = FDateTime();
    CurrentProfile = {};
    LoginState = EGuiyangLoginState::LoggedOut;
    if (LoginSettings)
    {
        UGameplayStatics::SaveGameToSlot(LoginSettings, SaveSlotName, 0);
    }
    UE_LOG(LogMahjongOnline, Log, TEXT("玩家已安全退出登录，本地会话已清理"));
    OnLoginStateChanged.Broadcast(LoginState, CurrentProfile);
}

void UGuiyangLoginSubsystem::ExpireSession(const FString& ChineseReason)
{
    // 网络层发现 401/会话过期时统一进入 Expired，要求用户重新登录。
    SessionToken.Reset();
    RefreshToken.Reset();
    SessionExpireAtUtc = FDateTime();
    LoginState = EGuiyangLoginState::Expired;
    if (UWorld* World = GetWorld()) World->GetTimerManager().ClearTimer(RefreshTimer);
    UE_LOG(LogMahjongOnline, Warning, TEXT("会话过期：%s"), *ChineseReason);
    OnLoginFailed.Broadcast(ChineseReason);
    OnLoginStateChanged.Broadcast(LoginState, CurrentProfile);
}

bool UGuiyangLoginSubsystem::IsSessionValid() const
{
    return LoginState == EGuiyangLoginState::LoggedIn && CurrentProfile.IsValid() && !SessionToken.IsEmpty() && FDateTime::UtcNow() < SessionExpireAtUtc;
}

bool UGuiyangLoginSubsystem::LoginForIntegrationTest(const FString& PlayerId, const FString& DisplayName,
    const FString& InSessionToken)
{
#if UE_BUILD_SHIPPING
    return false;
#else
    if (!FParse::Param(FCommandLine::Get(), TEXT("MahjongEnableIntegrationHooks"))
        || PlayerId.IsEmpty() || DisplayName.IsEmpty() || InSessionToken.Len() < 16)
    {
        return false;
    }
    if (UWorld* World = GetWorld()) World->GetTimerManager().ClearTimer(PendingLoginTimer);
    CurrentProfile.PlayerId = PlayerId.Left(80);
    CurrentProfile.DisplayName = DisplayName.Left(24);
    CurrentProfile.Provider = EGuiyangLoginProvider::Guest;
    SessionToken = InSessionToken.Left(256);
    SessionExpireAtUtc = FDateTime::UtcNow() + FTimespan::FromHours(1.0);
    LoginState = EGuiyangLoginState::LoggedIn;
    UE_LOG(LogMahjongOnline, Display, TEXT("MAHJONG_INTEGRATION_LOGIN_READY Player=%s"), *CurrentProfile.PlayerId);
    OnLoginStateChanged.Broadcast(LoginState, CurrentProfile);
    return true;
#endif
}

FString UGuiyangLoginSubsystem::MakePlayerSuffix(const FString& PlayerId)
{
    return PlayerId.Len() >= 4 ? PlayerId.Right(4).ToUpper() : TEXT("0000");
}
