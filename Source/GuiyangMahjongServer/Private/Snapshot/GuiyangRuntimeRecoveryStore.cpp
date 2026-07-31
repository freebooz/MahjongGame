#include "Snapshot/GuiyangRuntimeRecoveryStore.h"

#include "HAL/FileManager.h"
#include "HAL/PlatformMisc.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "JsonObjectConverter.h"
#include "Server/GuiyangGameServerBridge.h"
#include "openssl/sha.h"

namespace
{
    FString Sha256Hex(const FString& Text)
    {
        const FTCHARToUTF8 Utf8(*Text);
        uint8 Digest[SHA256_DIGEST_LENGTH] = {};
        SHA256(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length(), Digest);
        return BytesToHex(Digest, UE_ARRAY_COUNT(Digest)).ToLower();
    }

    bool IsSafeIdentifier(const FString& Value)
    {
        FGuid Guid;
        return FGuid::Parse(Value, Guid);
    }
}

bool FGuiyangRuntimeRecoveryStore::Initialize(
    const FGuiyangGameServerLaunchConfig& Config,
    FString& OutError)
{
    if (FPaths::IsRelative(Config.RecoveryDirectory)
        || !IsSafeIdentifier(Config.MatchId) || !IsSafeIdentifier(Config.RoomId)
        || Config.RoomEpoch < 1)
    {
        OutError = TEXT("恢复仓库配置无效");
        return false;
    }
    MatchId = Config.MatchId;
    RoomId = Config.RoomId;
    CurrentRoomEpoch = Config.RoomEpoch;
    RootDirectory = Config.RecoveryDirectory;
    MatchDirectory = FPaths::Combine(Config.RecoveryDirectory, MatchId);
    FPaths::NormalizeDirectoryName(MatchDirectory);
    if (!IFileManager::Get().MakeDirectory(*MatchDirectory, true))
    {
        OutError = TEXT("无法创建比赛恢复目录");
        return false;
    }
    FString EveryText = FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_SNAPSHOT_EVERY_ACTIONS"));
    int32 ParsedEvery = 0;
    if (LexTryParseString(ParsedEvery, *EveryText)) SnapshotEveryActions = FMath::Clamp(ParsedEvery, 1, 5);
    FString IntervalText = FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_SNAPSHOT_MAX_INTERVAL_SECONDS"));
    int32 ParsedInterval = 0;
    if (LexTryParseString(ParsedInterval, *IntervalText))
        SnapshotMaxIntervalSeconds = FMath::Clamp(ParsedInterval, 1, 60);
    OutError.Reset();
    return true;
}

bool FGuiyangRuntimeRecoveryStore::AppendAction(
    FGuiyangActionEvidenceRecord& Record,
    FString& OutError)
{
    Record.ActionSequence = LastActionSequence + 1;
    Record.PreviousHash = LastActionHash;
    Record.ActionHash = FGuiyangActionEvidence::CalculateHash(Record);
    FString Json;
    if (Record.ActionHash.Len() != 64
        || !FJsonObjectConverter::UStructToJsonObjectString(Record, Json))
    {
        OutError = TEXT("动作证据序列化失败");
        return false;
    }
    const FString Path = FPaths::Combine(
        MatchDirectory, FString::Printf(TEXT("actions-%lld.jsonl"), CurrentRoomEpoch));
    if (!FFileHelper::SaveStringToFile(
            Json + LINE_TERMINATOR, *Path, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM,
            &IFileManager::Get(), FILEWRITE_Append))
    {
        OutError = TEXT("动作证据追加失败");
        return false;
    }
    LastActionSequence = Record.ActionSequence;
    LastActionHash = Record.ActionHash;
    OutError.Reset();
    return true;
}

FString FGuiyangRuntimeRecoveryStore::CalculateStateHash(
    const FGuiyangAuthoritativeSnapshot& Snapshot)
{
    // 将 StateHash 自身清空后序列化全部权威字段，确保房间、托管和公平性状态也受摘要保护。
    FGuiyangAuthoritativeSnapshot Canonical = Snapshot;
    Canonical.StateHash.Reset();
    FString Json;
    if (!FJsonObjectConverter::UStructToJsonObjectString(Canonical, Json)) return FString();
    return Sha256Hex(TEXT("snapshot-state-v1|") + Json);
}

FString FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(
    const FMahjongTableRecoveryState& TableState)
{
    FString TableJson;
    if (!FJsonObjectConverter::UStructToJsonObjectString(TableState, TableJson)) return FString();
    return Sha256Hex(TEXT("table-state-v1|") + TableJson);
}

bool FGuiyangRuntimeRecoveryStore::MaterializeFinalEvidence(
    TArray<FGuiyangRecoveryEvidenceObject>& OutObjects,
    FString& OutError) const
{
    OutObjects.Reset();
    const TArray<TPair<FString, FString>> Sources = {
        { TEXT("snapshot"), FPaths::Combine(
            MatchDirectory, FString::Printf(TEXT("snapshot-%lld.json"), CurrentRoomEpoch)) },
        { TEXT("actions"), FPaths::Combine(
            MatchDirectory, FString::Printf(TEXT("actions-%lld.jsonl"), CurrentRoomEpoch)) }
    };
    for (const TPair<FString, FString>& Source : Sources)
    {
        TArray<uint8> Bytes;
        if (!FFileHelper::LoadFileToArray(Bytes, *Source.Value) || Bytes.IsEmpty())
        {
            OutError = FString::Printf(TEXT("结算证据文件缺失或为空：%s"), *Source.Key);
            OutObjects.Reset();
            return false;
        }
        uint8 Digest[SHA256_DIGEST_LENGTH] = {};
        SHA256(Bytes.GetData(), Bytes.Num(), Digest);
        const FString Hash = BytesToHex(Digest, UE_ARRAY_COUNT(Digest)).ToLower();
        const FString Extension = Source.Key == TEXT("actions") ? TEXT("jsonl") : TEXT("json");
        const FString ObjectKey = FString::Printf(
            TEXT("matches/%s/epochs/%lld/%s/%s.%s"),
            *MatchId, CurrentRoomEpoch, *Hash, *Source.Key, *Extension);
        const FString Target = FPaths::Combine(RootDirectory, ObjectKey);
        const FString Parent = FPaths::GetPath(Target);
        if (!IFileManager::Get().MakeDirectory(*Parent, true))
        {
            OutError = TEXT("无法创建内容寻址证据目录");
            OutObjects.Reset();
            return false;
        }
        if (IFileManager::Get().FileExists(*Target))
        {
            TArray<uint8> Existing;
            if (!FFileHelper::LoadFileToArray(Existing, *Target) || Existing != Bytes)
            {
                OutError = TEXT("内容寻址证据键已存在但内容不一致");
                OutObjects.Reset();
                return false;
            }
        }
        else
        {
            const FString Temporary = Target + TEXT(".tmp");
            if (!FFileHelper::SaveArrayToFile(Bytes, *Temporary)
                || !IFileManager::Get().Move(*Target, *Temporary, false, true, false, true))
            {
                IFileManager::Get().Delete(*Temporary, false, true, true);
                OutError = TEXT("内容寻址证据原子写入失败");
                OutObjects.Reset();
                return false;
            }
        }
        FGuiyangRecoveryEvidenceObject Object;
        Object.Kind = Source.Key;
        Object.ObjectKey = ObjectKey;
        Object.AbsolutePath = Target;
        Object.Sha256 = Hash;
        Object.SizeBytes = Bytes.Num();
        OutObjects.Add(MoveTemp(Object));
    }
    OutError.Reset();
    return true;
}

bool FGuiyangRuntimeRecoveryStore::SaveSnapshot(
    FGuiyangAuthoritativeSnapshot& Snapshot,
    FString& OutError)
{
    Snapshot.ActionSequence = LastActionSequence;
    Snapshot.PreviousActionHash = LastActionHash;
    Snapshot.StateHash = CalculateStateHash(Snapshot);
    FString Json;
    if (Snapshot.StateHash.Len() != 64
        || !FJsonObjectConverter::UStructToJsonObjectString(Snapshot, Json))
    {
        OutError = TEXT("权威快照序列化失败");
        return false;
    }
    const FString Target = FPaths::Combine(
        MatchDirectory, FString::Printf(TEXT("snapshot-%lld.json"), CurrentRoomEpoch));
    const FString Temporary = Target + TEXT(".tmp");
    if (!FFileHelper::SaveStringToFile(
            Json, *Temporary, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
    {
        OutError = TEXT("权威快照临时文件写入失败");
        return false;
    }
    if (!IFileManager::Get().Move(*Target, *Temporary, true, true, false, true))
    {
        IFileManager::Get().Delete(*Temporary, false, true, true);
        OutError = TEXT("权威快照原子替换失败");
        return false;
    }
    OutError.Reset();
    return true;
}

bool FGuiyangRuntimeRecoveryStore::LoadLatestPriorEpoch(
    FGuiyangAuthoritativeSnapshot& OutSnapshot,
    TArray<FGuiyangActionEvidenceRecord>& OutActions,
    FString& OutError) const
{
    OutActions.Reset();
    TArray<FString> Files;
    IFileManager::Get().FindFiles(Files, *FPaths::Combine(MatchDirectory, TEXT("snapshot-*.json")), true, false);
    int64 BestEpoch = 0;
    FString BestPath;
    for (const FString& File : Files)
    {
        const FString Stem = FPaths::GetBaseFilename(File);
        int64 Epoch = 0;
        if (LexTryParseString(Epoch, *Stem.RightChop(9)) && Epoch > BestEpoch && Epoch < CurrentRoomEpoch)
        {
            BestEpoch = Epoch;
            BestPath = FPaths::Combine(MatchDirectory, File);
        }
    }
    if (BestPath.IsEmpty())
    {
        OutError.Reset();
        return false;
    }
    FString Json;
    if (!FFileHelper::LoadFileToString(Json, *BestPath)
        || !FJsonObjectConverter::JsonObjectStringToUStruct(Json, &OutSnapshot, 0, 0)
        || OutSnapshot.MatchId != MatchId || OutSnapshot.RoomId != RoomId
        || OutSnapshot.RoomEpoch != BestEpoch
        || OutSnapshot.StateHash != CalculateStateHash(OutSnapshot))
    {
        OutError = TEXT("历史权威快照损坏或作用域不匹配");
        return false;
    }
    const FString ActionPath = FPaths::Combine(
        MatchDirectory, FString::Printf(TEXT("actions-%lld.jsonl"), BestEpoch));
    TArray<FString> Lines;
    FFileHelper::LoadFileToStringArray(Lines, *ActionPath);
    FString PreviousHash = OutSnapshot.PreviousActionHash;
    for (const FString& Line : Lines)
    {
        if (Line.TrimStartAndEnd().IsEmpty()) continue;
        FGuiyangActionEvidenceRecord Record;
        if (!FJsonObjectConverter::JsonObjectStringToUStruct(Line, &Record, 0, 0))
        {
            OutError = TEXT("动作证据文件包含无法解析的记录");
            return false;
        }
        if (Record.ActionSequence <= OutSnapshot.ActionSequence) continue;
        if (Record.MatchId != MatchId || Record.RoomId != RoomId || Record.RoomEpoch != BestEpoch
            || Record.PreviousHash != PreviousHash
            || Record.ActionHash != FGuiyangActionEvidence::CalculateHash(Record))
        {
            OutError = TEXT("快照后的动作证据链不连续");
            OutActions.Reset();
            return false;
        }
        PreviousHash = Record.ActionHash;
        OutActions.Add(MoveTemp(Record));
    }
    OutActions.Sort([](const FGuiyangActionEvidenceRecord& Left, const FGuiyangActionEvidenceRecord& Right)
    {
        return Left.ActionSequence < Right.ActionSequence;
    });
    OutError.Reset();
    return true;
}
