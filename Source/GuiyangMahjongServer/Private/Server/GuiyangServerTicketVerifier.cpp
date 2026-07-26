#include "Server/GuiyangServerTicketVerifier.h"

#include "Server/GuiyangGameServerBridge.h"
#include "Dom/JsonObject.h"
#include "Misc/Base64.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

THIRD_PARTY_INCLUDES_START
#define UI OPENSSL_UI
#include <openssl/evp.h>
#include <openssl/hmac.h>
#undef UI
THIRD_PARTY_INCLUDES_END

namespace
{
    /** 将 JWT 风格的 Base64URL 字段还原为标准 Base64 后解码。 */
    bool Base64UrlDecode(const FString& Value, TArray<uint8>& OutBytes)
    {
        FString Normalized = Value;
        Normalized.ReplaceInline(TEXT("-"), TEXT("+"));
        Normalized.ReplaceInline(TEXT("_"), TEXT("/"));
        while (Normalized.Len() % 4 != 0) Normalized.AppendChar(TEXT('='));
        return FBase64::Decode(Normalized, OutBytes);
    }

    /** 使用 OpenSSL 计算 32 字节 HMAC-SHA256，不在日志中暴露密钥或原始票据。 */
    bool ComputeHmacSha256(const FString& Key, const FString& Data, TArray<uint8>& OutDigest)
    {
        FTCHARToUTF8 KeyUtf8(*Key);
        FTCHARToUTF8 DataUtf8(*Data);
        OutDigest.SetNumZeroed(32);
        unsigned int DigestLength = 0;
        const unsigned char* Result = HMAC(
            EVP_sha256(), KeyUtf8.Get(), KeyUtf8.Length(),
            reinterpret_cast<const unsigned char*>(DataUtf8.Get()),
            static_cast<size_t>(DataUtf8.Length()), OutDigest.GetData(), &DigestLength);
        if (!Result || DigestLength != 32)
        {
            OutDigest.Reset();
            return false;
        }
        return true;
    }

    /** 常量时间比较签名，避免普通早退比较泄露正确前缀长度。 */
    bool ConstantTimeEquals(const TArray<uint8>& Left, const TArray<uint8>& Right)
    {
        uint32 Difference = static_cast<uint32>(Left.Num() ^ Right.Num());
        const int32 Count = FMath::Max(Left.Num(), Right.Num());
        for (int32 Index = 0; Index < Count; ++Index)
        {
            const uint8 LeftByte = Left.IsValidIndex(Index) ? Left[Index] : 0;
            const uint8 RightByte = Right.IsValidIndex(Index) ? Right[Index] : 0;
            Difference |= static_cast<uint32>(LeftByte ^ RightByte);
        }
        return Difference == 0;
    }
}

FGuiyangJoinTicketValidator::FGuiyangJoinTicketValidator(const FGuiyangGameServerLaunchConfig& Config)
    : SigningKey(Config.JoinTicketSigningKey)
    , ExpectedRoomId(Config.RoomId)
    , ExpectedMatchId(Config.MatchId)
    , ExpectedServerInstanceId(Config.ServerInstanceId)
{
}

bool FGuiyangJoinTicketValidator::ValidateAndConsume(const FString& Ticket, const FString& SuppliedPlayerId,
    const int64 NowUnixSeconds, FGuiyangJoinTicketClaims& OutClaims, FString& OutError)
{
    // 先限制输入尺寸，避免异常大票据消耗解析与加密资源。
    if (Ticket.IsEmpty() || Ticket.Len() > 4096 || SuppliedPlayerId.IsEmpty())
    {
        OutError = TEXT("JOIN_TICKET_INVALID");
        return false;
    }

    FString EncodedPayload;
    FString EncodedSignature;
    if (!Ticket.Split(TEXT("."), &EncodedPayload, &EncodedSignature)
        || EncodedPayload.IsEmpty() || EncodedSignature.IsEmpty())
    {
        OutError = TEXT("JOIN_TICKET_INVALID");
        return false;
    }

    TArray<uint8> SuppliedSignature;
    TArray<uint8> ExpectedSignature;
    // 必须先验证签名，再信任并解析负载中的任何声明。
    if (!Base64UrlDecode(EncodedSignature, SuppliedSignature)
        || !ComputeHmacSha256(SigningKey, EncodedPayload, ExpectedSignature)
        || !ConstantTimeEquals(SuppliedSignature, ExpectedSignature))
    {
        OutError = TEXT("JOIN_TICKET_SIGNATURE_INVALID");
        return false;
    }

    TArray<uint8> PayloadBytes;
    if (!Base64UrlDecode(EncodedPayload, PayloadBytes))
    {
        OutError = TEXT("JOIN_TICKET_INVALID");
        return false;
    }
    PayloadBytes.Add(0);
    const FString PayloadJson = UTF8_TO_TCHAR(reinterpret_cast<const ANSICHAR*>(PayloadBytes.GetData()));
    TSharedPtr<FJsonObject> Payload;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(PayloadJson);
    if (!FJsonSerializer::Deserialize(Reader, Payload) || !Payload.IsValid()
        || !Payload->TryGetStringField(TEXT("playerId"), OutClaims.PlayerId)
        || !Payload->TryGetStringField(TEXT("roomId"), OutClaims.RoomId)
        || !Payload->TryGetStringField(TEXT("matchId"), OutClaims.MatchId)
        || !Payload->TryGetStringField(TEXT("serverInstanceId"), OutClaims.ServerInstanceId)
        || !Payload->TryGetStringField(TEXT("nonce"), OutClaims.Nonce)
        || !Payload->TryGetNumberField(TEXT("expiresAtUnixSeconds"), OutClaims.ExpiresAtUnixSeconds))
    {
        OutError = TEXT("JOIN_TICKET_INVALID");
        return false;
    }

    // 票据必须同时绑定玩家、房间、比赛和本次服务器实例，不能跨服复用。
    if (OutClaims.PlayerId != SuppliedPlayerId
        || OutClaims.RoomId != ExpectedRoomId
        || OutClaims.MatchId != ExpectedMatchId
        || OutClaims.ServerInstanceId != ExpectedServerInstanceId)
    {
        OutError = TEXT("JOIN_TICKET_SCOPE_MISMATCH");
        return false;
    }
    // 除了过期检查，也拒绝有效期异常长的票据，限制密钥泄露后的攻击窗口。
    if (OutClaims.ExpiresAtUnixSeconds <= NowUnixSeconds
        || OutClaims.ExpiresAtUnixSeconds > NowUnixSeconds + 120)
    {
        OutError = TEXT("JOIN_TICKET_EXPIRED");
        return false;
    }
    if (OutClaims.Nonce.Len() < 16 || OutClaims.Nonce.Len() > 128 || UsedNonces.Contains(OutClaims.Nonce))
    {
        OutError = TEXT("JOIN_TICKET_REPLAYED");
        return false;
    }

    // 消费前清理已过期 Nonce，使缓存大小随活跃票据数量而非进程时长增长。
    for (auto It = UsedNonces.CreateIterator(); It; ++It)
    {
        if (It.Value() <= NowUnixSeconds) It.RemoveCurrent();
    }
    UsedNonces.Add(OutClaims.Nonce, OutClaims.ExpiresAtUnixSeconds);
    return true;
}
