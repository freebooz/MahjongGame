#pragma once

#include "CoreMinimal.h"
#include "Core/MahjongTypes.h"
#include "Rules/GuiyangRuleSnapshot.h"

/**
 * 单局洗牌公平性证明。
 *
 * 该结构只在 Dedicated Server 与内部审计链路中流转，不复制给未结束牌局的客户端。
 * SeedHex 和 ServerNonceHex 必须等到本局结束后才能写入披露记录；开局前仅允许持久化承诺值。
 */
struct GUIYANGMAHJONGSERVER_API FGuiyangShuffleAuditProof
{
    /** 证明协议和洗牌实现版本；算法行为变化时必须升级。 */
    FString Algorithm = TEXT("UE-FRandomStream-FisherYates-v1");
    /** 本场比赛内从 1 开始连续递增的局号。 */
    int32 RoundId = 0;
    /** 32 位洗牌种子的无符号小写十六进制表示，固定为 8 个字符。 */
    FString SeedHex;
    /** CSPRNG 产生的 256 位服务端随机数，固定为 64 个小写十六进制字符。 */
    FString ServerNonceHex;
    /** 开局前持久化的 SHA-256 承诺，不包含可用于还原牌序的明文。 */
    FString SeedCommitment;
    /** 洗牌完成后按完整牌墙稳定序列计算的 SHA-256 摘要。 */
    FString DeckOrderDigest;
    /** 与本局绑定的冻结规则身份和规则内容摘要。 */
    FString RuleId;
    int32 RuleVersion = 0;
    FString RuleHash;
    /** 承诺创建时间和本局结束披露时间；均为服务器 UTC，仅用于审计排序。 */
    FDateTime CreatedAtUtc;
    FDateTime RevealedAtUtc;
};

/**
 * 服务端安全洗牌与证明工具。
 *
 * 随机材料只能由服务端 CSPRNG 生成；客户端输入、时间戳和进程周期均不得参与最终种子的决定。
 * 摘要对带版本、带字段名的 UTF-8 规范文本计算 SHA-256，避免直接拼接造成边界歧义。
 */
class GUIYANGMAHJONGSERVER_API FGuiyangFairShuffle final
{
public:
    /** 生成单局种子、nonce 和承诺；失败时不会返回部分可用材料。 */
    static bool Generate(
        const FString& RoomId,
        int32 RoundId,
        const FGuiyangRuleSnapshot& RuleSnapshot,
        int32& OutShuffleSeed,
        FGuiyangShuffleAuditProof& OutProof,
        FString& OutError);

    /** 按稳定牌面和实例序号序列计算完整牌墙摘要，不输出或记录原始牌序。 */
    static FString CalculateDeckOrderDigest(const TArray<FMahjongTile>& Deck);

    /** 重新计算开局承诺，供 Lobby、测试和争议调查工具验证披露内容。 */
    static FString CalculateCommitment(
        const FString& RoomId,
        const FGuiyangShuffleAuditProof& Proof);

    /** 将上一审计链摘要与本局最终证明绑定，形成不可静默删改或重排的链式摘要。 */
    static FString CalculateEventChainDigest(
        const FString& PreviousDigest,
        const FString& RoomId,
        const FGuiyangShuffleAuditProof& Proof);

    /** 验证字段格式、规则绑定、承诺、牌序摘要和披露时间的基本一致性。 */
    static bool Verify(
        const FString& RoomId,
        const FGuiyangRuleSnapshot& RuleSnapshot,
        const TArray<FMahjongTile>& Deck,
        const FGuiyangShuffleAuditProof& Proof);

private:
    /** 对 UTF-8 文本计算小写十六进制 SHA-256。 */
    static FString Sha256Hex(const FString& CanonicalText);
};
