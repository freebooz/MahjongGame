#pragma once

#include "CoreMinimal.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "Lobby/GuiyangLobbyBackend.h"
#include "Lobby/GuiyangLobbyTypes.h"
#include "Network/MahjongNetworkTypes.h"
#include "GuiyangLobbySubsystem.generated.h"

class APlayerController;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_ThreeParams(FGuiyangLobbyRequestFailed,
    const FString&, RequestId, EGuiyangLobbyErrorCode, ErrorCode, const FString&, ChineseMessage);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FGuiyangLobbyRequestSubmitted,
    const FString&, RequestId, EGuiyangLobbyBackendMode, BackendMode);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FGuiyangLobbyBootstrapUpdated,
    const FGuiyangLobbyBootstrap&, Bootstrap);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FGuiyangLobbyRouteReady,
    const FGuiyangGameServerRoute&, Route);

/**
 * 客户端大厅统一入口。当前默认 LocalLegacy；RemoteLobby 只有在后端完整配置后才允许启用。
 * 所有 UI 请求经此处生成关联 RequestId，便于后续串联大厅、分配器和 GameServer 日志。
 */
UCLASS()
class GUIYANGMAHJONGCLIENT_API UGuiyangLobbySubsystem final : public UGameInstanceSubsystem
{
    GENERATED_BODY()

public:
    /** 初始化时选择并创建大厅后端；销毁时释放异步后端，避免回调进入失效 GameInstance。 */
    virtual void Initialize(FSubsystemCollectionBase& Collection) override;
    virtual void Deinitialize() override;

    UPROPERTY(BlueprintAssignable, Category="麻将|大厅") FGuiyangLobbyRequestFailed OnRequestFailed;
    UPROPERTY(BlueprintAssignable, Category="麻将|大厅") FGuiyangLobbyRequestSubmitted OnRequestSubmitted;
    UPROPERTY(BlueprintAssignable, Category="麻将|大厅") FGuiyangLobbyBootstrapUpdated OnBootstrapUpdated;
    UPROPERTY(BlueprintAssignable, Category="麻将|大厅") FGuiyangLobbyRouteReady OnRouteReady;

    UFUNCTION(BlueprintCallable, Category="麻将|大厅")
    FGuiyangLobbyOperationResult RequestBootstrap(APlayerController* PlayerController);
    UFUNCTION(BlueprintCallable, Category="麻将|大厅")
    FGuiyangLobbyOperationResult RequestQuickStart(APlayerController* PlayerController);
    UFUNCTION(BlueprintCallable, Category="麻将|大厅")
    FGuiyangLobbyOperationResult RequestCreateRoom(
        APlayerController* PlayerController, const FMahjongCreateRoomRequest& Request);
    UFUNCTION(BlueprintCallable, Category="麻将|大厅")
    FGuiyangLobbyOperationResult RequestJoinRoom(
        APlayerController* PlayerController, const FMahjongJoinRoomRequest& Request);
    UFUNCTION(BlueprintCallable, Category="麻将|大厅")
    FGuiyangLobbyOperationResult RequestReconnect(APlayerController* PlayerController);
    UFUNCTION(BlueprintCallable, Category="麻将|大厅")
    FGuiyangLobbyOperationResult RequestLeaveCurrentRoom(APlayerController* PlayerController);
    UFUNCTION(BlueprintCallable, Category="麻将|大厅")
    FGuiyangLobbyOperationResult RequestCloseOwnedRoom(APlayerController* PlayerController);

    UFUNCTION(BlueprintPure, Category="麻将|大厅")
    EGuiyangLobbyBackendMode GetBackendMode() const { return BackendMode; }
    UFUNCTION(BlueprintPure, Category="麻将|大厅")
    bool IsRemoteLobbyReady() const { return BackendMode == EGuiyangLobbyBackendMode::RemoteLobby && Backend.IsValid(); }

    /** 仅解析稳定配置文本，不访问世界；供配置和契约自动化测试共同使用。 */
    static bool TryParseBackendMode(const FString& Value, EGuiyangLobbyBackendMode& OutMode);
    static const TCHAR* GetBackendModeName(EGuiyangLobbyBackendMode Mode);

    void HandleRemoteBootstrap(const FGuiyangLobbyBootstrap& Bootstrap);
    void HandleRemoteRouteReady(class AGuiyangMahjongPlayerController* PlayerController,
        const FGuiyangGameServerRoute& Route);
    void HandleRemoteFailure(const FString& RequestId, EGuiyangLobbyErrorCode ErrorCode,
        const FString& ChineseMessage);

private:
    EGuiyangLobbyBackendMode BackendMode = EGuiyangLobbyBackendMode::LocalLegacy;
    TSharedPtr<ILobbyBackend> Backend;

    FString MakeRequestId() const;
    FGuiyangLobbyOperationResult RejectRequest(
        const FString& RequestId, EGuiyangLobbyErrorCode ErrorCode, const FString& ChineseMessage);
    FGuiyangLobbyOperationResult FinalizeBackendResult(const FGuiyangLobbyOperationResult& Result);
};
