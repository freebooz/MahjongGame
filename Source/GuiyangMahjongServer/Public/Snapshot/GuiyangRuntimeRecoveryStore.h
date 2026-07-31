#pragma once

#include "CoreMinimal.h"
#include "Evidence/GuiyangActionEvidence.h"
#include "Network/MahjongNetworkTypes.h"
#include "Server/GuiyangFairShuffle.h"
#include "GuiyangRuntimeRecoveryStore.generated.h"

struct FGuiyangGameServerLaunchConfig;

/**
 * 一次可原子恢复的完整权威快照。
 * TableState 含完整手牌和牌墙，只能写入受限恢复卷；StateHash 用于读取后拒绝静默损坏。
 */
USTRUCT()
struct GUIYANGMAHJONGSERVER_API FGuiyangAuthoritativeSnapshot
{
    GENERATED_BODY()

    UPROPERTY() FString MatchId;
    UPROPERTY() FString RoomId;
    UPROPERTY() FString RoomCode;
    UPROPERTY() int64 RoomEpoch = 0;
    UPROPERTY() int32 SnapshotVersion = 1;
    UPROPERTY() int64 ActionSequence = 0;
    UPROPERTY() int32 StateVersion = 0;
    UPROPERTY() FString RuleSetVersion;
    UPROPERTY() FString RandomState;
    /** 当前未披露证明与既往已披露证明只存在受限快照，用于崩溃后继续完成审计链。 */
    UPROPERTY() bool bHasPendingShuffleProof = false;
    UPROPERTY() FGuiyangShuffleAuditProof PendingShuffleProof;
    UPROPERTY() TArray<FGuiyangShuffleAuditProof> CompletedShuffleProofs;
    UPROPERTY() FString FairnessEventChainDigest;
    UPROPERTY() FMahjongRoomState RoomState;
    UPROPERTY() FMahjongTableRecoveryState TableState;
    UPROPERTY() TArray<int32> TrusteeSeats;
    UPROPERTY() FString PreviousActionHash;
    UPROPERTY() FString StateHash;
    UPROPERTY() FString CreatedAtUtc;
};

/**
 * 按 Match 隔离的本地/共享卷恢复仓库。
 * 写入采用临时文件加原子替换，动作采用追加 JSONL；不同 Epoch 使用不同文件，旧实例不能覆盖新实例。
 */
class GUIYANGMAHJONGSERVER_API FGuiyangRuntimeRecoveryStore final
{
public:
    /** 校验并创建 Match 专属目录；RootDirectory 必须是绝对路径。 */
    bool Initialize(const FGuiyangGameServerLaunchConfig& Config, FString& OutError);
    /** 追加一个已接受动作并推进哈希链；失败时调用方保持牌局运行但必须告警。 */
    bool AppendAction(FGuiyangActionEvidenceRecord& Record, FString& OutError);
    /** 原子保存完整快照；结算前调用失败必须阻止正常结算上报。 */
    bool SaveSnapshot(FGuiyangAuthoritativeSnapshot& Snapshot, FString& OutError);
    /** 读取当前 Epoch 之前最新有效快照及其后的连续动作；无快照返回 false 且错误为空。 */
    bool LoadLatestPriorEpoch(
        FGuiyangAuthoritativeSnapshot& OutSnapshot,
        TArray<FGuiyangActionEvidenceRecord>& OutActions,
        FString& OutError) const;
    /** 计算完整状态哈希；序列化失败返回空字符串。 */
    static FString CalculateStateHash(const FGuiyangAuthoritativeSnapshot& Snapshot);
    /** 仅计算牌桌恢复状态内容哈希，跨 RoomEpoch 重放时应保持一致。 */
    static FString CalculateTableStateHash(const FMahjongTableRecoveryState& TableState);
    /** 新 Epoch 完成重放后继承全局动作序号和上一哈希，但后续文件仍写入当前 Epoch。 */
    void AdoptRecoveredChain(int64 ActionSequence, const FString& ActionHash)
    {
        LastActionSequence = FMath::Max<int64>(0, ActionSequence);
        LastActionHash = ActionHash;
    }
    int64 GetLastActionSequence() const { return LastActionSequence; }
    const FString& GetLastActionHash() const { return LastActionHash; }
    int32 GetSnapshotEveryActions() const { return SnapshotEveryActions; }
    int32 GetSnapshotMaxIntervalSeconds() const { return SnapshotMaxIntervalSeconds; }

private:
    FString MatchDirectory;
    FString MatchId;
    FString RoomId;
    int64 CurrentRoomEpoch = 0;
    int64 LastActionSequence = 0;
    FString LastActionHash;
    int32 SnapshotEveryActions = 3;
    int32 SnapshotMaxIntervalSeconds = 10;
};
