#pragma once

#include "CoreMinimal.h"
#include "Engine/EngineBaseTypes.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "GuiyangReconnectSubsystem.generated.h"

class UNetDriver;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_ThreeParams(FMahjongReconnectStateChanged,
    const FString&, Status, int32, RemainingSeconds, bool, bCanRetry);

/**
 * 跨地图保存连接端点与重连窗口。网络/Travel 失败后 PlayerController 即使重建，
 * 新 RootHUD 仍可恢复同一重连遮罩和剩余时间。
 */
UCLASS()
class GUIYANGMAHJONGCLIENT_API UGuiyangReconnectSubsystem : public UGameInstanceSubsystem
{
    GENERATED_BODY()

public:
    /** 注册全局网络/旅行失败监听；销毁时解除句柄，防止跨 PIE 会话重复响应。 */
    virtual void Initialize(FSubsystemCollectionBase& Collection) override;
    virtual void Deinitialize() override;

    UPROPERTY(BlueprintAssignable, Category="麻将|重连") FMahjongReconnectStateChanged OnReconnectStateChanged;

    UFUNCTION(BlueprintCallable, Category="麻将|重连")
    void RememberConnection(const FString& ServerIP, int32 ServerPort, const FString& PlayerName);
    UFUNCTION(BlueprintCallable, Category="麻将|重连")
    void RememberRemoteRoute(const FString& RoomId, const FString& MatchId);
    UFUNCTION(BlueprintCallable, Category="麻将|重连")
    void BeginReconnectWindow(const FString& Status, int32 TimeoutSeconds);
    UFUNCTION(BlueprintCallable, Category="麻将|重连") void MarkRetrying();
    UFUNCTION(BlueprintCallable, Category="麻将|重连") void MarkRestored();
    UFUNCTION(BlueprintCallable, Category="麻将|重连") void MarkRetryFailed(const FString& Status);
    UFUNCTION(BlueprintCallable, Category="麻将|重连") void CancelReconnect();

    UFUNCTION(BlueprintPure, Category="麻将|重连") bool IsReconnectPending() const { return bReconnectPending; }
    UFUNCTION(BlueprintPure, Category="麻将|重连") int32 GetRemainingSeconds() const;
    UFUNCTION(BlueprintPure, Category="麻将|重连") bool CanRetry() const;
    UFUNCTION(BlueprintPure, Category="麻将|重连") const FString& GetStatus() const { return ReconnectStatus; }

    bool GetLastConnection(FString& OutServerIP, int32& OutServerPort, FString& OutPlayerName) const;
    bool GetLastRemoteRoute(FString& OutRoomId, FString& OutMatchId) const;
    static int32 ClampReconnectTimeoutSeconds(int32 TimeoutSeconds);

private:
    void HandleNetworkFailure(UWorld* World, UNetDriver* NetDriver,
        ENetworkFailure::Type FailureType, const FString& ErrorString);
    void HandleTravelFailure(UWorld* World, ETravelFailure::Type FailureType, const FString& ErrorString);
    void BroadcastState();

    FDelegateHandle NetworkFailureHandle;
    FDelegateHandle TravelFailureHandle;
    UPROPERTY() FString LastServerIP;
    UPROPERTY() FString LastPlayerName;
    UPROPERTY() FString ReconnectStatus;
    UPROPERTY() FString LastRemoteRoomId;
    UPROPERTY() FString LastRemoteMatchId;
    int32 LastServerPort = 7777;
    double ReconnectDeadlineSeconds = 0.0;
    bool bReconnectPending = false;
    bool bRetrying = false;
};
