#pragma once

#include "CoreMinimal.h"
#include "Core/MahjongTypes.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "GuiyangRuleSnapshot.generated.h"

/** 房间创建时生成的不可变规则快照，可用于开局、重连和回放校验。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FGuiyangRuleSnapshot
{
    GENERATED_BODY()

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly) FMahjongRuleConfig Config;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly) FString CanonicalDefinition;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly) FString RuleHash;

    int32 GetTileCount() const;
};

/**
 * 蓝图可调用的规则快照工厂与完整性校验入口。
 *
 * 所有创建路径都必须先规范化配置再计算规范文本和哈希；验证仅判断快照是否自洽，
 * 不授权在房间开局后修改已冻结规则。
 */
UCLASS()
class GUIYANGMAHJONGCORE_API UGuiyangRuleSnapshotLibrary : public UBlueprintFunctionLibrary
{
    GENERATED_BODY()

public:
    /** 规范化请求配置并创建可用于复制、重连和回放验证的不可变快照。 */
    UFUNCTION(BlueprintPure, Category="麻将|规则")
    static FGuiyangRuleSnapshot CreateSnapshot(const FMahjongRuleConfig& RequestedConfig);

    /** 重新计算规范文本与哈希；任一内容漂移都会返回 false。 */
    UFUNCTION(BlueprintPure, Category="麻将|规则")
    static bool VerifySnapshot(const FGuiyangRuleSnapshot& Snapshot);

private:
    /** 收敛范围和联动开关，保证等价请求产生相同的规范表示。 */
    static FMahjongRuleConfig NormalizeConfig(const FMahjongRuleConfig& RequestedConfig);
    /** 按稳定字段顺序生成跨进程可比较的规则文本。 */
    static FString BuildCanonicalDefinition(const FMahjongRuleConfig& Config);
    /** 对 UTF-8 规范文本计算持久化哈希，不承担密码学签名职责。 */
    static FString CalculateHash(const FString& CanonicalDefinition);
};
