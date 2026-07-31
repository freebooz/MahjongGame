#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"

#include "Evidence/GuiyangActionEvidence.h"
#include "HAL/FileManager.h"
#include "Rules/GuiyangRuleSnapshot.h"
#include "Server/GuiyangGameServerBridge.h"
#include "Snapshot/GuiyangRuntimeRecoveryStore.h"
#include "Table/MahjongTableEngine.h"

#if WITH_DEV_AUTOMATION_TESTS

namespace GuiyangServerRecoveryTests
{
    /** 构造四个稳定座位；测试身份只用于内存引擎，不写日志或外部存储。 */
    TArray<FMahjongSeatInfo> MakeSeats()
    {
        TArray<FMahjongSeatInfo> Seats;
        for (int32 Index = 0; Index < 4; ++Index)
        {
            FMahjongSeatInfo Seat;
            Seat.SeatIndex = Index;
            Seat.PlayerId = FString::Printf(TEXT("recovery-player-%d"), Index);
            Seat.PlayerName = FString::Printf(TEXT("恢复玩家%d"), Index);
            Seat.bOccupied = true;
            Seats.Add(MoveTemp(Seat));
        }
        return Seats;
    }

    /** 构造跨 Epoch 测试所需的最小托管启动配置；Root 由测试独占并在结束时清理。 */
    FGuiyangGameServerLaunchConfig MakeConfig(const FString& Root, const int64 Epoch)
    {
        FGuiyangGameServerLaunchConfig Config;
        Config.RoomId = TEXT("11111111-1111-1111-1111-111111111111");
        Config.MatchId = TEXT("22222222-2222-2222-2222-222222222222");
        Config.ServerInstanceId = TEXT("33333333-3333-3333-3333-333333333333");
        Config.RoomEpoch = Epoch;
        Config.RecoveryDirectory = Root;
        Config.RuleSetVersion = TEXT("guiyang-zhuoji-v1");
        return Config;
    }
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangTableSnapshotRoundTripTest,
    "GuiyangMahjong.GameServer.Snapshot.RoundTripAndHash",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::ServerContext
        | EAutomationTestFlags::EngineFilter)

bool FGuiyangTableSnapshotRoundTripTest::RunTest(const FString& Parameters)
{
    UMahjongTableEngine* Original = NewObject<UMahjongTableEngine>();
    FString Error;
    TestTrue(TEXT("固定种子牌局应启动"), Original->StartRound(
        UGuiyangRuleSnapshotLibrary::CreateSnapshot(FMahjongRuleConfig()),
        GuiyangServerRecoveryTests::MakeSeats(), 0, 20260731, Error));
    FMahjongTableRecoveryState Exported;
    TestTrue(TEXT("完整权威状态应导出"), Original->ExportRecoveryState(Exported));
    const FString OriginalHash = FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(Exported);
    TestEqual(TEXT("状态哈希必须是 SHA-256"), OriginalHash.Len(), 64);

    UMahjongTableEngine* Restored = NewObject<UMahjongTableEngine>();
    TestTrue(TEXT("完整权威状态应恢复"), Restored->RestoreRecoveryState(Exported, Error));
    FMahjongTableRecoveryState RoundTripped;
    TestTrue(TEXT("恢复后状态应再次导出"), Restored->ExportRecoveryState(RoundTripped));
    TestEqual(TEXT("恢复前后完整状态哈希必须一致"),
        FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(RoundTripped), OriginalHash);

    FMahjongPrivatePlayerState DealerState;
    TestTrue(TEXT("恢复前的庄家私有状态应存在"), Original->GetPrivateState(0, DealerState));
    FMahjongActionRequest DeterministicAction;
    DeterministicAction.ClientActionId = TEXT("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    DeterministicAction.ClientSequence = 1;
    DeterministicAction.ExpectedStateVersion = Original->GetPublicState().StateSequence;
    DeterministicAction.RoundId = Original->GetPublicState().RoundId;
    DeterministicAction.TurnId = Original->GetPublicState().TurnId;
    DeterministicAction.Type = EMahjongActionType::Play;
    DeterministicAction.TargetTileId = DealerState.Hand.Tiles[0].UniqueId;
    // Play 在权威牌桌中有独立入口；测试必须走与 GameMode 完全相同的分派路径，
    // 否则会把“不支持的回合动作”误判为快照恢复不确定。
    TestTrue(TEXT("原实例应接受确定性动作"), Original->SubmitPlayTile(0, DeterministicAction).bSuccess);
    TestTrue(TEXT("恢复实例应接受相同确定性动作"), Restored->SubmitPlayTile(0, DeterministicAction).bSuccess);
    FMahjongTableRecoveryState OriginalAfterAction;
    FMahjongTableRecoveryState RestoredAfterAction;
    TestTrue(TEXT("原实例动作后状态应导出"), Original->ExportRecoveryState(OriginalAfterAction));
    TestTrue(TEXT("恢复实例动作后状态应导出"), Restored->ExportRecoveryState(RestoredAfterAction));
    TestEqual(TEXT("相同动作在恢复实例上必须产生相同完整状态哈希"),
        FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(RestoredAfterAction),
        FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(OriginalAfterAction));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangActionEvidenceChainTest,
    "GuiyangMahjong.GameServer.Evidence.HashChain",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::ServerContext
        | EAutomationTestFlags::EngineFilter)

bool FGuiyangActionEvidenceChainTest::RunTest(const FString& Parameters)
{
    FGuiyangActionEvidenceRecord First;
    First.MatchId = TEXT("22222222-2222-2222-2222-222222222222");
    First.RoomId = TEXT("11111111-1111-1111-1111-111111111111");
    First.RoomEpoch = 2;
    First.ActionSequence = 1;
    First.StateVersionBefore = 7;
    First.StateVersionAfter = 8;
    First.StateHashAfter = FString::ChrN(64, TEXT('a'));
    First.PlayerId = TEXT("recovery-player-0");
    First.SeatId = 0;
    First.ActionType = TEXT("1");
    First.OccurredAtUtc = TEXT("2026-07-31T00:00:00.000Z");
    First.Request.ClientActionId = TEXT("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    First.Request.ClientSequence = 1;
    First.NormalizedPayload = FGuiyangActionEvidence::NormalizeRequest(First.Request);
    First.ActionHash = FGuiyangActionEvidence::CalculateHash(First);
    TestEqual(TEXT("动作哈希必须是 SHA-256"), First.ActionHash.Len(), 64);

    FGuiyangActionEvidenceRecord Second = First;
    Second.ActionSequence = 2;
    Second.PreviousHash = First.ActionHash;
    Second.ActionHash = FGuiyangActionEvidence::CalculateHash(Second);
    TestNotEqual(TEXT("链式 previous_hash 必须改变后续摘要"), Second.ActionHash, First.ActionHash);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangCrossEpochSnapshotStoreTest,
    "GuiyangMahjong.GameServer.Snapshot.CrossEpochLoad",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::ServerContext
        | EAutomationTestFlags::EngineFilter)

bool FGuiyangCrossEpochSnapshotStoreTest::RunTest(const FString& Parameters)
{
    const FString Root = FPaths::ConvertRelativePathToFull(
        FPaths::ProjectSavedDir() / TEXT("Automation/Stage6Recovery"));
    IFileManager::Get().DeleteDirectory(*Root, false, true);
    FString Error;
    FGuiyangRuntimeRecoveryStore OldStore;
    TestTrue(TEXT("旧 Epoch 恢复仓库应初始化"), OldStore.Initialize(
        GuiyangServerRecoveryTests::MakeConfig(Root, 1), Error));
    FGuiyangAuthoritativeSnapshot Snapshot;
    Snapshot.MatchId = TEXT("22222222-2222-2222-2222-222222222222");
    Snapshot.RoomId = TEXT("11111111-1111-1111-1111-111111111111");
    Snapshot.RoomCode = TEXT("123456");
    Snapshot.RoomEpoch = 1;
    Snapshot.RuleSetVersion = TEXT("guiyang-zhuoji-v1");
    Snapshot.CreatedAtUtc = TEXT("2026-07-31T00:00:00.000Z");
    TestTrue(TEXT("旧 Epoch 快照应原子保存"), OldStore.SaveSnapshot(Snapshot, Error));

    FGuiyangRuntimeRecoveryStore NewStore;
    TestTrue(TEXT("新 Epoch 恢复仓库应初始化"), NewStore.Initialize(
        GuiyangServerRecoveryTests::MakeConfig(Root, 2), Error));
    FGuiyangAuthoritativeSnapshot Loaded;
    TArray<FGuiyangActionEvidenceRecord> Actions;
    TestTrue(TEXT("新 Epoch 应读取旧 Epoch 最新快照"),
        NewStore.LoadLatestPriorEpoch(Loaded, Actions, Error));
    TestEqual(TEXT("恢复来源必须是旧 Epoch"), Loaded.RoomEpoch, static_cast<int64>(1));
    IFileManager::Get().DeleteDirectory(*Root, false, true);
    return true;
}

#endif
