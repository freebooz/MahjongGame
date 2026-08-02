#include "Game/GuiyangMahjongGameState.h"

#include "GuiyangMahjong.h"
#include "Net/UnrealNetwork.h"

AGuiyangMahjongGameState::AGuiyangMahjongGameState()
{
    bReplicates = true;
}

void AGuiyangMahjongGameState::SetPublicTableStateAuthority(const FMahjongPublicTableState& NewState)
{
    if (!HasAuthority())
    {
        UE_LOG(LogMahjongNet, Warning, TEXT("拒绝在客户端写入公共牌桌状态"));
        return;
    }

    PublicTableState = NewState;
    // StateSequence 属于牌桌引擎的并发控制版本，客户端会原样回传给动作 RPC。
    // 发布复制快照不能额外递增该值，否则客户端永远比引擎领先一版并被误判为过期请求。
    ForceNetUpdate();
    OnPublicTableStateUpdated.Broadcast(PublicTableState);
    UE_LOG(LogMahjongServer, Verbose, TEXT("服务端更新公共牌桌状态：序号=%d"), PublicTableState.StateSequence);
}

void AGuiyangMahjongGameState::OnRep_PublicTableState()
{
    UE_LOG(LogMahjongUI, Verbose, TEXT("客户端收到公共牌桌复制状态：序号=%d"), PublicTableState.StateSequence);
    OnPublicTableStateUpdated.Broadcast(PublicTableState);
}

void AGuiyangMahjongGameState::SetRoomStateAuthority(const FMahjongRoomState& NewState)
{
    if (!HasAuthority()) return;
    RoomState = NewState;
    ForceNetUpdate();
    OnRoomStateUpdated.Broadcast(RoomState);
}

void AGuiyangMahjongGameState::OnRep_RoomState()
{
    OnRoomStateUpdated.Broadcast(RoomState);
}

void AGuiyangMahjongGameState::GetLifetimeReplicatedProps(TArray<FLifetimeProperty>& OutLifetimeProps) const
{
    Super::GetLifetimeReplicatedProps(OutLifetimeProps);
    DOREPLIFETIME(AGuiyangMahjongGameState, PublicTableState);
    DOREPLIFETIME(AGuiyangMahjongGameState, RoomState);
}
