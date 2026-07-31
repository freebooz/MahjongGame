#include "Room/GuiyangRoomManager.h"

#include "Room/GuiyangManagedRoomDefinition.h"
#include "HAL/PlatformTime.h"
#include "HAL/PlatformProcess.h"
#include "GuiyangMahjong.h"

#if !PLATFORM_WINDOWS
THIRD_PARTY_INCLUDES_START
#define UI OPENSSL_UI
#include <openssl/evp.h>
#undef UI
THIRD_PARTY_INCLUDES_END
#endif

bool UGuiyangRoomManager::CreateManagedRoom(const FGuiyangManagedRoomDefinition& Definition,
    FMahjongRoomState& OutState, EMahjongRoomError& OutError)
{
    // 托管房间的 ID、规则和房主全部来自已校验 Bootstrap，不接受客户端覆盖。
    OutError = EMahjongRoomError::None;
    if (Rooms.Num() != 0 || Definition.RoomCode.Len() != 6 || !Definition.RoomCode.IsNumeric()
        || Definition.RoundCount < 1 || Definition.RoundCount > 16 || Definition.MaximumPlayers != 4
        || Definition.OwnerPlayerId.IsEmpty()
        || !UGuiyangRuleSnapshotLibrary::VerifySnapshot(Definition.RuleSnapshot))
    {
        OutError = EMahjongRoomError::InvalidRequest;
        return false;
    }

    FRoomRecord Record;
    Record.bManagedAuthority = true;
    Record.PublicState.RoomInfo.MatchId = Definition.MatchId;
    Record.PublicState.RoomInfo.RoomId = Definition.RoomCode;
    Record.PublicState.RoomInfo.OwnerPlayerId = Definition.OwnerPlayerId;
    Record.PublicState.RoomInfo.RoundCount = Definition.RoundCount;
    Record.PublicState.RoomInfo.MaxPlayers = Definition.MaximumPlayers;
    Record.PublicState.RoomInfo.BaseScore = Definition.RuleSnapshot.Config.BaseScore;
    Record.PublicState.RoomInfo.bPublicRoom = Definition.bPublicRoom;
    Record.PublicState.RoomInfo.bAutoStart = Definition.bAutoStart;
    Record.PublicState.RoomInfo.bPasswordProtected = Definition.bPasswordProtected;
    Record.PublicState.RuleSnapshot = Definition.RuleSnapshot;
    Record.PublicState.RoomInfo.RuleSummary = FString::Printf(TEXT("%s · %d张 · %d局"),
        *Definition.RuleSnapshot.Config.RuleId.ToString(), Definition.RuleSnapshot.GetTileCount(), Definition.RoundCount);
    Record.PublicState.Lifecycle = EMahjongRoomLifecycle::WaitingForPlayers;
    Record.PublicState.Seats.SetNum(Definition.MaximumPlayers);
    for (int32 SeatIndex = 0; SeatIndex < Record.PublicState.Seats.Num(); ++SeatIndex)
        Record.PublicState.Seats[SeatIndex].SeatIndex = SeatIndex;
    ++Record.PublicState.StateSequence;
    Rooms.Add(Definition.RoomCode, MoveTemp(Record));
    OutState = Rooms[Definition.RoomCode].PublicState;
    return true;
}

bool UGuiyangRoomManager::AdmitManagedPlayer(const FString& RoomCode, const FString& PlayerId,
    const FString& DisplayName, FMahjongRoomState& OutState, EMahjongRoomError& OutError)
{
    // 单活动房间反向索引阻止同一玩家同时占用多个房间座位。
    OutError = EMahjongRoomError::None;
    if (!ValidateIdentity(PlayerId, DisplayName))
    {
        OutError = EMahjongRoomError::InvalidRequest;
        return false;
    }
    if (const FString* ExistingRoomCode = PlayerRoomCodes.Find(PlayerId))
    {
        if (*ExistingRoomCode != RoomCode)
        {
            OutError = EMahjongRoomError::AlreadyInRoom;
            return false;
        }
        FRoomRecord* ExistingRecord = Rooms.Find(RoomCode);
        FMahjongSeatInfo* ExistingSeat = ExistingRecord ? FindSeat(ExistingRecord->PublicState, PlayerId) : nullptr;
        if (!ExistingRecord || !ExistingRecord->bManagedAuthority || !ExistingSeat)
        {
            OutError = EMahjongRoomError::NotInRoom;
            return false;
        }
        ExistingSeat->bOnline = true;
        FGuiyangPlayerConnectionTelemetry& Connection =
            ExistingRecord->ConnectionTelemetryByPlayer.FindOrAdd(PlayerId);
        // 重复接纳只在确实从掉线恢复时推进序号，保证控制面幂等。
        if (Connection.bDisconnected)
        {
            const FDateTime Now = FDateTime::UtcNow();
            Connection.bDisconnected = false;
            Connection.ChangedAtUtc = Now;
            Connection.ReconnectedAtUtc = Now;
            Connection.DisconnectedAtUtc = FDateTime();
            Connection.DisconnectReason.Reset();
            ++Connection.Sequence;
            Connection.EventId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
        }
        ++ExistingRecord->PublicState.StateSequence;
        OutState = ExistingRecord->PublicState;
        return true;
    }

    FRoomRecord* Record = Rooms.Find(RoomCode);
    if (!Record || !Record->bManagedAuthority)
    {
        OutError = EMahjongRoomError::RoomNotFound;
        return false;
    }
    if (Record->PublicState.Lifecycle != EMahjongRoomLifecycle::WaitingForPlayers
        && Record->PublicState.Lifecycle != EMahjongRoomLifecycle::ReadyCheck)
    {
        OutError = EMahjongRoomError::GameAlreadyStarted;
        return false;
    }
    FMahjongSeatInfo* EmptySeat = Record->PublicState.Seats.FindByPredicate(
        [](const FMahjongSeatInfo& Seat) { return !Seat.bOccupied; });
    if (!EmptySeat)
    {
        OutError = EMahjongRoomError::RoomFull;
        return false;
    }
    EmptySeat->PlayerId = PlayerId;
    EmptySeat->PlayerName = DisplayName.TrimStartAndEnd();
    EmptySeat->bOwner = PlayerId == Record->PublicState.RoomInfo.OwnerPlayerId;
    EmptySeat->bOccupied = true;
    EmptySeat->bOnline = true;
    EmptySeat->bReady = false;
    PlayerRoomCodes.Add(PlayerId, RoomCode);
    FGuiyangPlayerConnectionTelemetry& Connection =
        Record->ConnectionTelemetryByPlayer.FindOrAdd(PlayerId);
    Connection.bDisconnected = false;
    Connection.ChangedAtUtc = FDateTime::UtcNow();
    Connection.Sequence = FMath::Max<int64>(1, Connection.Sequence + 1);
    Connection.EventId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    RefreshLifecycle(Record->PublicState);
    ++Record->PublicState.StateSequence;
    OutState = Record->PublicState;
    UE_LOG(LogMahjongServer, Log, TEXT("托管玩家入座：Room=%s，Player=%s，Seat=%d"),
        *RoomCode, *PlayerId, EmptySeat->SeatIndex);
    return true;
}

bool UGuiyangRoomManager::CreateRoom(const FString& PlayerId, const FString& DisplayName,
    const FMahjongCreateRoomRequest& Request, FMahjongRoomState& OutState, EMahjongRoomError& OutError)
{
    // 本地创建先完整校验身份、局数、规则和密码，再一次性插入两张索引表。
    OutError = EMahjongRoomError::None;
    if (!ValidateIdentity(PlayerId, DisplayName) || Request.RoundCount < 1 || Request.RoundCount > 16
        || (Request.bEnablePassword && !ValidatePassword(Request.Password)))
    {
        OutError = EMahjongRoomError::InvalidRequest;
        return false;
    }
    if (PlayerRoomCodes.Contains(PlayerId))
    {
        OutError = EMahjongRoomError::AlreadyInRoom;
        return false;
    }

    const FString RoomCode = GenerateUniqueRoomCode();
    if (RoomCode.IsEmpty())
    {
        OutError = EMahjongRoomError::InvalidRequest;
        return false;
    }

    FRoomRecord Record;
    Record.PublicState.RoomInfo.MatchId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphens);
    Record.PublicState.RoomInfo.RoomId = RoomCode;
    Record.PublicState.RoomInfo.OwnerPlayerId = PlayerId;
    Record.PublicState.RoomInfo.RoundCount = Request.RoundCount;
    Record.PublicState.RoomInfo.BaseScore = Request.Rules.BaseScore;
    Record.PublicState.RoomInfo.bPublicRoom = Request.bPublicRoom;
    Record.PublicState.RoomInfo.bAutoStart = Request.bAutoStart;
    Record.PublicState.RoomInfo.bPasswordProtected = Request.bEnablePassword;
    Record.PublicState.RuleSnapshot = UGuiyangRuleSnapshotLibrary::CreateSnapshot(Request.Rules);
    Record.PublicState.RoomInfo.BaseScore = Record.PublicState.RuleSnapshot.Config.BaseScore;
    Record.PublicState.RoomInfo.RuleSummary = FString::Printf(TEXT("%s · %d张 · %d局"),
        *Record.PublicState.RuleSnapshot.Config.RuleId.ToString(), Record.PublicState.RuleSnapshot.GetTileCount(), Request.RoundCount);
    Record.PublicState.Lifecycle = EMahjongRoomLifecycle::WaitingForPlayers;
    Record.PublicState.Seats.SetNum(4);
    for (int32 SeatIndex = 0; SeatIndex < Record.PublicState.Seats.Num(); ++SeatIndex)
    {
        Record.PublicState.Seats[SeatIndex].SeatIndex = SeatIndex;
    }
    FMahjongSeatInfo& OwnerSeat = Record.PublicState.Seats[0];
    OwnerSeat.PlayerId = PlayerId;
    OwnerSeat.PlayerName = DisplayName.TrimStartAndEnd();
    OwnerSeat.bOwner = true;
    OwnerSeat.bOccupied = true;
    OwnerSeat.bOnline = true;
    if (Request.bEnablePassword)
    {
        Record.PasswordSalt = MakePasswordSalt();
        Record.PasswordDigest = HashPassword(Request.Password, Record.PasswordSalt);
    }
    ++Record.PublicState.StateSequence;
    Rooms.Add(RoomCode, MoveTemp(Record));
    PlayerRoomCodes.Add(PlayerId, RoomCode);
    OutState = Rooms[RoomCode].PublicState;
    UE_LOG(LogMahjongServer, Log, TEXT("创建房间成功：Room=%s，Owner=%s，RuleHash=%s，密码保护=%s"),
        *RoomCode, *PlayerId, *OutState.RuleSnapshot.RuleHash, Request.bEnablePassword ? TEXT("是") : TEXT("否"));
    return true;
}

bool UGuiyangRoomManager::QuickStart(const FString& PlayerId, const FString& DisplayName,
    FMahjongRoomState& OutState, EMahjongRoomError& OutError)
{
    OutError = EMahjongRoomError::None;
    if (!ValidateIdentity(PlayerId, DisplayName))
    {
        OutError = EMahjongRoomError::InvalidRequest;
        return false;
    }
    if (PlayerRoomCodes.Contains(PlayerId))
    {
        OutError = EMahjongRoomError::AlreadyInRoom;
        return false;
    }

    TArray<FString> CandidateRoomCodes;
    Rooms.GetKeys(CandidateRoomCodes);
    CandidateRoomCodes.Sort();
    for (const FString& RoomCode : CandidateRoomCodes)
    {
        const FRoomRecord* Record = Rooms.Find(RoomCode);
        if (!Record || !Record->PublicState.RoomInfo.bPublicRoom
            || Record->PublicState.RoomInfo.bPasswordProtected
            || (Record->PublicState.Lifecycle != EMahjongRoomLifecycle::WaitingForPlayers
                && Record->PublicState.Lifecycle != EMahjongRoomLifecycle::ReadyCheck)
            || !Record->PublicState.Seats.ContainsByPredicate(
                [](const FMahjongSeatInfo& Seat) { return !Seat.bOccupied; }))
        {
            continue;
        }

        FMahjongJoinRoomRequest JoinRequest;
        JoinRequest.RoomCode = RoomCode;
        return JoinRoom(PlayerId, DisplayName, JoinRequest, OutState, OutError);
    }

    FMahjongCreateRoomRequest CreateRequest;
    CreateRequest.bPublicRoom = true;
    CreateRequest.bEnablePassword = false;
    return CreateRoom(PlayerId, DisplayName, CreateRequest, OutState, OutError);
}

bool UGuiyangRoomManager::JoinRoom(const FString& PlayerId, const FString& DisplayName,
    const FMahjongJoinRoomRequest& Request, FMahjongRoomState& OutState, EMahjongRoomError& OutError)
{
    // 密码验证失败会按玩家累计并短期锁定，成功后清除失败状态。
    OutError = EMahjongRoomError::None;
    const FString RoomCode = Request.RoomCode.TrimStartAndEnd();
    if (!ValidateIdentity(PlayerId, DisplayName) || RoomCode.Len() != 6 || !RoomCode.IsNumeric())
    {
        OutError = EMahjongRoomError::InvalidRequest;
        return false;
    }
    if (PlayerRoomCodes.Contains(PlayerId))
    {
        OutError = EMahjongRoomError::AlreadyInRoom;
        return false;
    }
    FRoomRecord* Record = Rooms.Find(RoomCode);
    if (!Record)
    {
        OutError = EMahjongRoomError::RoomNotFound;
        return false;
    }
    if (Record->PublicState.Lifecycle != EMahjongRoomLifecycle::WaitingForPlayers
        && Record->PublicState.Lifecycle != EMahjongRoomLifecycle::ReadyCheck)
    {
        OutError = EMahjongRoomError::GameAlreadyStarted;
        return false;
    }

    if (Record->PublicState.RoomInfo.bPasswordProtected)
    {
        FPasswordAttemptState& Attempts = Record->PasswordAttemptsByPlayer.FindOrAdd(PlayerId);
        const FDateTime Now = FDateTime::UtcNow();
        if (Attempts.LockedUntilUtc > Now)
        {
            OutError = EMahjongRoomError::TooManyPasswordAttempts;
            return false;
        }
        if (Request.Password.IsEmpty())
        {
            OutError = EMahjongRoomError::PasswordRequired;
            return false;
        }
        const FString CandidateDigest = HashPassword(Request.Password, Record->PasswordSalt);
        if (!ConstantTimeEquals(CandidateDigest, Record->PasswordDigest))
        {
            ++Attempts.FailureCount;
            if (Attempts.FailureCount >= MaxPasswordFailures)
            {
                Attempts.FailureCount = 0;
                Attempts.LockedUntilUtc = Now + FTimespan::FromSeconds(PasswordLockSeconds);
                OutError = EMahjongRoomError::TooManyPasswordAttempts;
            }
            else
            {
                OutError = EMahjongRoomError::WrongPassword;
            }
            return false;
        }
        Record->PasswordAttemptsByPlayer.Remove(PlayerId);
    }

    FMahjongSeatInfo* EmptySeat = Record->PublicState.Seats.FindByPredicate(
        [](const FMahjongSeatInfo& Seat) { return !Seat.bOccupied; });
    if (!EmptySeat)
    {
        OutError = EMahjongRoomError::RoomFull;
        return false;
    }
    EmptySeat->PlayerId = PlayerId;
    EmptySeat->PlayerName = DisplayName.TrimStartAndEnd();
    EmptySeat->bOccupied = true;
    EmptySeat->bOnline = true;
    EmptySeat->bReady = false;
    PlayerRoomCodes.Add(PlayerId, RoomCode);
    RefreshLifecycle(Record->PublicState);
    ++Record->PublicState.StateSequence;
    OutState = Record->PublicState;
    UE_LOG(LogMahjongServer, Log, TEXT("加入房间成功：Room=%s，Player=%s，Seat=%d"), *RoomCode, *PlayerId, EmptySeat->SeatIndex);
    return true;
}

bool UGuiyangRoomManager::ToggleReady(const FString& PlayerId, FMahjongRoomState& OutState, EMahjongRoomError& OutError)
{
    // 只允许等待阶段切换准备；每次变更递增 StateSequence。
    OutError = EMahjongRoomError::None;
    const FString* RoomCode = PlayerRoomCodes.Find(PlayerId);
    FRoomRecord* Record = RoomCode ? Rooms.Find(*RoomCode) : nullptr;
    if (!Record)
    {
        OutError = EMahjongRoomError::NotInRoom;
        return false;
    }
    if (Record->PublicState.Lifecycle != EMahjongRoomLifecycle::WaitingForPlayers
        && Record->PublicState.Lifecycle != EMahjongRoomLifecycle::ReadyCheck)
    {
        OutError = EMahjongRoomError::GameAlreadyStarted;
        return false;
    }
    FMahjongSeatInfo* Seat = FindSeat(Record->PublicState, PlayerId);
    if (!Seat)
    {
        OutError = EMahjongRoomError::NotInRoom;
        return false;
    }
    Seat->bReady = !Seat->bReady;
    RefreshLifecycle(Record->PublicState);
    ++Record->PublicState.StateSequence;
    OutState = Record->PublicState;
    return true;
}

bool UGuiyangRoomManager::LeaveRoom(const FString& PlayerId, FMahjongRoomState& OutState, EMahjongRoomError& OutError)
{
    // 移除座位与玩家反向索引；非托管房间房主离开时按策略转移或关闭房间。
    OutError = EMahjongRoomError::None;
    const FString* RoomCodePtr = PlayerRoomCodes.Find(PlayerId);
    if (!RoomCodePtr)
    {
        OutError = EMahjongRoomError::NotInRoom;
        return false;
    }
    const FString RoomCode = *RoomCodePtr;
    FRoomRecord* Record = Rooms.Find(RoomCode);
    if (!Record)
    {
        PlayerRoomCodes.Remove(PlayerId);
        OutError = EMahjongRoomError::RoomNotFound;
        return false;
    }
    const bool bWasActiveRound =
        Record->PublicState.Lifecycle == EMahjongRoomLifecycle::Starting
        || Record->PublicState.Lifecycle == EMahjongRoomLifecycle::Playing
        || Record->PublicState.Lifecycle == EMahjongRoomLifecycle::Settlement
        || Record->PublicState.Lifecycle == EMahjongRoomLifecycle::WaitingNextRound;
    FMahjongSeatInfo* Seat = FindSeat(Record->PublicState, PlayerId);
    if (!Seat)
    {
        OutError = EMahjongRoomError::NotInRoom;
        return false;
    }
    const bool bWasOwner = Seat->bOwner;
    const int32 SeatIndex = Seat->SeatIndex;
    *Seat = FMahjongSeatInfo();
    Seat->SeatIndex = SeatIndex;
    PlayerRoomCodes.Remove(PlayerId);
    Record->ConnectionTelemetryByPlayer.Remove(PlayerId);

    // An explicit departure is different from a temporary disconnect. A
    // four-player round cannot remain authoritative after one of its seats is
    // removed, so return the surviving seats to an unready waiting state. The
    // game mode clears the active table snapshot in the same transaction.
    if (bWasActiveRound)
    {
        for (FMahjongSeatInfo& RemainingSeat : Record->PublicState.Seats)
        {
            if (RemainingSeat.bOccupied)
            {
                RemainingSeat.bReady = false;
            }
        }
        Record->PublicState.bGameStarting = false;
    }

    int32 OccupiedCount = 0;
    for (const FMahjongSeatInfo& Item : Record->PublicState.Seats)
    {
        OccupiedCount += Item.bOccupied ? 1 : 0;
    }
    if (OccupiedCount == 0)
    {
        if (Record->bManagedAuthority)
        {
            Record->PublicState.Lifecycle = EMahjongRoomLifecycle::WaitingForPlayers;
            Record->PublicState.bGameStarting = false;
            ++Record->PublicState.StateSequence;
            OutState = Record->PublicState;
            UE_LOG(LogMahjongServer, Log, TEXT("托管房间已清空并保留：Room=%s"), *RoomCode);
            return true;
        }
        OutState = Record->PublicState;
        OutState.Lifecycle = EMahjongRoomLifecycle::Closed;
        ++OutState.StateSequence;
        Rooms.Remove(RoomCode);
        UE_LOG(LogMahjongServer, Log, TEXT("最后一名玩家离开，房间销毁：Room=%s"), *RoomCode);
        return true;
    }
    if (bWasOwner && !Record->bManagedAuthority)
    {
        FMahjongSeatInfo* NewOwner = Record->PublicState.Seats.FindByPredicate(
            [](const FMahjongSeatInfo& Item) { return Item.bOccupied; });
        check(NewOwner);
        NewOwner->bOwner = true;
        Record->PublicState.RoomInfo.OwnerPlayerId = NewOwner->PlayerId;
    }
    RefreshLifecycle(Record->PublicState);
    ++Record->PublicState.StateSequence;
    OutState = Record->PublicState;
    return true;
}

bool UGuiyangRoomManager::GetRoomState(const FString& RoomCode, FMahjongRoomState& OutState) const
{
    const FRoomRecord* Record = Rooms.Find(RoomCode);
    if (!Record) return false;
    OutState = Record->PublicState;
    return true;
}

bool UGuiyangRoomManager::BeginPlaying(const FString& RoomCode, FMahjongRoomState& OutState, EMahjongRoomError& OutError)
{
    OutError = EMahjongRoomError::None;
    FRoomRecord* Record = Rooms.Find(RoomCode);
    if (!Record)
    {
        OutError = EMahjongRoomError::RoomNotFound;
        return false;
    }
    if (Record->PublicState.Lifecycle != EMahjongRoomLifecycle::Starting
        || !UGuiyangRuleSnapshotLibrary::VerifySnapshot(Record->PublicState.RuleSnapshot))
    {
        OutError = EMahjongRoomError::InvalidRequest;
        return false;
    }
    Record->PublicState.Lifecycle = EMahjongRoomLifecycle::Playing;
    Record->PublicState.bGameStarting = false;
    ++Record->PublicState.RoomInfo.CurrentRound;
    ++Record->PublicState.StateSequence;
    OutState = Record->PublicState;
    return true;
}

bool UGuiyangRoomManager::FinishRound(const FString& RoomCode, const FMahjongSettlementResult& Settlement,
    FMahjongRoomState& OutState, EMahjongRoomError& OutError)
{
    // 逐座位应用结算总分后推进局数，达到上限时进入 Closed 并生成最终排名。
    OutError = EMahjongRoomError::None;
    FRoomRecord* Record = Rooms.Find(RoomCode);
    if (!Record)
    {
        OutError = EMahjongRoomError::RoomNotFound;
        return false;
    }
    if (Record->PublicState.Lifecycle != EMahjongRoomLifecycle::Playing || Settlement.PlayerResults.Num() != 4)
    {
        OutError = EMahjongRoomError::InvalidRequest;
        return false;
    }

    TSet<int32> UpdatedSeats;
    int32 TotalDelta = 0;
    for (const FMahjongPlayerScoreResult& Result : Settlement.PlayerResults)
    {
        if (!Record->PublicState.Seats.IsValidIndex(Result.SeatIndex) || UpdatedSeats.Contains(Result.SeatIndex))
        {
            OutError = EMahjongRoomError::InvalidRequest;
            return false;
        }
        UpdatedSeats.Add(Result.SeatIndex);
        TotalDelta += Result.TotalDelta;
    }
    if (TotalDelta != 0)
    {
        OutError = EMahjongRoomError::InvalidRequest;
        return false;
    }

    for (const FMahjongPlayerScoreResult& Result : Settlement.PlayerResults)
        Record->PublicState.Seats[Result.SeatIndex].Score += Result.TotalDelta;

    if (Settlement.bDrawGame)
    {
        if (!Record->PublicState.RuleSnapshot.Config.bDrawGameDealerContinues)
            Record->PublicState.RoomInfo.DealerSeat = (Record->PublicState.RoomInfo.DealerSeat + 1) % 4;
    }
    else if (Record->PublicState.Seats.IsValidIndex(Settlement.WinnerSeat))
    {
        Record->PublicState.RoomInfo.DealerSeat = Settlement.WinnerSeat;
    }

    for (FMahjongSeatInfo& Seat : Record->PublicState.Seats) Seat.bReady = false;
    Record->PublicState.bGameStarting = false;
    Record->PublicState.Lifecycle = Record->PublicState.RoomInfo.CurrentRound >= Record->PublicState.RoomInfo.RoundCount
        ? EMahjongRoomLifecycle::Settlement : EMahjongRoomLifecycle::WaitingNextRound;
    ++Record->PublicState.StateSequence;
    OutState = Record->PublicState;
    UE_LOG(LogMahjongServer, Log, TEXT("单局分数已回写：Room=%s，Round=%d/%d，Dealer=%d，Lifecycle=%d"),
        *RoomCode, OutState.RoomInfo.CurrentRound, OutState.RoomInfo.RoundCount,
        OutState.RoomInfo.DealerSeat, static_cast<int32>(OutState.Lifecycle));
    return true;
}

bool UGuiyangRoomManager::RequestNextRound(const FString& PlayerId, FMahjongRoomState& OutState,
    EMahjongRoomError& OutError)
{
    OutError = EMahjongRoomError::None;
    const FString* RoomCode = PlayerRoomCodes.Find(PlayerId);
    FRoomRecord* Record = RoomCode ? Rooms.Find(*RoomCode) : nullptr;
    if (!Record)
    {
        OutError = EMahjongRoomError::NotInRoom;
        return false;
    }
    if (Record->PublicState.Lifecycle != EMahjongRoomLifecycle::WaitingNextRound)
    {
        OutError = EMahjongRoomError::InvalidRequest;
        return false;
    }
    FMahjongSeatInfo* Seat = FindSeat(Record->PublicState, PlayerId);
    if (!Seat)
    {
        OutError = EMahjongRoomError::NotInRoom;
        return false;
    }
    Seat->bReady = true;

    const bool bAllReady = Record->PublicState.Seats.Num() == 4
        && Record->PublicState.Seats.ContainsByPredicate([](const FMahjongSeatInfo& Item)
        {
            return !Item.bOccupied || !Item.bReady;
        }) == false;
    if (bAllReady)
    {
        Record->PublicState.Lifecycle = EMahjongRoomLifecycle::Starting;
        Record->PublicState.bGameStarting = true;
    }
    ++Record->PublicState.StateSequence;
    OutState = Record->PublicState;
    return true;
}

bool UGuiyangRoomManager::MarkDisconnected(const FString& PlayerId, FMahjongRoomState& OutState,
    EMahjongRoomError& OutError, const FString& Reason)
{
    // 保留座位和玩家反向索引，并记录受控原因、序号和幂等事件供宽限期重连与监控。
    OutError = EMahjongRoomError::None;
    const FString* RoomCode = PlayerRoomCodes.Find(PlayerId);
    FRoomRecord* Record = RoomCode ? Rooms.Find(*RoomCode) : nullptr;
    if (!Record)
    {
        OutError = EMahjongRoomError::NotInRoom;
        return false;
    }
    FMahjongSeatInfo* Seat = FindSeat(Record->PublicState, PlayerId);
    if (!Seat)
    {
        OutError = EMahjongRoomError::NotInRoom;
        return false;
    }
    static const TSet<FString> AllowedReasons = {
        TEXT("NormalExit"), TEXT("NetworkInterrupted"), TEXT("ReconnectTimeout"),
        TEXT("Kicked"), TEXT("ServerShutdown")
    };
    if (!AllowedReasons.Contains(Reason))
    {
        OutError = EMahjongRoomError::InvalidRequest;
        return false;
    }
    FGuiyangPlayerConnectionTelemetry& Connection =
        Record->ConnectionTelemetryByPlayer.FindOrAdd(PlayerId);
    if (Connection.bDisconnected)
    {
        // 网络层可能重复通知同一次断线；返回当前状态但不生成重复事件。
        OutState = Record->PublicState;
        return true;
    }
    const FDateTime Now = FDateTime::UtcNow();
    Seat->bOnline = false;
    Seat->bReady = false;
    Connection.bDisconnected = true;
    Connection.ChangedAtUtc = Now;
    Connection.DisconnectedAtUtc = Now;
    Connection.DisconnectReason = Reason;
    ++Connection.Sequence;
    Connection.EventId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    ++Record->PublicState.StateSequence;
    OutState = Record->PublicState;
    UE_LOG(LogMahjongReconnect, Log, TEXT("玩家断线，座位已保留：Room=%s，Player=%s，Seat=%d"),
        **RoomCode, *PlayerId, Seat->SeatIndex);
    return true;
}

bool UGuiyangRoomManager::ReconnectPlayer(const FString& PlayerId, FMahjongRoomState& OutState,
    int32& OutRemainingSeconds, EMahjongRoomError& OutError)
{
    // 超过规则快照中的重连宽限期后拒绝恢复，避免无限占用座位。
    OutError = EMahjongRoomError::None;
    OutRemainingSeconds = 0;
    const FString* RoomCode = PlayerRoomCodes.Find(PlayerId);
    FRoomRecord* Record = RoomCode ? Rooms.Find(*RoomCode) : nullptr;
    if (!Record)
    {
        OutError = EMahjongRoomError::NotInRoom;
        return false;
    }
    FMahjongSeatInfo* Seat = FindSeat(Record->PublicState, PlayerId);
    if (!Seat)
    {
        OutError = EMahjongRoomError::NotInRoom;
        return false;
    }

    const int32 TimeoutSeconds = Record->PublicState.RuleSnapshot.Config.ReconnectTimeoutSeconds;
    const FGuiyangPlayerConnectionTelemetry* ExistingConnection =
        Record->ConnectionTelemetryByPlayer.Find(PlayerId);
    if (ExistingConnection && ExistingConnection->bDisconnected)
    {
        const int32 ElapsedSeconds = FMath::Max(0, FMath::FloorToInt(
            (FDateTime::UtcNow() - ExistingConnection->DisconnectedAtUtc).GetTotalSeconds()));
        OutRemainingSeconds = FMath::Max(0, TimeoutSeconds - ElapsedSeconds);
        if (ElapsedSeconds > TimeoutSeconds)
        {
            OutError = EMahjongRoomError::SessionExpired;
            return false;
        }
    }
    else
    {
        OutRemainingSeconds = TimeoutSeconds;
    }

    Seat->bOnline = true;
    FGuiyangPlayerConnectionTelemetry& Connection =
        Record->ConnectionTelemetryByPlayer.FindOrAdd(PlayerId);
    const FDateTime Now = FDateTime::UtcNow();
    Connection.bDisconnected = false;
    Connection.ChangedAtUtc = Now;
    Connection.ReconnectedAtUtc = Now;
    Connection.DisconnectedAtUtc = FDateTime();
    Connection.DisconnectReason.Reset();
    ++Connection.Sequence;
    Connection.EventId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    ++Record->PublicState.StateSequence;
    OutState = Record->PublicState;
    UE_LOG(LogMahjongReconnect, Log, TEXT("玩家重连并恢复座位：Room=%s，Player=%s，Seat=%d"),
        **RoomCode, *PlayerId, Seat->SeatIndex);
    return true;
}

bool UGuiyangRoomManager::GetPlayerRoomCode(const FString& PlayerId, FString& OutRoomCode) const
{
    const FString* RoomCode = PlayerRoomCodes.Find(PlayerId);
    if (!RoomCode) return false;
    OutRoomCode = *RoomCode;
    return true;
}

bool UGuiyangRoomManager::GetPlayerConnectionTelemetry(
    const FString& PlayerId, FGuiyangPlayerConnectionTelemetry& OutTelemetry) const
{
    const FString* RoomCode = PlayerRoomCodes.Find(PlayerId);
    const FRoomRecord* Record = RoomCode ? Rooms.Find(*RoomCode) : nullptr;
    const FGuiyangPlayerConnectionTelemetry* Telemetry =
        Record ? Record->ConnectionTelemetryByPlayer.Find(PlayerId) : nullptr;
    if (!Telemetry) return false;
    OutTelemetry = *Telemetry;
    return true;
}

FMahjongFinalSettlementResult UGuiyangRoomManager::BuildFinalSettlement(const FMahjongRoomState& State)
{
    // 按总分降序、座位升序稳定排名，保证同分结果在各端一致。
    FMahjongFinalSettlementResult Result;
    Result.MatchId = State.RoomInfo.MatchId;
    Result.RoomId = State.RoomInfo.RoomId;
    Result.CompletedRounds = State.RoomInfo.CurrentRound;
    TArray<FMahjongSeatInfo> RankedSeats = State.Seats.FilterByPredicate(
        [](const FMahjongSeatInfo& Seat) { return Seat.bOccupied; });
    RankedSeats.Sort([](const FMahjongSeatInfo& Left, const FMahjongSeatInfo& Right)
    {
        if (Left.Score != Right.Score) return Left.Score > Right.Score;
        return Left.SeatIndex < Right.SeatIndex;
    });
    for (int32 Index = 0; Index < RankedSeats.Num(); ++Index)
    {
        FMahjongFinalPlayerResult Player;
        Player.PlayerId = RankedSeats[Index].PlayerId;
        Player.Rank = Index + 1;
        Player.SeatIndex = RankedSeats[Index].SeatIndex;
        Player.PlayerName = RankedSeats[Index].PlayerName;
        Player.TotalScore = RankedSeats[Index].Score;
        Result.Players.Add(MoveTemp(Player));
    }
    return Result;
}

FString UGuiyangRoomManager::GenerateUniqueRoomCode()
{
    if (!bRandomInitialized)
    {
        RoomCodeRandom.Initialize(static_cast<int32>(FPlatformTime::Cycles64()) ^ GetTypeHash(FGuid::NewGuid()));
        bRandomInitialized = true;
    }
    for (int32 Attempt = 0; Attempt < 128; ++Attempt)
    {
        const FString Candidate = FString::Printf(TEXT("%06d"), RoomCodeRandom.RandRange(0, 999999));
        if (!Rooms.Contains(Candidate)) return Candidate;
    }
    return FString();
}

bool UGuiyangRoomManager::ValidateIdentity(const FString& PlayerId, const FString& DisplayName)
{
    const FString CleanId = PlayerId.TrimStartAndEnd();
    const FString CleanName = DisplayName.TrimStartAndEnd();
    return !CleanId.IsEmpty() && CleanId.Len() <= 80 && !CleanName.IsEmpty() && CleanName.Len() <= 24;
}

bool UGuiyangRoomManager::ValidatePassword(const FString& Password)
{
    if (Password.Len() < 6 || Password.Len() > 12) return false;
    for (const TCHAR Character : Password)
    {
        if (FChar::IsWhitespace(Character) || FChar::IsControl(Character)) return false;
    }
    return true;
}

FString UGuiyangRoomManager::MakePasswordSalt()
{
    return FGuid::NewGuid().ToString(EGuidFormats::Digits) + FGuid::NewGuid().ToString(EGuidFormats::Digits);
}

FString UGuiyangRoomManager::HashPassword(const FString& Password, const FString& Salt)
{
    // 多轮 SHA-256 派生仅存储摘要；原始密码不会进入房间记录或日志。
    FTCHARToUTF8 PasswordUtf8(*Password);
    FTCHARToUTF8 SaltUtf8(*Salt);
    uint8 DerivedKey[32] = {};
#if PLATFORM_WINDOWS
    // 动态调用系统 CNG，避免把 Windows SDK 全量头文件引入 Unreal UObject 编译单元。
    using FOpenAlgorithm = int32(*)(void**, const TCHAR*, const TCHAR*, uint32);
    using FDerivePbkdf2 = int32(*)(void*, uint8*, uint32, uint8*, uint32, uint64, uint8*, uint32, uint32);
    using FCloseAlgorithm = int32(*)(void*, uint32);
    void* BcryptDll = FPlatformProcess::GetDllHandle(TEXT("bcrypt.dll"));
    if (!BcryptDll) return FString();
    const FOpenAlgorithm OpenAlgorithm = reinterpret_cast<FOpenAlgorithm>(FPlatformProcess::GetDllExport(BcryptDll, TEXT("BCryptOpenAlgorithmProvider")));
    const FDerivePbkdf2 DerivePbkdf2 = reinterpret_cast<FDerivePbkdf2>(FPlatformProcess::GetDllExport(BcryptDll, TEXT("BCryptDeriveKeyPBKDF2")));
    const FCloseAlgorithm CloseAlgorithm = reinterpret_cast<FCloseAlgorithm>(FPlatformProcess::GetDllExport(BcryptDll, TEXT("BCryptCloseAlgorithmProvider")));
    if (!OpenAlgorithm || !DerivePbkdf2 || !CloseAlgorithm)
    {
        FPlatformProcess::FreeDllHandle(BcryptDll);
        return FString();
    }
    void* Algorithm = nullptr;
    constexpr uint32 HmacFlag = 0x00000008;
    const int32 OpenStatus = OpenAlgorithm(&Algorithm, TEXT("SHA256"), nullptr, HmacFlag);
    if (OpenStatus < 0)
    {
        FPlatformProcess::FreeDllHandle(BcryptDll);
        return FString();
    }
    const int32 DeriveStatus = DerivePbkdf2(
        Algorithm,
        reinterpret_cast<uint8*>(const_cast<ANSICHAR*>(PasswordUtf8.Get())), PasswordUtf8.Length(),
        reinterpret_cast<uint8*>(const_cast<ANSICHAR*>(SaltUtf8.Get())), SaltUtf8.Length(),
        PasswordHashRounds, DerivedKey, UE_ARRAY_COUNT(DerivedKey), 0);
    CloseAlgorithm(Algorithm, 0);
    FPlatformProcess::FreeDllHandle(BcryptDll);
    const bool bSucceeded = DeriveStatus >= 0;
#else
    const int32 Result = PKCS5_PBKDF2_HMAC(
        PasswordUtf8.Get(), PasswordUtf8.Length(),
        reinterpret_cast<const uint8*>(SaltUtf8.Get()), SaltUtf8.Length(),
        PasswordHashRounds, EVP_sha256(), UE_ARRAY_COUNT(DerivedKey), DerivedKey);
    const bool bSucceeded = Result == 1;
#endif
    return bSucceeded ? BytesToHex(DerivedKey, UE_ARRAY_COUNT(DerivedKey)).ToLower() : FString();
}

bool UGuiyangRoomManager::ConstantTimeEquals(const FString& Left, const FString& Right)
{
    uint32 Difference = static_cast<uint32>(Left.Len() ^ Right.Len());
    const int32 Count = FMath::Max(Left.Len(), Right.Len());
    for (int32 Index = 0; Index < Count; ++Index)
    {
        const TCHAR LeftChar = Left.IsValidIndex(Index) ? Left[Index] : 0;
        const TCHAR RightChar = Right.IsValidIndex(Index) ? Right[Index] : 0;
        Difference |= static_cast<uint32>(LeftChar ^ RightChar);
    }
    return Difference == 0;
}

FMahjongSeatInfo* UGuiyangRoomManager::FindSeat(FMahjongRoomState& State, const FString& PlayerId)
{
    return State.Seats.FindByPredicate([&PlayerId](const FMahjongSeatInfo& Seat) { return Seat.bOccupied && Seat.PlayerId == PlayerId; });
}

const FMahjongSeatInfo* UGuiyangRoomManager::FindSeat(const FMahjongRoomState& State, const FString& PlayerId)
{
    return State.Seats.FindByPredicate([&PlayerId](const FMahjongSeatInfo& Seat) { return Seat.bOccupied && Seat.PlayerId == PlayerId; });
}

void UGuiyangRoomManager::RefreshLifecycle(FMahjongRoomState& State)
{
    // 生命周期完全由公开座位状态和房间配置推导，调用方不能任意指定。
    int32 Occupied = 0;
    int32 Ready = 0;
    for (const FMahjongSeatInfo& Seat : State.Seats)
    {
        Occupied += Seat.bOccupied ? 1 : 0;
        Ready += Seat.bOccupied && Seat.bReady ? 1 : 0;
    }
    State.bGameStarting = Occupied == 4 && Ready == 4;
    State.Lifecycle = State.bGameStarting ? EMahjongRoomLifecycle::Starting
        : Occupied > 1 ? EMahjongRoomLifecycle::ReadyCheck : EMahjongRoomLifecycle::WaitingForPlayers;
}
