#pragma once

#include "CoreMinimal.h"
#include "GameFramework/PlayerState.h"
#include "Auth/GuiyangLoginTypes.h"
#include "GuiyangMahjongPlayerState.generated.h"

/**
 * 每条玩家连接的服务端认证、房间和座位状态。服务端 SessionId 不参与 Replication。
 */
UCLASS()
class GUIYANGMAHJONG_API AGuiyangMahjongPlayerState : public APlayerState
{
    GENERATED_BODY()

public:
    AGuiyangMahjongPlayerState();

    UPROPERTY(Replicated, BlueprintReadOnly, Category="麻将|账号") FString MahjongPlayerId;
    UPROPERTY(Replicated, BlueprintReadOnly, Category="麻将|账号") FString DisplayName;
    UPROPERTY(Replicated, BlueprintReadOnly, Category="麻将|账号") EGuiyangLoginProvider LoginProvider = EGuiyangLoginProvider::None;
    UPROPERTY(Replicated, BlueprintReadOnly, Category="麻将|房间") FString RoomCode;
    UPROPERTY(Replicated, BlueprintReadOnly, Category="麻将|房间") int32 SeatIndex = INDEX_NONE;
    UPROPERTY(Replicated, BlueprintReadOnly, Category="麻将|房间") bool bReady = false;
    UPROPERTY(Replicated, BlueprintReadOnly, Category="麻将|房间") bool bOnline = true;

    /** 仅由服务端 PlayerController 调用，为本连接创建短期会话。 */
    bool AuthenticateServer(const FString& InMahjongPlayerId, const FString& PlayerDisplayName, EGuiyangLoginProvider Provider);
    bool HasValidServerSession() const;
    void ClearServerSession();
    void EnterRoomServer(const FString& InRoomCode, int32 InSeatIndex, bool bIsReady = false);
    void LeaveRoomServer();

    /** 注册公开账号与房间字段的复制规则；ServerSessionId 等敏感会话字段必须保持服务端私有。 */
    virtual void GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const override;

private:
    FString ServerSessionId;
    FDateTime ServerSessionExpireAtUtc;
};
