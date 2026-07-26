#pragma once

#include "CoreMinimal.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "History/MahjongMatchHistoryTypes.h"
#include "GuiyangMatchHistorySubsystem.generated.h"

DECLARE_DYNAMIC_MULTICAST_DELEGATE(FMahjongMatchHistoryChanged);

/** 管理当前设备上的本地战绩缓存、持久化和蓝图通知。 */
UCLASS()
class GUIYANGMAHJONGCLIENT_API UGuiyangMatchHistorySubsystem : public UGameInstanceSubsystem
{
    GENERATED_BODY()
public:
    /** 记录新增或清空后广播，供大厅战绩页刷新。 */
    UPROPERTY(BlueprintAssignable, Category="麻将|战绩") FMahjongMatchHistoryChanged OnHistoryChanged;

    /** 加载 SaveGame；不存在时创建空容器。 */
    virtual void Initialize(FSubsystemCollectionBase& Collection) override;
    /** 在 GameInstance 销毁前释放缓存引用。 */
    virtual void Deinitialize() override;

    /** 幂等写入最终结算，并限制历史记录总数。 */
    UFUNCTION(BlueprintCallable, Category="麻将|战绩")
    bool RecordFinalSettlement(const FMahjongFinalSettlementResult& Result);

    /** 返回副本，避免外部直接修改内部排序和容量约束。 */
    UFUNCTION(BlueprintPure, Category="麻将|战绩")
    TArray<FMahjongMatchHistoryRecord> GetRecords() const;

    /** 清空内存与磁盘记录并广播变化。 */
    UFUNCTION(BlueprintCallable, Category="麻将|战绩")
    void ClearHistory();

private:
    /** SaveGame 固定槽名和最多保留条数。 */
    static constexpr const TCHAR* SaveSlotName = TEXT("GuiyangMatchHistory");
    static constexpr int32 MaxHistoryRecords = 50;
    /** 当前已加载的可序列化对象。 */
    UPROPERTY() TObjectPtr<class UMahjongMatchHistorySaveGame> HistorySave;
    /** 将当前 HistorySave 原子写回固定槽位。 */
    bool SaveHistory() const;
};
