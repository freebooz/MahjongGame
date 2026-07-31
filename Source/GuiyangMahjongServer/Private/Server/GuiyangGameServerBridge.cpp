#include "Server/GuiyangGameServerBridge.h"

#include "Dom/JsonObject.h"
#include "Engine/NetDriver.h"
#include "Engine/World.h"
#include "Game/GuiyangMahjongGameMode.h"
#include "Game/GuiyangMahjongGameState.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "Game/GuiyangMahjongPlayerState.h"
#include "GameFramework/PlayerController.h"
#include "GuiyangMahjong.h"
#include "HttpModule.h"
#include "HAL/PlatformFileManager.h"
#include "HAL/PlatformMemory.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformTime.h"
#include "Misc/DateTime.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Room/GuiyangManagedRoomDefinition.h"
#include "Room/GuiyangRoomManager.h"
#include "Snapshot/GuiyangRuntimeRecoveryStore.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "TimerManager.h"
#include "openssl/hmac.h"
#include "openssl/sha.h"

namespace GuiyangGameServerPrivate
{
    /** 房间运行遥测主版本；变更字段单位或空值语义时必须升级该版本。 */
    constexpr int32 RuntimeTelemetrySchemaVersion = 1;

    /** 读取并拒绝空白命令行值，避免“存在但不可用”的启动配置。 */
    bool ReadRequiredValue(const TCHAR* CommandLine, const TCHAR* Match, FString& OutValue)
    {
        return FParse::Value(CommandLine, Match, OutValue) && !OutValue.TrimStartAndEnd().IsEmpty();
    }

    /** 统一使用紧凑 JSON 作为控制面 HTTP 请求体。 */
    FString SerializeJson(const TSharedRef<FJsonObject>& Object)
    {
        FString Body;
        const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Body);
        FJsonSerializer::Serialize(Object, Writer);
        return Body;
    }

    /** 使用注册后获得的用途隔离结果凭据计算最终信封 HMAC-SHA256；密钥不进入日志或持久化正文。 */
    FString HmacSha256Hex(const FString& Key, const FString& Canonical)
    {
        const FTCHARToUTF8 KeyUtf8(*Key);
        const FTCHARToUTF8 DataUtf8(*Canonical);
        uint8 Digest[EVP_MAX_MD_SIZE] = {};
        unsigned int DigestLength = 0;
        const unsigned char* Result = HMAC(
            EVP_sha256(), KeyUtf8.Get(), KeyUtf8.Length(),
            reinterpret_cast<const unsigned char*>(DataUtf8.Get()), DataUtf8.Length(),
            Digest, &DigestLength);
        return Result && DigestLength == SHA256_DIGEST_LENGTH
            ? BytesToHex(Digest, DigestLength).ToLower()
            : FString();
    }

    /** 对短期工作负载凭据生成不可逆 SHA-256 摘要，供 Lobby 绑定实例身份而不暴露原文。 */
    FString Sha256Hex(const FString& Value)
    {
        const FTCHARToUTF8 Utf8(*Value);
        uint8 Digest[SHA256_DIGEST_LENGTH] = {};
        return SHA256(
            reinterpret_cast<const unsigned char*>(Utf8.Get()), Utf8.Length(), Digest)
            ? BytesToHex(Digest, SHA256_DIGEST_LENGTH).ToLower()
            : FString();
    }

    /**
     * 输出与控制面一致的单行 JSON 日志。
     * 该入口只接受已由调用方构造的摘要，不接受凭证、原始 IP、聊天或支付正文。
     */
    void WriteStructuredLog(
        const FString& Level,
        const FString& TraceId,
        const FString& RoomId,
        const FString& MatchId,
        const FString& ServerInstanceId,
        const FString& EventId,
        const FString& Message)
    {
        FString Environment = FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_ENVIRONMENT"));
        if (Environment.IsEmpty())
        {
#if UE_BUILD_SHIPPING
            Environment = TEXT("Production");
#else
            Environment = TEXT("Development");
#endif
        }
        const TSharedRef<FJsonObject> Entry = MakeShared<FJsonObject>();
        Entry->SetStringField(TEXT("Timestamp"), FDateTime::UtcNow().ToIso8601());
        Entry->SetStringField(TEXT("Level"), Level);
        Entry->SetStringField(TEXT("Service"), TEXT("GuiyangMahjong.DedicatedServer"));
        Entry->SetStringField(TEXT("Environment"), Environment);
        Entry->SetStringField(TEXT("TraceId"), TraceId);
        Entry->SetStringField(TEXT("RoomId"), RoomId);
        Entry->SetStringField(TEXT("PlayerId"), TEXT(""));
        Entry->SetStringField(TEXT("MatchId"), MatchId);
        Entry->SetStringField(TEXT("ServerInstanceId"), ServerInstanceId);
        Entry->SetStringField(TEXT("EventId"), EventId);
        Entry->SetStringField(TEXT("Category"), TEXT("ManagedGameServer"));
        Entry->SetStringField(TEXT("Message"), Message);
        Entry->SetObjectField(TEXT("Properties"), MakeShared<FJsonObject>());
        UE_LOG(LogMahjongServer, Log, TEXT("%s"), *SerializeJson(Entry));
    }
}

bool FGuiyangGameServerLaunchConfig::TryParse(const TCHAR* CommandLine, const FString& SigningKey,
    const FString& RegistrationCredential, const FString& MatchResultOutboxPath,
    FGuiyangGameServerLaunchConfig& OutConfig, FString& OutError)
{
    // 解析前重置输出；只有全部验证通过后调用方才可使用该配置。
    OutConfig = FGuiyangGameServerLaunchConfig();
    if (!FParse::Param(CommandLine, TEXT("MahjongManagedGameServer")))
    {
        OutError = TEXT("Managed GameServer flag is missing");
        return false;
    }
    if (!GuiyangGameServerPrivate::ReadRequiredValue(CommandLine, TEXT("RoomId="), OutConfig.RoomId)
        || !GuiyangGameServerPrivate::ReadRequiredValue(CommandLine, TEXT("MatchId="), OutConfig.MatchId)
        || !GuiyangGameServerPrivate::ReadRequiredValue(
            CommandLine, TEXT("ServerInstanceId="), OutConfig.ServerInstanceId)
        || !GuiyangGameServerPrivate::ReadRequiredValue(
            CommandLine, TEXT("LobbyInternalUrl="), OutConfig.LobbyInternalUrl)
        || !GuiyangGameServerPrivate::ReadRequiredValue(
            CommandLine, TEXT("BuildVersion="), OutConfig.BuildVersion)
        || !GuiyangGameServerPrivate::ReadRequiredValue(
            CommandLine, TEXT("RuleSetVersion="), OutConfig.RuleSetVersion)
        || !GuiyangGameServerPrivate::ReadRequiredValue(
            CommandLine, TEXT("ProtocolVersion="), OutConfig.ProtocolVersion)
        || !GuiyangGameServerPrivate::ReadRequiredValue(
            CommandLine, TEXT("AdvertisedIp="), OutConfig.AdvertisedIp)
        || !FParse::Value(CommandLine, TEXT("Port="), OutConfig.Port)
        || !FParse::Value(CommandLine, TEXT("RoomEpoch="), OutConfig.RoomEpoch)
        || !FParse::Value(
            CommandLine,
            TEXT("LeaseFencingToken="),
            OutConfig.LeaseFencingToken))
    {
        OutError = TEXT("Managed GameServer launch arguments are incomplete");
        return false;
    }

    // 规范化来自编排器的文本，URL 去除尾斜杠以便安全拼接固定端点。
    OutConfig.RoomId.TrimStartAndEndInline();
    OutConfig.MatchId.TrimStartAndEndInline();
    OutConfig.ServerInstanceId.TrimStartAndEndInline();
    OutConfig.LobbyInternalUrl.TrimStartAndEndInline();
    OutConfig.LobbyInternalUrl.RemoveFromEnd(TEXT("/"));
    OutConfig.GameDataInternalUrl = FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_GAMEDATA_INTERNAL_URL"));
    OutConfig.GameDataInternalUrl.TrimStartAndEndInline();
    if (OutConfig.GameDataInternalUrl.IsEmpty()) OutConfig.GameDataInternalUrl = OutConfig.LobbyInternalUrl;
    OutConfig.GameDataInternalUrl.RemoveFromEnd(TEXT("/"));
    OutConfig.RegistrationCredential = RegistrationCredential;
    OutConfig.RegistrationCredential.TrimStartAndEndInline();
    OutConfig.BuildVersion.TrimStartAndEndInline();
    OutConfig.RuleSetVersion.TrimStartAndEndInline();
    OutConfig.ProtocolVersion.TrimStartAndEndInline();
    OutConfig.AdvertisedIp.TrimStartAndEndInline();
    OutConfig.JoinTicketSigningKey = SigningKey;
    OutConfig.SettlementSigningKey = FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_SETTLEMENT_SIGNING_KEY"));
    OutConfig.SettlementSigningKey.TrimStartAndEndInline();
    OutConfig.MatchResultOutboxPath = MatchResultOutboxPath.TrimStartAndEnd();
    FPaths::NormalizeFilename(OutConfig.MatchResultOutboxPath);
    OutConfig.RecoveryDirectory = FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_RECOVERY_DIRECTORY"));
    OutConfig.RecoveryDirectory.TrimStartAndEndInline();
    // 本地旧启动脚本在兼容窗口内可回退到 Outbox 同级 recovery；Agones 仍由 Fleet 强制显式挂载。
    if (OutConfig.RecoveryDirectory.IsEmpty())
        OutConfig.RecoveryDirectory = FPaths::Combine(
            FPaths::GetPath(OutConfig.MatchResultOutboxPath), TEXT("recovery"));
    FPaths::NormalizeDirectoryName(OutConfig.RecoveryDirectory);
    OutConfig.bAllowLegacyJoinTickets = FPlatformMisc::GetEnvironmentVariable(
        TEXT("MAHJONG_ALLOW_LEGACY_JOIN_TICKETS")).Equals(TEXT("true"), ESearchCase::IgnoreCase);
    FString CompatibleBuilds = FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_COMPATIBLE_CLIENT_BUILDS"));
    CompatibleBuilds.ParseIntoArray(OutConfig.CompatibleClientBuilds, TEXT(","), true);
    for (FString& Build : OutConfig.CompatibleClientBuilds) Build.TrimStartAndEndInline();
    OutConfig.CompatibleClientBuilds.RemoveAll([](const FString& Build) { return Build.IsEmpty(); });
    if (OutConfig.CompatibleClientBuilds.IsEmpty()) OutConfig.CompatibleClientBuilds.Add(OutConfig.BuildVersion);
    // 严格校验 GUID、网络端点、凭证强度及每实例唯一 Outbox 路径。
    FGuid ParsedGuid;
    if (!FGuid::Parse(OutConfig.RoomId, ParsedGuid)
        || !FGuid::Parse(OutConfig.MatchId, ParsedGuid)
        || !FGuid::Parse(OutConfig.ServerInstanceId, ParsedGuid)
        || OutConfig.Port < 1024 || OutConfig.Port > 65535
        || OutConfig.RoomEpoch < 1
        || OutConfig.LeaseFencingToken < 1
        || (!OutConfig.LobbyInternalUrl.StartsWith(TEXT("http://"))
            && !OutConfig.LobbyInternalUrl.StartsWith(TEXT("https://")))
        || (!OutConfig.GameDataInternalUrl.StartsWith(TEXT("http://"))
            && !OutConfig.GameDataInternalUrl.StartsWith(TEXT("https://")))
        || OutConfig.BuildVersion.Len() > 80
        || OutConfig.RuleSetVersion.IsEmpty() || OutConfig.RuleSetVersion.Len() > 80
        || OutConfig.ProtocolVersion.IsEmpty() || OutConfig.ProtocolVersion.Len() > 32
        || OutConfig.AdvertisedIp.Len() > 255
        || OutConfig.RegistrationCredential.Len() < 32
        || OutConfig.JoinTicketSigningKey.Len() < 32
        || OutConfig.SettlementSigningKey.Len() < 32
        || OutConfig.MatchResultOutboxPath.IsEmpty()
        || OutConfig.MatchResultOutboxPath.Len() > 1024
        || FPaths::IsRelative(OutConfig.MatchResultOutboxPath)
        || !FPaths::GetExtension(OutConfig.MatchResultOutboxPath).Equals(TEXT("json"), ESearchCase::IgnoreCase)
        || !FPaths::GetBaseFilename(OutConfig.MatchResultOutboxPath).Equals(
            OutConfig.ServerInstanceId, ESearchCase::IgnoreCase)
        || OutConfig.RecoveryDirectory.IsEmpty()
        || OutConfig.RecoveryDirectory.Len() > 1024
        || FPaths::IsRelative(OutConfig.RecoveryDirectory))
    {
        OutError = TEXT("Managed GameServer launch arguments failed validation");
        return false;
    }
    return true;
}

bool UGuiyangGameServerBridge::Initialize(
    UWorld* InWorld, const FGuiyangGameServerLaunchConfig& InConfig, FString& OutError)
{
    if (!InWorld || !IsRunningDedicatedServer())
    {
        OutError = TEXT("Managed bridge requires a Dedicated Server world");
        return false;
    }
    // 在第一次网络请求前建立全部不可变依赖。
    World = InWorld;
    Config = InConfig;
    TicketValidator = MakeUnique<FGuiyangJoinTicketValidator>(Config);
    SendRegistration();
    return true;
}

void UGuiyangGameServerBridge::Shutdown()
{
    // 先置关闭标志，使已在途 HTTP 回调只做安全早退。
    bShuttingDown = true;
    bRegistered = false;
    HeartbeatCredential.Reset();
    ResultCredential.Reset();
    TicketValidator.Reset();
    if (World.IsValid())
    {
        World->GetTimerManager().ClearTimer(HeartbeatTimer);
        World->GetTimerManager().ClearTimer(MatchResultRetryTimer);
    }
}

void UGuiyangGameServerBridge::BeginDestroy()
{
    Shutdown();
    Super::BeginDestroy();
}

bool UGuiyangGameServerBridge::ValidateAndConsumeJoinTicket(const FString& Ticket, const FString& PlayerId,
    FGuiyangJoinTicketClaims& OutClaims, FString& OutError)
{
    // 未向控制面注册完成前不接受任何玩家票据。
    if (!bRegistered || !TicketValidator)
    {
        OutError = TEXT("GAMESERVER_NOT_REGISTERED");
        return false;
    }
    return TicketValidator->ValidateAndConsume(
        Ticket, PlayerId, FDateTime::UtcNow().ToUnixTimestamp(), OutClaims, OutError);
}

bool UGuiyangGameServerBridge::AppendShuffleAuditRecord(
    const FGuiyangShuffleAuditProof& Proof,
    const bool bReveal,
    const FString& EventChainDigest) const
{
    if (Config.MatchResultOutboxPath.IsEmpty() || Proof.RoundId < 1
        || Proof.SeedCommitment.Len() != 64)
    {
        return false;
    }

    const TSharedRef<FJsonObject> Record = MakeShared<FJsonObject>();
    Record->SetStringField(TEXT("schema"), TEXT("mahjong-fairness-audit-v1"));
    Record->SetStringField(TEXT("eventType"), bReveal ? TEXT("Reveal") : TEXT("Commitment"));
    Record->SetStringField(TEXT("roomId"), Config.RoomId);
    Record->SetStringField(TEXT("matchId"), Config.MatchId);
    Record->SetStringField(TEXT("serverInstanceId"), Config.ServerInstanceId);
    Record->SetNumberField(TEXT("roundId"), Proof.RoundId);
    Record->SetStringField(TEXT("algorithm"), Proof.Algorithm);
    Record->SetStringField(TEXT("ruleId"), Proof.RuleId);
    Record->SetNumberField(TEXT("ruleVersion"), Proof.RuleVersion);
    Record->SetStringField(TEXT("ruleHash"), Proof.RuleHash);
    Record->SetStringField(TEXT("seedCommitment"), Proof.SeedCommitment);
    Record->SetStringField(TEXT("createdAtUtc"), Proof.CreatedAtUtc.ToIso8601());
    if (bReveal)
    {
        // 只有结算后的 Reveal 事件才能包含可复现种子；开局 Commitment 分支故意没有这些字段。
        Record->SetStringField(TEXT("seedHex"), Proof.SeedHex);
        Record->SetStringField(TEXT("serverNonceHex"), Proof.ServerNonceHex);
        Record->SetStringField(TEXT("deckOrderDigest"), Proof.DeckOrderDigest);
        Record->SetStringField(TEXT("revealedAtUtc"), Proof.RevealedAtUtc.ToIso8601());
        Record->SetStringField(TEXT("eventChainDigest"), EventChainDigest);
    }

    const FString AuditDirectory = FPaths::GetPath(Config.MatchResultOutboxPath);
    const FString AuditPath = FPaths::Combine(
        AuditDirectory, Config.MatchId + TEXT(".fairness.jsonl"));
    if (!FPlatformFileManager::Get().GetPlatformFile().CreateDirectoryTree(*AuditDirectory))
    {
        return false;
    }
    // 单行追加使进程崩溃后的恢复工具可以忽略最后一个不完整行，同时保留此前已刷盘的承诺。
    return FFileHelper::SaveStringToFile(
        GuiyangGameServerPrivate::SerializeJson(Record) + LINE_TERMINATOR,
        *AuditPath,
        FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM,
        &IFileManager::Get(),
        FILEWRITE_Append);
}

void UGuiyangGameServerBridge::QueueFinalSettlement(
    const FMahjongFinalSettlementResult& Result,
    const int32 SettlementVersion,
    const TArray<FGuiyangShuffleAuditProof>& ShuffleProofs,
    const FString& EventChainDigest,
    const FString& FinalStateHash,
    const FString& ActionLogHash,
    const FString& RandomCommitment,
    const FString& EvidenceId,
    const TArray<FGuiyangRecoveryEvidenceObject>& EvidenceObjects)
{
    FGuid ParsedEvidenceId;
    if (!bRegistered || bShuttingDown || ResultCredential.Len() < 32
        || Result.MatchId != Config.MatchId || Result.RoomId != Config.RoomId
        || SettlementVersion < 1 || Result.CompletedRounds < 1 || Result.CompletedRounds > 16
        || Result.Players.IsEmpty() || Result.Players.Num() > 4
        || ShuffleProofs.Num() != Result.CompletedRounds || EventChainDigest.Len() != 64
        || FinalStateHash.Len() != 64 || ActionLogHash.Len() != 64
        || RandomCommitment.Len() != 64 || EvidenceObjects.Num() < 2
        || !FGuid::Parse(EvidenceId, ParsedEvidenceId))
    {
        UE_LOG(LogMahjongServer, Error,
            TEXT("Final settlement report rejected locally InstanceId=%s MatchId=%s Sequence=%lld"),
            *Config.ServerInstanceId, *Result.MatchId, static_cast<int64>(SettlementVersion));
        return;
    }
    // 进程内同一时刻只允许一个待确认结算；相同序号视为幂等重试。
    if (!PendingMatchResultBody.IsEmpty())
    {
        if (PendingMatchId == Result.MatchId && PendingResultSequence == SettlementVersion) return;
        UE_LOG(LogMahjongServer, Error,
            TEXT("A different final settlement is already pending InstanceId=%s MatchId=%s"),
            *Config.ServerInstanceId, *Config.MatchId);
        return;
    }

    const TSharedRef<FJsonObject> Body = MakeShared<FJsonObject>();
    Body->SetStringField(TEXT("match_id"), Result.MatchId);
    Body->SetStringField(TEXT("room_id"), Result.RoomId);
    Body->SetNumberField(TEXT("round_no"), Result.CompletedRounds);
    Body->SetNumberField(TEXT("settlement_version"), SettlementVersion);
    Body->SetStringField(TEXT("server_instance_id"), Config.ServerInstanceId);
    Body->SetNumberField(TEXT("room_epoch"), static_cast<double>(Config.RoomEpoch));
    Body->SetStringField(TEXT("ruleset_version"), Config.RuleSetVersion);
    Body->SetStringField(TEXT("server_build"), Config.BuildVersion);
    // 只绑定短期工作负载凭据的不可逆摘要；凭据原文只用于本次 HTTP Authorization。
    const FString WorkloadCredentialHash = GuiyangGameServerPrivate::Sha256Hex(ResultCredential);
    if (WorkloadCredentialHash.Len() != 64) return;
    Body->SetStringField(TEXT("workload_credential_hash"), WorkloadCredentialHash);
    Body->SetStringField(TEXT("final_state_hash"), FinalStateHash.ToLower());
    Body->SetStringField(TEXT("action_log_hash"), ActionLogHash.ToLower());
    Body->SetStringField(TEXT("random_commitment"), RandomCommitment.ToLower());
    Body->SetStringField(TEXT("evidence_id"), EvidenceId);
    // 在落盘前验证每名玩家及座位唯一性，防止无效结果进入可靠 Outbox。
    TArray<TSharedPtr<FJsonValue>> Players;
    TSet<FString> PlayerIds;
    for (const FMahjongFinalPlayerResult& Player : Result.Players)
    {
        if (Player.PlayerId.IsEmpty() || Player.PlayerId.Len() > 80
            || Player.SeatIndex < 0 || Player.SeatIndex > 3
            || Player.Rank < 1 || Player.Rank > 4 || PlayerIds.Contains(Player.PlayerId))
        {
            UE_LOG(LogMahjongServer, Error,
                TEXT("Final settlement player data is invalid InstanceId=%s MatchId=%s"),
                *Config.ServerInstanceId, *Config.MatchId);
            return;
        }
        PlayerIds.Add(Player.PlayerId);
        const TSharedRef<FJsonObject> PlayerObject = MakeShared<FJsonObject>();
        PlayerObject->SetStringField(TEXT("player_id"), Player.PlayerId);
        PlayerObject->SetNumberField(TEXT("seat_id"), Player.SeatIndex);
        PlayerObject->SetNumberField(TEXT("rank"), Player.Rank);
        PlayerObject->SetNumberField(TEXT("total_score"), Player.TotalScore);
        Players.Add(MakeShared<FJsonValueObject>(PlayerObject));
    }
    Body->SetArrayField(TEXT("player_results"), Players);
    int32 ExpectedProofRound = 1;
    for (const FGuiyangShuffleAuditProof& Proof : ShuffleProofs)
    {
        if (Proof.RoundId != ExpectedProofRound++ || Proof.SeedHex.Len() != 8
            || Proof.ServerNonceHex.Len() != 64 || Proof.SeedCommitment.Len() != 64
            || Proof.DeckOrderDigest.Len() != 64 || Proof.RuleVersion < 1
            || Proof.RevealedAtUtc < Proof.CreatedAtUtc)
        {
            UE_LOG(LogMahjongServer, Error,
                TEXT("Final settlement fairness proof rejected InstanceId=%s RoundId=%d"),
                *Config.ServerInstanceId, Proof.RoundId);
            return;
        }
    }
    // 公平性证明已固化到受限证据对象；网络信封只保留组合承诺和证据清单，避免扩散完整种子。
    TArray<FGuiyangRecoveryEvidenceObject> SortedEvidence = EvidenceObjects;
    SortedEvidence.Sort([](const FGuiyangRecoveryEvidenceObject& Left, const FGuiyangRecoveryEvidenceObject& Right)
    {
        return Left.Kind < Right.Kind;
    });
    TArray<TSharedPtr<FJsonValue>> EvidenceValues;
    TArray<FString> EvidenceCanonical;
    for (const FGuiyangRecoveryEvidenceObject& Object : SortedEvidence)
    {
        if (Object.Kind.IsEmpty() || Object.ObjectKey.IsEmpty() || Object.Sha256.Len() != 64 || Object.SizeBytes < 1)
            return;
        const TSharedRef<FJsonObject> Item = MakeShared<FJsonObject>();
        Item->SetStringField(TEXT("kind"), Object.Kind);
        Item->SetStringField(TEXT("object_key"), Object.ObjectKey);
        Item->SetStringField(TEXT("sha256"), Object.Sha256.ToLower());
        Item->SetNumberField(TEXT("size_bytes"), static_cast<double>(Object.SizeBytes));
        EvidenceValues.Add(MakeShared<FJsonValueObject>(Item));
        EvidenceCanonical.Add(FString::Printf(TEXT("%s,%s,%s,%lld"),
            *Object.Kind, *Object.ObjectKey, *Object.Sha256.ToLower(), Object.SizeBytes));
    }
    Body->SetArrayField(TEXT("evidence_manifest"), EvidenceValues);
    // 统一截断到整秒，确保 C++ JSON 时间和 .NET Unix 毫秒规范串完全一致。
    const FDateTime GeneratedAt = FDateTime::FromUnixTimestamp(FDateTime::UtcNow().ToUnixTimestamp());
    Body->SetStringField(TEXT("generated_at"), GeneratedAt.ToIso8601());
    TArray<FMahjongFinalPlayerResult> SortedPlayers = Result.Players;
    SortedPlayers.Sort([](const FMahjongFinalPlayerResult& Left, const FMahjongFinalPlayerResult& Right)
    {
        return Left.SeatIndex < Right.SeatIndex;
    });
    TArray<FString> PlayerCanonical;
    for (const FMahjongFinalPlayerResult& Player : SortedPlayers)
        PlayerCanonical.Add(FString::Printf(TEXT("%s,%d,%d,%d"),
            *Player.PlayerId, Player.SeatIndex, Player.Rank, Player.TotalScore));
    const TArray<FString> CanonicalParts = {
        TEXT("final-result-v1"), Result.MatchId, Result.RoomId,
        FString::FromInt(Result.CompletedRounds), FString::FromInt(SettlementVersion),
        Config.ServerInstanceId, FString::Printf(TEXT("%lld"), Config.RoomEpoch),
        Config.RuleSetVersion, Config.BuildVersion, WorkloadCredentialHash, FinalStateHash.ToLower(),
        ActionLogHash.ToLower(), RandomCommitment.ToLower(), FString::Join(PlayerCanonical, TEXT(";")),
        EvidenceId, FString::Join(EvidenceCanonical, TEXT(";")),
        FString::Printf(TEXT("%lld"), GeneratedAt.ToUnixTimestamp() * 1000)
    };
    const FString Canonical = FString::Join(CanonicalParts, TEXT("|"));
    const FString ServerSignature = GuiyangGameServerPrivate::HmacSha256Hex(
        Config.SettlementSigningKey, Canonical);
    if (ServerSignature.Len() != 64) return;
    Body->SetStringField(TEXT("server_signature"), ServerSignature);
    // 必须先持久化再发送，确保进程在 HTTP 请求期间崩溃也不会丢失结算。
    if (!PersistPendingMatchResult(Body))
    {
        UE_LOG(LogMahjongServer, Error,
            TEXT("Final settlement outbox persistence failed InstanceId=%s MatchId=%s"),
            *Config.ServerInstanceId, *Config.MatchId);
        return;
    }
    PendingMatchResultBody = GuiyangGameServerPrivate::SerializeJson(Body);
    PendingMatchId = Result.MatchId;
    PendingResultSequence = SettlementVersion;
    SettlementResultSequence = SettlementVersion;
    SettlementStatus = TEXT("Submitted");
    SettlementSubmittedAtUtc = FDateTime::UtcNow();
    SettlementConfirmedAtUtc = FDateTime();
    SettlementFailureReason.Reset();
    const FTCHARToUTF8 ResultUtf8(*PendingMatchResultBody);
    FSHA256Signature ResultSignature;
    if (FPlatformMisc::GetSHA256Signature(
        ResultUtf8.Get(), static_cast<uint32>(ResultUtf8.Length()), ResultSignature))
    {
        SettlementResultHash = ResultSignature.ToString().ToLower();
    }
    MatchResultAttempt = 0;
    SendPendingMatchResult();
}

void UGuiyangGameServerBridge::SendRegistration()
{
    const TSharedRef<FJsonObject> Body = MakeShared<FJsonObject>();
    Body->SetStringField(TEXT("serverInstanceId"), Config.ServerInstanceId);
    Body->SetStringField(TEXT("roomId"), Config.RoomId);
    Body->SetStringField(TEXT("matchId"), Config.MatchId);
    Body->SetStringField(TEXT("listenIp"), Config.AdvertisedIp);
    Body->SetNumberField(TEXT("listenPort"), Config.Port);
    Body->SetStringField(TEXT("buildVersion"), Config.BuildVersion);
    Body->SetStringField(TEXT("registrationCredential"), Config.RegistrationCredential);
    Body->SetNumberField(TEXT("roomEpoch"), static_cast<double>(Config.RoomEpoch));
    Body->SetNumberField(
        TEXT("fencingToken"),
        static_cast<double>(Config.LeaseFencingToken));

    // 注册凭证放在请求体中，仅发送到受信任的 Lobby 内网端点。
    const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request = FHttpModule::Get().CreateRequest();
    Request->SetURL(Config.LobbyInternalUrl + TEXT("/internal/gameservers/register"));
    Request->SetVerb(TEXT("POST"));
    Request->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
    Request->SetHeader(TEXT("X-Request-Id"), FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower));
    Request->SetContentAsString(GuiyangGameServerPrivate::SerializeJson(Body));
    Request->OnProcessRequestComplete().BindUObject(this, &ThisClass::HandleRegistrationResponse);
    Request->ProcessRequest();
}

void UGuiyangGameServerBridge::HandleRegistrationResponse(
    FHttpRequestPtr Request, FHttpResponsePtr Response, const bool bSucceeded)
{
    if (bShuttingDown) return;
    const FString TraceId = Request.IsValid()
        ? Request->GetHeader(TEXT("X-Request-Id"))
        : FString();
    TSharedPtr<FJsonObject> Body;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(
        Response.IsValid() ? Response->GetContentAsString() : FString());
    bool bAccepted = false;
    double ResponseRoomEpoch = 0.0;
    double ResponseFencingToken = 0.0;
    // 同时校验 HTTP、JSON、接受标志及两类短期凭证，任一步失败都不开放玩家连接。
    if (!bSucceeded || !Response.IsValid() || Response->GetResponseCode() < 200
        || Response->GetResponseCode() >= 300 || !FJsonSerializer::Deserialize(Reader, Body)
        || !Body.IsValid() || !Body->TryGetBoolField(TEXT("accepted"), bAccepted) || !bAccepted
        || !Body->TryGetNumberField(TEXT("heartbeatIntervalSeconds"), HeartbeatIntervalSeconds)
        || !Body->TryGetStringField(TEXT("heartbeatCredential"), HeartbeatCredential)
        || !Body->TryGetStringField(TEXT("resultCredential"), ResultCredential)
        || !Body->TryGetNumberField(TEXT("roomEpoch"), ResponseRoomEpoch)
        || static_cast<int64>(ResponseRoomEpoch) != Config.RoomEpoch
        || !Body->TryGetNumberField(TEXT("fencingToken"), ResponseFencingToken)
        || static_cast<int64>(ResponseFencingToken) != Config.LeaseFencingToken
        || HeartbeatCredential.Len() < 32 || ResultCredential.Len() < 32)
    {
        UE_LOG(LogMahjongServer, Error,
            TEXT("Managed GameServer registration failed InstanceId=%s RoomId=%s Status=%d"),
            *Config.ServerInstanceId, *Config.RoomId, Response.IsValid() ? Response->GetResponseCode() : 0);
        GuiyangGameServerPrivate::WriteStructuredLog(
            TEXT("Error"), TraceId, Config.RoomId, Config.MatchId,
            Config.ServerInstanceId, TraceId,
            TEXT("Dedicated Server 注册失败"));
        return;
    }

    const TSharedPtr<FJsonObject>* BootstrapPointer = nullptr;
    FGuiyangManagedRoomDefinition Definition;
    FString BootstrapError;
    AGuiyangMahjongGameMode* GameMode = World.IsValid()
        ? World->GetAuthGameMode<AGuiyangMahjongGameMode>() : nullptr;
    // 控制面 Bootstrap 必须与启动作用域一致，并成功初始化权威 GameMode。
    if (!Body->TryGetObjectField(TEXT("roomBootstrap"), BootstrapPointer)
        || !BootstrapPointer
        || !FGuiyangManagedRoomDefinition::TryParse(*BootstrapPointer,
            Config.RoomId, Config.MatchId, Definition, BootstrapError)
        || !GameMode
        || !GameMode->InitializeManagedRoomAuthority(Definition, BootstrapError))
    {
        HeartbeatCredential.Reset();
        UE_LOG(LogMahjongServer, Error,
            TEXT("Managed GameServer bootstrap rejected InstanceId=%s RoomId=%s Reason=%s"),
            *Config.ServerInstanceId, *Config.RoomId,
            BootstrapError.IsEmpty() ? TEXT("ROOM_BOOTSTRAP_INVALID") : *BootstrapError);
        GuiyangGameServerPrivate::WriteStructuredLog(
            TEXT("Error"), TraceId, Config.RoomId, Config.MatchId,
            Config.ServerInstanceId, TraceId,
            TEXT("Dedicated Server 房间 Bootstrap 校验失败"));
        return;
    }

    // 一次性注册凭证使用后立即从内存清除。
    Config.RegistrationCredential.Reset();
    HeartbeatIntervalSeconds = FMath::Clamp(HeartbeatIntervalSeconds, 1, 60);
    bRegistered = true;
    if (World.IsValid())
    {
        World->GetTimerManager().SetTimer(
            HeartbeatTimer, this, &ThisClass::SendHeartbeat,
            static_cast<float>(HeartbeatIntervalSeconds), true,
            static_cast<float>(HeartbeatIntervalSeconds));
    }
    UE_LOG(LogMahjongServer, Display,
        TEXT("Managed GameServer registered InstanceId=%s RoomId=%s Port=%d"),
        *Config.ServerInstanceId, *Config.RoomId, Config.Port);
    GuiyangGameServerPrivate::WriteStructuredLog(
        TEXT("Information"), TraceId, Config.RoomId, Config.MatchId,
        Config.ServerInstanceId, TraceId,
        TEXT("Dedicated Server 注册并绑定房间成功"));
}

void UGuiyangGameServerBridge::SendHeartbeat()
{
    if (!bRegistered || bShuttingDown || !World.IsValid()) return;
    int32 RoundId = 0;
    const FString Lifecycle = BuildHeartbeatLifecycle(RoundId);
    TArray<TSharedPtr<FJsonValue>> ConnectedPlayerIds;
    TArray<FString> AuthorizedPlayerIds;
    bool bRecoveredAuthority = false;
    bool bSettlementEvidenceReady = true;
    FString AuthoritativeStateHash;
    if (const AGuiyangMahjongGameMode* GameMode =
        World->GetAuthGameMode<AGuiyangMahjongGameMode>())
    {
        // The signed join ticket authenticates a connection before the optional
        // client profile RPC runs. Counting PlayerState identities here used to
        // report zero, so Lobby reclaimed a live room after its empty timeout.
        GameMode->GetConnectedAuthorizedPlayerIds(AuthorizedPlayerIds);
        bRecoveredAuthority = GameMode->IsRecoveredAuthority();
        bSettlementEvidenceReady = GameMode->IsSettlementEvidenceReady();
        AuthoritativeStateHash = GameMode->GetAuthoritativeStateHash();
    }
    for (const FString& PlayerId : AuthorizedPlayerIds)
    {
        ConnectedPlayerIds.Add(MakeShared<FJsonValueString>(PlayerId));
    }
    const TSharedRef<FJsonObject> Body = MakeShared<FJsonObject>();
    // 显式携带版本便于 Lobby 拒绝未知语义；旧构建未携带时仍按 v1 兼容。
    Body->SetNumberField(
        TEXT("telemetrySchemaVersion"),
        GuiyangGameServerPrivate::RuntimeTelemetrySchemaVersion);
    Body->SetStringField(TEXT("roomId"), Config.RoomId);
    Body->SetNumberField(TEXT("roomEpoch"), static_cast<double>(Config.RoomEpoch));
    Body->SetNumberField(
        TEXT("fencingToken"),
        static_cast<double>(Config.LeaseFencingToken));
    Body->SetStringField(TEXT("heartbeatCredential"), HeartbeatCredential);
    Body->SetNumberField(TEXT("connectedPlayers"), ConnectedPlayerIds.Num());
    Body->SetArrayField(TEXT("connectedPlayerIds"), ConnectedPlayerIds);
    Body->SetStringField(TEXT("roomLifecycle"), Lifecycle);
    Body->SetNumberField(TEXT("roundId"), RoundId);
    Body->SetBoolField(TEXT("recoveredAuthority"), bRecoveredAuthority);
    Body->SetBoolField(TEXT("settlementEvidenceReady"), bSettlementEvidenceReady);
    if (!AuthoritativeStateHash.IsEmpty())
        Body->SetStringField(TEXT("authoritativeStateHash"), AuthoritativeStateHash);
    if (Lifecycle == TEXT("Playing") && GameStartedAtUtc.GetTicks() == 0)
    {
        GameStartedAtUtc = FDateTime::UtcNow();
    }
    if (GameStartedAtUtc.GetTicks() > 0)
    {
        Body->SetStringField(TEXT("gameStartedAtUtc"), GameStartedAtUtc.ToIso8601());
    }

    const float DeltaSeconds = World->GetDeltaSeconds();
    if (DeltaSeconds > SMALL_NUMBER)
    {
        Body->SetNumberField(TEXT("serverTickMilliseconds"), DeltaSeconds * 1000.0f);
        Body->SetNumberField(TEXT("serverFramesPerSecond"), 1.0f / DeltaSeconds);
    }
    Body->SetNumberField(
        TEXT("rpcReceivedCount"),
        static_cast<double>(AGuiyangMahjongPlayerController::GetServerRpcReceivedCount()));
    TArray<TSharedPtr<FJsonValue>> RpcMethods;
    for (const FGuiyangRpcMethodTelemetry& Metric :
        AGuiyangMahjongPlayerController::GetServerRpcTelemetry())
    {
        const TSharedRef<FJsonObject> Method = MakeShared<FJsonObject>();
        Method->SetStringField(TEXT("methodName"), Metric.MethodName);
        Method->SetNumberField(TEXT("receivedCount"), static_cast<double>(Metric.ReceivedCount));
        Method->SetNumberField(TEXT("rejectedCount"), static_cast<double>(Metric.RejectedCount));
        Method->SetNumberField(TEXT("failedCount"), static_cast<double>(Metric.FailedCount));
        Method->SetNumberField(TEXT("timeoutCount"), static_cast<double>(Metric.TimeoutCount));
        Method->SetNumberField(TEXT("p95DurationMilliseconds"), Metric.P95DurationMilliseconds);
        Method->SetNumberField(TEXT("p99DurationMilliseconds"), Metric.P99DurationMilliseconds);
        RpcMethods.Add(MakeShared<FJsonValueObject>(Method));
    }
    Body->SetArrayField(TEXT("rpcMethods"), RpcMethods);

    // UE 在 Windows 返回进程 WorkingSetSize，在 Linux 返回 /proc/self/status 的 VmRSS，
    // 两端均是当前 Dedicated Server 的 RSS/工作集，不是节点总已用内存。
    const FPlatformMemoryStats MemoryStats = FPlatformMemory::GetStats();
    Body->SetNumberField(
        TEXT("processMemoryBytes"),
        static_cast<double>(MemoryStats.UsedPhysical));
    // CPUTimePct 已按节点全部逻辑 CPU 容量归一化到 0～100，CPUTimePctRelative 才是单核口径。
    const FCPUTime CpuTime = FPlatformTime::GetCPUTime();
    Body->SetNumberField(
        TEXT("processCpuPercent"),
        static_cast<double>(FMath::Clamp(CpuTime.CPUTimePct, 0.0f, 100.0f)));
    Body->SetNumberField(TEXT("processCpuSampleWindowMilliseconds"), 250.0);
    UpdateNetworkTelemetry();
    Body->SetNumberField(TEXT("networkIngressBytes"), static_cast<double>(NetworkIngressBytes));
    Body->SetNumberField(TEXT("networkEgressBytes"), static_cast<double>(NetworkEgressBytes));

    TArray<TSharedPtr<FJsonValue>> Players;
    const AGuiyangMahjongGameState* GameState =
        World->GetGameState<AGuiyangMahjongGameState>();
    if (GameState)
    {
        const AGuiyangMahjongGameMode* GameMode =
            World->GetAuthGameMode<AGuiyangMahjongGameMode>();
        for (const FMahjongSeatInfo& Seat : GameState->RoomState.Seats)
        {
            if (!Seat.bOccupied || Seat.PlayerId.IsEmpty()) continue;
            const TSharedRef<FJsonObject> Player = MakeShared<FJsonObject>();
            Player->SetStringField(TEXT("playerId"), Seat.PlayerId);
            Player->SetNumberField(TEXT("seatIndex"), Seat.SeatIndex);
            Player->SetStringField(
                TEXT("connectionState"),
                Seat.bOnline ? TEXT("Connected") : TEXT("Disconnected"));
            Player->SetNumberField(
                TEXT("latencyMilliseconds"),
                FMath::Max(0, Seat.PingMilliseconds));
            if (GameMode)
            {
                FGuiyangPlayerConnectionTelemetry Connection;
                if (GameMode->GetPlayerConnectionTelemetry(Seat.PlayerId, Connection))
                {
                    Player->SetStringField(
                        TEXT("connectionChangedAtUtc"), Connection.ChangedAtUtc.ToIso8601());
                    Player->SetNumberField(
                        TEXT("connectionStateSequence"), static_cast<double>(Connection.Sequence));
                    Player->SetStringField(TEXT("connectionEventId"), Connection.EventId);
                    if (Connection.bDisconnected)
                    {
                        Player->SetStringField(
                            TEXT("disconnectedAtUtc"), Connection.DisconnectedAtUtc.ToIso8601());
                        Player->SetStringField(TEXT("disconnectReason"), Connection.DisconnectReason);
                    }
                    if (Connection.ReconnectedAtUtc.GetTicks() > 0)
                    {
                        Player->SetStringField(
                            TEXT("reconnectedAtUtc"), Connection.ReconnectedAtUtc.ToIso8601());
                    }
                }
                bool bTrustee = false;
                FDateTime TrusteeChangedAtUtc;
                if (GameMode->GetPlayerTrusteeTelemetry(
                    Seat.PlayerId, bTrustee, TrusteeChangedAtUtc))
                {
                    Player->SetBoolField(TEXT("trustee"), bTrustee);
                    Player->SetStringField(
                        TEXT("trusteeChangedAtUtc"), TrusteeChangedAtUtc.ToIso8601());
                }
            }
            Players.Add(MakeShared<FJsonValueObject>(Player));
        }
    }
    Body->SetArrayField(TEXT("players"), Players);
    if (Lifecycle == TEXT("Settling") && SettlementStatus.IsEmpty())
    {
        SettlementStatus = TEXT("Calculating");
    }
    if (!SettlementStatus.IsEmpty())
    {
        const TSharedRef<FJsonObject> Settlement = MakeShared<FJsonObject>();
        Settlement->SetStringField(TEXT("status"), SettlementStatus);
        Settlement->SetStringField(TEXT("matchId"), Config.MatchId);
        if (SettlementResultSequence > 0)
            Settlement->SetNumberField(TEXT("resultSequence"), static_cast<double>(SettlementResultSequence));
        if (!SettlementResultHash.IsEmpty())
            Settlement->SetStringField(TEXT("resultHash"), SettlementResultHash);
        if (SettlementSubmittedAtUtc.GetTicks() > 0)
            Settlement->SetStringField(TEXT("submittedAtUtc"), SettlementSubmittedAtUtc.ToIso8601());
        if (SettlementConfirmedAtUtc.GetTicks() > 0)
            Settlement->SetStringField(TEXT("confirmedAtUtc"), SettlementConfirmedAtUtc.ToIso8601());
        if (!SettlementFailureReason.IsEmpty())
            Settlement->SetStringField(TEXT("failureReason"), SettlementFailureReason);
        Body->SetObjectField(TEXT("settlement"), Settlement);
    }
    Body->SetStringField(TEXT("buildVersion"), Config.BuildVersion);
    Body->SetStringField(TEXT("sentAtUtc"), FDateTime::UtcNow().ToIso8601());

    const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request = FHttpModule::Get().CreateRequest();
    Request->SetURL(Config.LobbyInternalUrl + TEXT("/internal/gameservers/")
        + Config.ServerInstanceId + TEXT("/heartbeat"));
    Request->SetVerb(TEXT("POST"));
    Request->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
    Request->SetHeader(TEXT("X-Request-Id"), FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower));
    Request->SetContentAsString(GuiyangGameServerPrivate::SerializeJson(Body));
    Request->OnProcessRequestComplete().BindUObject(this, &ThisClass::HandleHeartbeatResponse);
    Request->ProcessRequest();
}

void UGuiyangGameServerBridge::HandleHeartbeatResponse(
    FHttpRequestPtr Request, FHttpResponsePtr Response, const bool bSucceeded)
{
    if (!bShuttingDown && (!bSucceeded || !Response.IsValid()
        || Response->GetResponseCode() < 200 || Response->GetResponseCode() >= 300))
    {
        UE_LOG(LogMahjongServer, Warning,
            TEXT("Managed GameServer heartbeat failed InstanceId=%s Status=%d"),
            *Config.ServerInstanceId, Response.IsValid() ? Response->GetResponseCode() : 0);
        const FString TraceId = Request.IsValid()
            ? Request->GetHeader(TEXT("X-Request-Id"))
            : FString();
        GuiyangGameServerPrivate::WriteStructuredLog(
            TEXT("Warning"), TraceId, Config.RoomId, Config.MatchId,
            Config.ServerInstanceId, TraceId,
            TEXT("Dedicated Server 心跳上报失败"));
    }
}

void UGuiyangGameServerBridge::SendPendingMatchResult()
{
    // 飞行标志避免定时器与回调并发发送同一结算。
    if (bShuttingDown || !bRegistered || bMatchResultRequestInFlight
        || PendingMatchResultBody.IsEmpty() || ResultCredential.Len() < 32)
        return;
    bMatchResultRequestInFlight = true;
    // 每次实际重试进入 Submitted；只有启动失败或回执失败才转为 Failed。
    SettlementStatus = TEXT("Submitted");
    SettlementFailureReason.Reset();
    const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request = FHttpModule::Get().CreateRequest();
    Request->SetURL(Config.GameDataInternalUrl + TEXT("/internal/settlements/") + PendingMatchId);
    Request->SetVerb(TEXT("POST"));
    Request->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
    Request->SetHeader(TEXT("Authorization"), TEXT("Bearer ") + ResultCredential);
    Request->SetHeader(TEXT("X-Request-Id"), FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower));
    // MatchId 与单调序号共同组成跨进程幂等键。
    // 当前一次提交覆盖整场最终结果，RoundNo 等于已完成局数，SettlementVersion 是单独版本。
    TSharedPtr<FJsonObject> PendingObject;
    const TSharedRef<TJsonReader<>> PendingReader = TJsonReaderFactory<>::Create(PendingMatchResultBody);
    double RoundNo = 0.0;
    if (!FJsonSerializer::Deserialize(PendingReader, PendingObject) || !PendingObject.IsValid()
        || !PendingObject->TryGetNumberField(TEXT("round_no"), RoundNo))
    {
        bMatchResultRequestInFlight = false;
        SettlementStatus = TEXT("Failed");
        SettlementFailureReason = TEXT("OutboxContractInvalid");
        return;
    }
    Request->SetHeader(TEXT("Idempotency-Key"), FString::Printf(
        TEXT("%s:%d:%lld"), *PendingMatchId, static_cast<int32>(RoundNo), PendingResultSequence));
    Request->SetTimeout(10.0f);
    Request->SetContentAsString(PendingMatchResultBody);
    Request->OnProcessRequestComplete().BindUObject(this, &ThisClass::HandleMatchResultResponse);
    if (!Request->ProcessRequest())
    {
        Request->OnProcessRequestComplete().Unbind();
        bMatchResultRequestInFlight = false;
        SettlementStatus = TEXT("Failed");
        SettlementFailureReason = TEXT("RequestStartFailed");
        ScheduleMatchResultRetry();
    }
}

void UGuiyangGameServerBridge::HandleMatchResultResponse(
    FHttpRequestPtr Request, FHttpResponsePtr Response, const bool bSucceeded)
{
    (void)Request;
    bMatchResultRequestInFlight = false;
    if (bShuttingDown || PendingMatchResultBody.IsEmpty()) return;
    TSharedPtr<FJsonObject> Body;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(
        Response.IsValid() ? Response->GetContentAsString() : FString());
    int64 AckSequence = 0;
    FString AckMatchId;
    // 仅当控制面回执的比赛和序号与当前待发送项完全一致时删除 Outbox。
    if (bSucceeded && Response.IsValid() && Response->GetResponseCode() >= 200
        && Response->GetResponseCode() < 300 && FJsonSerializer::Deserialize(Reader, Body)
        && Body.IsValid()
        && Body->TryGetStringField(TEXT("matchId"), AckMatchId)
        && Body->TryGetNumberField(TEXT("settlementVersion"), AckSequence)
        && AckMatchId == PendingMatchId && AckSequence == PendingResultSequence)
    {
        UE_LOG(LogMahjongServer, Display,
            TEXT("Final settlement acknowledged InstanceId=%s MatchId=%s Sequence=%lld"),
            *Config.ServerInstanceId, *PendingMatchId, PendingResultSequence);
        DeletePersistedMatchResult();
        SettlementStatus = TEXT("Accepted");
        SettlementConfirmedAtUtc = FDateTime::UtcNow();
        SettlementFailureReason.Reset();
        PendingMatchResultBody.Reset();
        PendingMatchId.Reset();
        PendingResultSequence = 0;
        MatchResultAttempt = 0;
        return;
    }
    UE_LOG(LogMahjongServer, Warning,
        TEXT("Final settlement report will retry InstanceId=%s MatchId=%s Sequence=%lld Status=%d"),
        *Config.ServerInstanceId, *PendingMatchId, PendingResultSequence,
        Response.IsValid() ? Response->GetResponseCode() : 0);
    SettlementStatus = TEXT("Failed");
    SettlementFailureReason = Response.IsValid()
        ? FString::Printf(TEXT("LobbyHttp%d"), Response->GetResponseCode())
        : TEXT("LobbyUnavailable");
    ScheduleMatchResultRetry();
}

void UGuiyangGameServerBridge::ScheduleMatchResultRetry()
{
    if (bShuttingDown || !World.IsValid() || PendingMatchResultBody.IsEmpty()) return;
    ++MatchResultAttempt;
    // 指数退避上限 30 秒，既避免故障时压垮 Lobby，也保证最终可恢复。
    const float DelaySeconds = FMath::Min(30.0f,
        static_cast<float>(1 << FMath::Min(MatchResultAttempt - 1, 5)));
    World->GetTimerManager().SetTimer(
        MatchResultRetryTimer, this, &ThisClass::SendPendingMatchResult, DelaySeconds, false);
}

bool UGuiyangGameServerBridge::PersistPendingMatchResult(const TSharedRef<FJsonObject>& Report) const
{
    IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
    // 已存在文件代表前一份结算尚未确认，禁止静默覆盖。
    if (PlatformFile.FileExists(*Config.MatchResultOutboxPath)) return false;

    const TSharedRef<FJsonObject> Envelope = MakeShared<FJsonObject>();
    // v2 保存完整强结算信封；Allocator 只原样恢复，不接触或重算 DS 签名。
    Envelope->SetNumberField(TEXT("version"), 2);
    Envelope->SetStringField(TEXT("matchId"), Config.MatchId);
    Envelope->SetObjectField(TEXT("report"), Report);
    // 先写临时文件再原子移动，避免宕机留下半截 JSON。
    const FString TemporaryPath = Config.MatchResultOutboxPath + TEXT(".tmp");
    PlatformFile.DeleteFile(*TemporaryPath);
    if (!FFileHelper::SaveStringToFile(
            GuiyangGameServerPrivate::SerializeJson(Envelope), *TemporaryPath,
            FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
    {
        return false;
    }
    if (!PlatformFile.MoveFile(*Config.MatchResultOutboxPath, *TemporaryPath))
    {
        PlatformFile.DeleteFile(*TemporaryPath);
        return false;
    }
    return true;
}

void UGuiyangGameServerBridge::DeletePersistedMatchResult() const
{
    IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
    if (PlatformFile.FileExists(*Config.MatchResultOutboxPath)
        && !PlatformFile.DeleteFile(*Config.MatchResultOutboxPath))
    {
        UE_LOG(LogMahjongServer, Warning,
            TEXT("Acknowledged final settlement outbox could not be deleted InstanceId=%s MatchId=%s"),
            *Config.ServerInstanceId, *Config.MatchId);
    }
}

void UGuiyangGameServerBridge::UpdateNetworkTelemetry()
{
    UNetDriver* NetDriver = World.IsValid() ? World->GetNetDriver() : nullptr;
    if (!NetDriver) return;
    const uint32 CurrentIngress = NetDriver->InTotalBytes;
    const uint32 CurrentEgress = NetDriver->OutTotalBytes;
    if (!bNetworkBaselineInitialized)
    {
        // 第一次采样把驱动已累计值纳入“进程启动以来”总量。
        SampledNetDriver = NetDriver;
        PreviousNetworkIngressBytes = CurrentIngress;
        PreviousNetworkEgressBytes = CurrentEgress;
        NetworkIngressBytes = CurrentIngress;
        NetworkEgressBytes = CurrentEgress;
        bNetworkBaselineInitialized = true;
        return;
    }
    if (SampledNetDriver.Get() != NetDriver)
    {
        // 新驱动只建立基线；驱动切换常伴随计数器归零，不能把旧值与新值相减。
        SampledNetDriver = NetDriver;
        PreviousNetworkIngressBytes = CurrentIngress;
        PreviousNetworkEgressBytes = CurrentEgress;
        bNetworkBaselineInitialized = true;
        return;
    }
    // 同一 uint32 计数器的无符号减法天然处理单次回绕，累计结果使用 uint64 避免再次溢出。
    NetworkIngressBytes += static_cast<uint32>(CurrentIngress - PreviousNetworkIngressBytes);
    NetworkEgressBytes += static_cast<uint32>(CurrentEgress - PreviousNetworkEgressBytes);
    PreviousNetworkIngressBytes = CurrentIngress;
    PreviousNetworkEgressBytes = CurrentEgress;
}

FString UGuiyangGameServerBridge::BuildHeartbeatLifecycle(int32& OutRoundId) const
{
    OutRoundId = 0;
    const AGuiyangMahjongGameState* State = World.IsValid()
        ? World->GetGameState<AGuiyangMahjongGameState>() : nullptr;
    if (!State) return TEXT("Waiting");
    OutRoundId = State->PublicTableState.RoundId;
    // 只向控制面暴露稳定的跨版本生命周期字符串。
    switch (State->RoomState.Lifecycle)
    {
    case EMahjongRoomLifecycle::Playing: return TEXT("Playing");
    case EMahjongRoomLifecycle::Settlement: return TEXT("Settling");
    case EMahjongRoomLifecycle::Closed: return TEXT("Closed");
    default: return TEXT("Waiting");
    }
}
