#include "Server/GuiyangFairShuffle.h"

// OpenSSL 1.1.1 将 UI 声明为全局类型，而 UE 5.8 已使用同名全局命名空间；
// 仅在第三方头展开期间重命名该未使用类型，避免污染 UE 符号且不改变加密实现。
#define UI OPENSSL_UI
#include "openssl/crypto.h"
#include "openssl/rand.h"
#include "openssl/sha.h"
#undef UI

namespace GuiyangFairShufflePrivate
{
    /** 承诺和证明链的规范版本；字段顺序或转义语义变化时必须同步升级。 */
    constexpr const TCHAR* CommitmentVersion = TEXT("fair-shuffle-v1");
    constexpr const TCHAR* EventChainVersion = TEXT("fair-audit-chain-v1");

    /** 规范身份字段不得含分隔符，避免跨语言重算时出现多种等价解释。 */
    bool IsCanonicalIdentity(const FString& Value)
    {
        return !Value.IsEmpty() && !Value.Contains(TEXT("|"))
            && !Value.Contains(TEXT("=")) && !Value.Contains(TEXT("\r"))
            && !Value.Contains(TEXT("\n"));
    }

    /** 验证固定长度十六进制文本；不依赖区域设置，接受输入大小写供历史审计工具复核。 */
    bool IsHex(const FString& Value, const int32 ExpectedLength)
    {
        if (Value.Len() != ExpectedLength)
        {
            return false;
        }
        for (const TCHAR Character : Value)
        {
            const bool bDigit = Character >= TEXT('0') && Character <= TEXT('9');
            const bool bLower = Character >= TEXT('a') && Character <= TEXT('f');
            const bool bUpper = Character >= TEXT('A') && Character <= TEXT('F');
            if (!bDigit && !bLower && !bUpper)
            {
                return false;
            }
        }
        return true;
    }
}

bool FGuiyangFairShuffle::Generate(
    const FString& RoomId,
    const int32 RoundId,
    const FGuiyangRuleSnapshot& RuleSnapshot,
    int32& OutShuffleSeed,
    FGuiyangShuffleAuditProof& OutProof,
    FString& OutError)
{
    OutShuffleSeed = 0;
    OutProof = FGuiyangShuffleAuditProof();
    // 本地房间使用六位公开房间码，托管房间使用后端 UUID；两者都属于合法的审计身份。
    // 公平性协议只要求身份非空且不能注入规范文本分隔符，强制解析 GUID 会让本地牌桌永远无法发牌。
    if (RoundId < 1 || !GuiyangFairShufflePrivate::IsCanonicalIdentity(RoomId)
        || !UGuiyangRuleSnapshotLibrary::VerifySnapshot(RuleSnapshot)
        || !GuiyangFairShufflePrivate::IsCanonicalIdentity(RuleSnapshot.Config.RuleId.ToString()))
    {
        OutError = TEXT("洗牌公平性绑定参数无效");
        return false;
    }

    uint8 SeedBytes[sizeof(uint32)] = {};
    uint8 NonceBytes[32] = {};
    // CSPRNG 故障必须阻止开局，绝不能回退到时间戳、GUID 或进程周期。
    if (RAND_bytes(SeedBytes, UE_ARRAY_COUNT(SeedBytes)) != 1
        || RAND_bytes(NonceBytes, UE_ARRAY_COUNT(NonceBytes)) != 1)
    {
        OPENSSL_cleanse(SeedBytes, sizeof(SeedBytes));
        OPENSSL_cleanse(NonceBytes, sizeof(NonceBytes));
        OutError = TEXT("服务端安全随机源不可用");
        return false;
    }

    uint32 SeedBits = 0;
    FMemory::Memcpy(&SeedBits, SeedBytes, sizeof(SeedBits));
    OutShuffleSeed = static_cast<int32>(SeedBits);
    OutProof.RoundId = RoundId;
    OutProof.SeedHex = FString::Printf(TEXT("%08x"), SeedBits);
    OutProof.ServerNonceHex = BytesToHex(NonceBytes, UE_ARRAY_COUNT(NonceBytes)).ToLower();
    OutProof.RuleId = RuleSnapshot.Config.RuleId.ToString();
    OutProof.RuleVersion = RuleSnapshot.Config.RuleVersion;
    OutProof.RuleHash = RuleSnapshot.RuleHash.ToLower();
    OutProof.CreatedAtUtc = FDateTime::UtcNow();
    OutProof.SeedCommitment = CalculateCommitment(RoomId, OutProof);

    // 局内只保留证明所需的十六进制副本；立即擦除栈上的原始随机字节。
    OPENSSL_cleanse(SeedBytes, sizeof(SeedBytes));
    OPENSSL_cleanse(NonceBytes, sizeof(NonceBytes));
    return OutProof.SeedCommitment.Len() == SHA256_DIGEST_LENGTH * 2;
}

FString FGuiyangFairShuffle::CalculateDeckOrderDigest(const TArray<FMahjongTile>& Deck)
{
    FString Canonical = TEXT("deck-order-v1");
    for (int32 Index = 0; Index < Deck.Num(); ++Index)
    {
        const FMahjongTile& Tile = Deck[Index];
        Canonical += FString::Printf(TEXT("|%d:%d:%d:%d:%d"), Index,
            static_cast<int32>(Tile.Suit), static_cast<int32>(Tile.Type),
            Tile.Rank, Tile.UniqueId);
    }
    return Sha256Hex(Canonical);
}

FString FGuiyangFairShuffle::CalculateCommitment(
    const FString& RoomId,
    const FGuiyangShuffleAuditProof& Proof)
{
    if (!GuiyangFairShufflePrivate::IsCanonicalIdentity(RoomId)
        || !GuiyangFairShufflePrivate::IsCanonicalIdentity(Proof.RuleId))
    {
        return FString();
    }
    return Sha256Hex(FString::Printf(
        TEXT("%s|seed=%s|roomId=%s|roundId=%d|ruleId=%s|ruleVersion=%d|ruleHash=%s|serverNonce=%s"),
        GuiyangFairShufflePrivate::CommitmentVersion, *Proof.SeedHex.ToLower(),
        *RoomId, Proof.RoundId, *Proof.RuleId, Proof.RuleVersion,
        *Proof.RuleHash.ToLower(), *Proof.ServerNonceHex.ToLower()));
}

FString FGuiyangFairShuffle::CalculateEventChainDigest(
    const FString& PreviousDigest,
    const FString& RoomId,
    const FGuiyangShuffleAuditProof& Proof)
{
    return Sha256Hex(FString::Printf(
        TEXT("%s|previous=%s|roomId=%s|roundId=%d|commitment=%s|deckOrderDigest=%s|ruleHash=%s"),
        GuiyangFairShufflePrivate::EventChainVersion,
        PreviousDigest.IsEmpty() ? TEXT("genesis") : *PreviousDigest.ToLower(),
        *RoomId, Proof.RoundId, *Proof.SeedCommitment.ToLower(),
        *Proof.DeckOrderDigest.ToLower(), *Proof.RuleHash.ToLower()));
}

bool FGuiyangFairShuffle::Verify(
    const FString& RoomId,
    const FGuiyangRuleSnapshot& RuleSnapshot,
    const TArray<FMahjongTile>& Deck,
    const FGuiyangShuffleAuditProof& Proof)
{
    return Proof.Algorithm == TEXT("UE-FRandomStream-FisherYates-v1")
        && Proof.RoundId > 0
        && GuiyangFairShufflePrivate::IsHex(Proof.SeedHex, 8)
        && GuiyangFairShufflePrivate::IsHex(Proof.ServerNonceHex, 64)
        && GuiyangFairShufflePrivate::IsHex(Proof.SeedCommitment, 64)
        && GuiyangFairShufflePrivate::IsHex(Proof.DeckOrderDigest, 64)
        && Proof.RuleId == RuleSnapshot.Config.RuleId.ToString()
        && Proof.RuleVersion == RuleSnapshot.Config.RuleVersion
        && Proof.RuleHash.Equals(RuleSnapshot.RuleHash, ESearchCase::IgnoreCase)
        && Proof.SeedCommitment.Equals(CalculateCommitment(RoomId, Proof), ESearchCase::IgnoreCase)
        && Proof.DeckOrderDigest.Equals(CalculateDeckOrderDigest(Deck), ESearchCase::IgnoreCase)
        && Proof.CreatedAtUtc.GetTicks() > 0
        && Proof.RevealedAtUtc >= Proof.CreatedAtUtc;
}

FString FGuiyangFairShuffle::Sha256Hex(const FString& CanonicalText)
{
    const FTCHARToUTF8 Utf8(*CanonicalText);
    uint8 Digest[SHA256_DIGEST_LENGTH] = {};
    SHA256(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length(), Digest);
    return BytesToHex(Digest, UE_ARRAY_COUNT(Digest)).ToLower();
}
