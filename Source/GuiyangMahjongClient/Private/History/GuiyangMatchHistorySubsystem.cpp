#include "History/GuiyangMatchHistorySubsystem.h"

#include "History/MahjongMatchHistorySaveGame.h"
#include "Kismet/GameplayStatics.h"

void UGuiyangMatchHistorySubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
    Super::Initialize(Collection);
    // 优先恢复持久化记录；首次启动或存档损坏时创建空存档对象。
    if (UGameplayStatics::DoesSaveGameExist(SaveSlotName, 0))
        HistorySave = Cast<UMahjongMatchHistorySaveGame>(UGameplayStatics::LoadGameFromSlot(SaveSlotName, 0));
    if (!HistorySave)
        HistorySave = Cast<UMahjongMatchHistorySaveGame>(
            UGameplayStatics::CreateSaveGameObject(UMahjongMatchHistorySaveGame::StaticClass()));
}

void UGuiyangMatchHistorySubsystem::Deinitialize()
{
    HistorySave = nullptr;
    Super::Deinitialize();
}

bool UGuiyangMatchHistorySubsystem::RecordFinalSettlement(const FMahjongFinalSettlementResult& Result)
{
    // MatchId 是本地幂等键，避免重连后重复写入同一场结算。
    if (!HistorySave || Result.MatchId.IsEmpty() || Result.Players.IsEmpty()) return false;
    if (HistorySave->Records.ContainsByPredicate([&Result](const FMahjongMatchHistoryRecord& Record)
    {
        return Record.MatchId == Result.MatchId;
    })) return false;

    // 保存旧数组；磁盘写入失败时回滚内存，保持观察者看到的一致状态。
    const TArray<FMahjongMatchHistoryRecord> PreviousRecords = HistorySave->Records;
    FMahjongMatchHistoryRecord Record;
    Record.MatchId = Result.MatchId;
    Record.RecordedAtUtc = FDateTime::UtcNow().ToIso8601();
    Record.FinalResult = Result;
    HistorySave->Records.Insert(MoveTemp(Record), 0);
    // 新记录置顶，并限制本地存档体积。
    if (HistorySave->Records.Num() > MaxHistoryRecords)
        HistorySave->Records.SetNum(MaxHistoryRecords);
    if (!SaveHistory())
    {
        HistorySave->Records = PreviousRecords;
        return false;
    }
    OnHistoryChanged.Broadcast();
    return true;
}

TArray<FMahjongMatchHistoryRecord> UGuiyangMatchHistorySubsystem::GetRecords() const
{
    return HistorySave ? HistorySave->Records : TArray<FMahjongMatchHistoryRecord>();
}

void UGuiyangMatchHistorySubsystem::ClearHistory()
{
    if (!HistorySave) return;
    HistorySave->Records.Reset();
    SaveHistory();
    OnHistoryChanged.Broadcast();
}

bool UGuiyangMatchHistorySubsystem::SaveHistory() const
{
    return HistorySave && UGameplayStatics::SaveGameToSlot(HistorySave, SaveSlotName, 0);
}
