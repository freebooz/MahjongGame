#include "Evidence/GuiyangActionEvidence.h"

#include "openssl/sha.h"

namespace
{
    /** 对规范 UTF-8 文本计算小写 SHA-256；调用方不得把原始凭据传入。 */
    FString Sha256Hex(const FString& Text)
    {
        const FTCHARToUTF8 Utf8(*Text);
        uint8 Digest[SHA256_DIGEST_LENGTH] = {};
        SHA256(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length(), Digest);
        return BytesToHex(Digest, UE_ARRAY_COUNT(Digest)).ToLower();
    }
}

FString FGuiyangActionEvidence::NormalizeRequest(const FMahjongActionRequest& Request)
{
    FString Consumed;
    for (const int32 TileId : Request.ConsumedTileIds)
        Consumed += FString::Printf(TEXT("%s%d"), Consumed.IsEmpty() ? TEXT("") : TEXT(","), TileId);
    return FString::Printf(
        TEXT("action-v1|id=%s|sequence=%d|expected=%d|epoch=%lld|type=%d|round=%d|turn=%d|target=%d|consumed=%s"),
        *Request.ClientActionId.ToLower(), Request.ClientSequence, Request.ExpectedStateVersion,
        Request.RoomEpoch, static_cast<int32>(Request.Type), Request.RoundId, Request.TurnId,
        Request.TargetTileId, *Consumed);
}

FString FGuiyangActionEvidence::CalculateHash(const FGuiyangActionEvidenceRecord& Record)
{
    const FString Canonical = FString::Printf(
        TEXT("evidence-v1|previous=%s|match=%s|room=%s|epoch=%lld|action=%lld|before=%d|after=%d|stateHash=%s|player=%s|seat=%d|type=%s|replayable=%d|payload=%s|occurred=%s"),
        Record.PreviousHash.IsEmpty() ? TEXT("genesis") : *Record.PreviousHash.ToLower(),
        *Record.MatchId, *Record.RoomId, Record.RoomEpoch, Record.ActionSequence,
        Record.StateVersionBefore, Record.StateVersionAfter, *Record.StateHashAfter, *Record.PlayerId,
        Record.SeatId, *Record.ActionType, Record.bReplayable ? 1 : 0,
        *Record.NormalizedPayload, *Record.OccurredAtUtc);
    return Sha256Hex(Canonical);
}
