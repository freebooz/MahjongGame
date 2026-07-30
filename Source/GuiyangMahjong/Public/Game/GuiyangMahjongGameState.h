#pragma once

#include "CoreMinimal.h"
#include "GameFramework/GameStateBase.h"
#include "Network/MahjongNetworkTypes.h"
#include "GuiyangMahjongGameState.generated.h"

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongPublicTableStateUpdated, const FMahjongPublicTableState&, State);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongRoomStateUpdated, const FMahjongRoomState&, State);

/**
 * 牌桌公共状态复制源。这里只包含所有客户端都能看到的数据，严禁保存任何玩家私有手牌。
 */
UCLASS()
class GUIYANGMAHJONG_API AGuiyangMahjongGameState : public AGameStateBase
{
    GENERATED_BODY()

public:
    AGuiyangMahjongGameState();

    UPROPERTY(ReplicatedUsing=OnRep_PublicTableState, BlueprintReadOnly, Category="麻将|牌桌")
    FMahjongPublicTableState PublicTableState;

    UPROPERTY(ReplicatedUsing=OnRep_RoomState, BlueprintReadOnly, Category="麻将|房间")
    FMahjongRoomState RoomState;

    UPROPERTY(BlueprintAssignable, Category="麻将|UI")
    FMahjongPublicTableStateUpdated OnPublicTableStateUpdated;

    UPROPERTY(BlueprintAssignable, Category="麻将|UI")
    FMahjongRoomStateUpdated OnRoomStateUpdated;

    /** 仅供服务端牌桌流程写入权威快照。 */
    void SetPublicTableStateAuthority(const FMahjongPublicTableState& NewState);
    void SetRoomStateAuthority(const FMahjongRoomState& NewState);

    virtual void GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const override;

protected:
    /** 公共牌桌复制完成后广播不可变快照，驱动 HUD 刷新而不在客户端重算规则。 */
    UFUNCTION()
    void OnRep_PublicTableState();
    /** 房间复制完成后广播座位和生命周期变化，保持房间页与服务端权威状态一致。 */
    UFUNCTION()
    void OnRep_RoomState();
};
