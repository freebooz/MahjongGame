#pragma once

#include "CoreMinimal.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "Auth/GuiyangLoginTypes.h"
#include "GuiyangLoginSubsystem.generated.h"

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FGuiyangLoginStateChanged, EGuiyangLoginState, State, const FGuiyangLoginProfile&, Profile);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FGuiyangLoginFailed, const FString&, ChineseReason);

/**
 * 客户端登录会话入口。PC MVP 提供真实可用游客登录和明确标记的模拟微信 Provider。
 * SessionToken 仅保存在进程内存，并且任何日志都不得输出其内容。
 */
UCLASS()
class GUIYANGMAHJONGONLINE_API UGuiyangLoginSubsystem : public UGameInstanceSubsystem
{
    GENERATED_BODY()

public:
    UPROPERTY(BlueprintAssignable, Category="麻将|登录") FGuiyangLoginStateChanged OnLoginStateChanged;
    UPROPERTY(BlueprintAssignable, Category="麻将|登录") FGuiyangLoginFailed OnLoginFailed;

    virtual void Initialize(FSubsystemCollectionBase& Collection) override;
    virtual void Deinitialize() override;

    UFUNCTION(BlueprintCallable, Category="麻将|登录") void LoginAsGuest();
    UFUNCTION(BlueprintCallable, Category="麻将|登录") void LoginWithWechat();
    UFUNCTION(BlueprintCallable, Category="麻将|登录") void Logout();
    UFUNCTION(BlueprintCallable, Category="麻将|登录") void ExpireSession(const FString& ChineseReason = TEXT("登录会话已过期，请重新登录"));

    UFUNCTION(BlueprintPure, Category="麻将|登录") EGuiyangLoginState GetLoginState() const { return LoginState; }
    UFUNCTION(BlueprintPure, Category="麻将|登录") const FGuiyangLoginProfile& GetCurrentProfile() const { return CurrentProfile; }
    UFUNCTION(BlueprintPure, Category="麻将|登录") bool IsSessionValid() const;

    /** 仅供本地网络层提交给服务器验证；调用方不得记录返回值。 */
    const FString& GetSessionTokenForNetwork() const { return SessionToken; }

    /** 仅供显式启用的非 Shipping 多进程集成测试使用，不写 SaveGame。 */
    bool LoginForIntegrationTest(const FString& PlayerId, const FString& DisplayName, const FString& InSessionToken);

private:
    static constexpr const TCHAR* SaveSlotName = TEXT("GuiyangLoginSettings");

    UPROPERTY() EGuiyangLoginState LoginState = EGuiyangLoginState::LoggedOut;
    UPROPERTY() FGuiyangLoginProfile CurrentProfile;
    UPROPERTY() TObjectPtr<class UGuiyangLoginSaveGame> LoginSettings;

    FString SessionToken;
    FString RefreshToken;
    FDateTime SessionExpireAtUtc;
    FTimerHandle PendingLoginTimer;
    FTimerHandle RefreshTimer;
    FString AuthBaseUrl;
    bool bUseRemoteAuth = false;

    void BeginLogin(EGuiyangLoginProvider Provider, const FString& ExistingPlayerId = FString(), const FString& ExistingName = FString());
    void CompleteLogin(EGuiyangLoginProvider Provider, FString PlayerId, FString DisplayName);
    void BeginRemoteGuestLogin(const FString& ExistingName);
    void CompleteRemoteGuestLogin(FHttpRequestPtr Request, FHttpResponsePtr Response, bool bSucceeded);
    void RefreshRemoteSession();
    void CompleteRemoteRefresh(FHttpRequestPtr Request, FHttpResponsePtr Response, bool bSucceeded);
    bool ApplyRemoteSessionResponse(const FHttpResponsePtr& Response, bool bInitialLogin);
    void ScheduleRemoteRefresh();
    void FailLogin(const FString& ChineseReason);
    static FString MakePlayerSuffix(const FString& PlayerId);
};
