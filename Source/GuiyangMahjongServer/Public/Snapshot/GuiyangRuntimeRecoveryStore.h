#pragma once

#include "CoreMinimal.h"
#include "Evidence/GuiyangActionEvidence.h"
#include "Network/MahjongNetworkTypes.h"
#include "Server/GuiyangFairShuffle.h"
#include "GuiyangRuntimeRecoveryStore.generated.h"

struct FGuiyangGameServerLaunchConfig;

/** 结算前固化到内容寻址路径的证据对象；AbsolutePath 只供本机校验，不进入网络信封。 */
struct GUIYANGMAHJONGSERVER_API FGuiyangRecoveryEvidenceObject
{
    /** snapshot、actions 或 shuffle-audit；GameData 按类型检查证据完整性。 */
    FString Kind;
    /** 相对受控对象根目录的不可覆盖键，包含 Match、Epoch 和内容 SHA-256。 */
    FString ObjectKey;
    /** 当前节点上的只读绝对路径；不得写入普通日志或返回客户端。 */
    FString AbsolutePath;
    /** 文件内容 SHA-256 和字节数，用于对象存储端到端校验。 */
    FString Sha256;
    int64 SizeBytes = 0;
};

/**
 * 一次可原子恢复的完整权威快照。
 * TableState 含完整手牌和牌墙，只能写入受限恢复卷；StateHash 用于读取后拒绝静默损坏。
 */
USTRUCT()
struct GUIYANGMAHJONGSERVER_API FGuiyangAuthoritativeSnapshot
{
    GENERATED_BODY()

    /** 快照的比赛、房间和 Epoch 作用域；新 DS 只读取严格小于自身 Epoch 的数据。 */
    UPROPERTY() FString MatchId;
    UPROPERTY() FString RoomId;
    UPROPERTY() FString RoomCode;
    UPROPERTY() int64 RoomEpoch = 0;
    /** 快照契约版本、已覆盖动作序号和牌桌状态版本。 */
    UPROPERTY() int32 SnapshotVersion = 1;
    UPROPERTY() int64 ActionSequence = 0;
    UPROPERTY() int32 StateVersion = 0;
    /** 锁定规则与可恢复随机状态；随机原文属于受限证据，不能进入普通日志。 */
    UPROPERTY() FString RuleSetVersion;
    UPROPERTY() FString RandomState;
    /** 当前未披露证明与既往已披露证明只存在受限快照，用于崩溃后继续完成审计链。 */
    UPROPERTY() bool bHasPendingShuffleProof = false;
    UPROPERTY() FGuiyangShuffleAuditProof PendingShuffleProof;
    UPROPERTY() TArray<FGuiyangShuffleAuditProof> CompletedShuffleProofs;
    UPROPERTY() FString FairnessEventChainDigest;
    /** 房间控制摘要、完整权威牌桌和当前托管座位；TableState 含四家私有牌。 */
    UPROPERTY() FMahjongRoomState RoomState;
    UPROPERTY() FMahjongTableRecoveryState TableState;
    UPROPERTY() TArray<int32> TrusteeSeats;
    /** 快照覆盖点的动作链摘要、完整快照摘要及 UTC 创建时间。 */
    UPROPERTY() FString PreviousActionHash;
    UPROPERTY() FString StateHash;
    UPROPERTY() FString CreatedAtUtc;
    /** 快照时权威回合计时器剩余秒数；新进程恢复时从该值继续而不是重置完整窗口。 */
    UPROPERTY() float RemainingActionTimeoutSeconds = 0.0f;
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
    /**
     * 将当前 Epoch 快照和动作文件复制到内容寻址不可覆盖路径并返回清单。
     * 任一文件缺失、哈希失败或既有目标内容不一致时失败，结算不得继续上报。
     */
    bool MaterializeFinalEvidence(
        TArray<FGuiyangRecoveryEvidenceObject>& OutObjects,
        FString& OutError) const;
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
    /** 当前比赛专属目录及作用域，初始化后在该 DS 进程生命周期内保持不变。 */
    FString MatchDirectory;
    FString RootDirectory;
    FString MatchId;
    FString RoomId;
    int64 CurrentRoomEpoch = 0;
    /** 已成功持久化的动作链游标；追加失败不得推进这两个值。 */
    int64 LastActionSequence = 0;
    FString LastActionHash;
    /** 普通动作触发快照的数量与时间阈值，均在初始化时读取并限制安全范围。 */
    int32 SnapshotEveryActions = 3;
    int32 SnapshotMaxIntervalSeconds = 10;
};
