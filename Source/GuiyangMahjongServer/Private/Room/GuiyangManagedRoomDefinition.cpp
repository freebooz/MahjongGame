#include "Room/GuiyangManagedRoomDefinition.h"

#include "Dom/JsonObject.h"

namespace GuiyangManagedRoomDefinitionPrivate
{
    /** 规则 ID 只允许有限 ASCII 字符，避免日志、指标和存储键注入。 */
    bool IsRuleIdValid(const FString& Value)
    {
        if (Value.IsEmpty() || Value.Len() > 64) return false;
        for (const TCHAR Character : Value)
        {
            const bool bAsciiAlphaNumeric = (Character >= TEXT('a') && Character <= TEXT('z'))
                || (Character >= TEXT('A') && Character <= TEXT('Z'))
                || (Character >= TEXT('0') && Character <= TEXT('9'));
            if (!bAsciiAlphaNumeric && Character != TEXT('-') && Character != TEXT('_') && Character != TEXT('.'))
                return false;
        }
        return true;
    }

    /** 严格读取可选整数，拒绝小数及 int32 范围外数值。 */
    bool TryReadOptionalInteger(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field, int32& OutValue)
    {
        if (!Object->HasField(Field)) return true;
        double Number = 0.0;
        if (!Object->TryGetNumberField(Field, Number) || !FMath::IsNearlyEqual(Number, FMath::RoundToDouble(Number))
            || Number < static_cast<double>(MIN_int32) || Number > static_cast<double>(MAX_int32))
            return false;
        OutValue = static_cast<int32>(Number);
        return true;
    }

    bool TryReadOptionalBoolean(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field, bool& OutValue)
    {
        return !Object->HasField(Field) || Object->TryGetBoolField(Field, OutValue);
    }

    /** 同时接受稳定字符串枚举和旧版数字枚举。 */
    bool TryReadTileSetMode(const TSharedPtr<FJsonObject>& Object, FMahjongRuleConfig& Config)
    {
        if (!Object->HasField(TEXT("tileSetMode"))) return true;
        FString Value;
        if (Object->TryGetStringField(TEXT("tileSetMode"), Value))
        {
            if (Value.Equals(TEXT("Suited108"), ESearchCase::IgnoreCase))
                Config.TileSetMode = EMahjongTileSetMode::Suited108;
            else if (Value.Equals(TEXT("Standard136"), ESearchCase::IgnoreCase))
                Config.TileSetMode = EMahjongTileSetMode::Standard136;
            else return false;
            return true;
        }
        int32 Number = 0;
        if (!TryReadOptionalInteger(Object, TEXT("tileSetMode"), Number) || Number < 0 || Number > 1) return false;
        Config.TileSetMode = static_cast<EMahjongTileSetMode>(Number);
        return true;
    }

    bool TryReadJiCountingScope(const TSharedPtr<FJsonObject>& Object, FMahjongRuleConfig& Config)
    {
        if (!Object->HasField(TEXT("jiCountingScope"))) return true;
        FString Value;
        if (Object->TryGetStringField(TEXT("jiCountingScope"), Value))
        {
            if (Value.Equals(TEXT("HandOnly"), ESearchCase::IgnoreCase)) Config.JiCountingScope = EMahjongJiCountingScope::HandOnly;
            else if (Value.Equals(TEXT("HandAndMeld"), ESearchCase::IgnoreCase)) Config.JiCountingScope = EMahjongJiCountingScope::HandAndMeld;
            else if (Value.Equals(TEXT("HandAndDiscard"), ESearchCase::IgnoreCase)) Config.JiCountingScope = EMahjongJiCountingScope::HandAndDiscard;
            else if (Value.Equals(TEXT("HandMeldAndDiscard"), ESearchCase::IgnoreCase)) Config.JiCountingScope = EMahjongJiCountingScope::HandMeldAndDiscard;
            else return false;
            return true;
        }
        int32 Number = 0;
        if (!TryReadOptionalInteger(Object, TEXT("jiCountingScope"), Number) || Number < 0 || Number > 3) return false;
        Config.JiCountingScope = static_cast<EMahjongJiCountingScope>(Number);
        return true;
    }
}

bool FGuiyangManagedRoomDefinition::TryParse(const TSharedPtr<FJsonObject>& Bootstrap,
    const FString& ExpectedRoomId, const FString& ExpectedMatchId,
    FGuiyangManagedRoomDefinition& OutDefinition, FString& OutError)
{
    // 每次解析先清空输出，失败时调用方不会误用上一次成功结果。
    OutDefinition = FGuiyangManagedRoomDefinition();
    OutError = TEXT("ROOM_BOOTSTRAP_INVALID");
    if (!Bootstrap.IsValid()
        || !Bootstrap->TryGetStringField(TEXT("roomId"), OutDefinition.BackendRoomId)
        || !Bootstrap->TryGetStringField(TEXT("roomCode"), OutDefinition.RoomCode)
        || !Bootstrap->TryGetStringField(TEXT("matchId"), OutDefinition.MatchId)
        || !Bootstrap->TryGetStringField(TEXT("ownerPlayerId"), OutDefinition.OwnerPlayerId)
        || !Bootstrap->TryGetNumberField(TEXT("roundCount"), OutDefinition.RoundCount)
        || !Bootstrap->TryGetNumberField(TEXT("maximumPlayers"), OutDefinition.MaximumPlayers)
        || !Bootstrap->TryGetBoolField(TEXT("publicRoom"), OutDefinition.bPublicRoom)
        || !Bootstrap->TryGetBoolField(TEXT("autoStart"), OutDefinition.bAutoStart)
        || !Bootstrap->TryGetBoolField(TEXT("passwordProtected"), OutDefinition.bPasswordProtected))
        return false;

    // 校验控制面不可变字段及四人桌边界。
    FGuid ParsedGuid;
    if (!FGuid::Parse(OutDefinition.BackendRoomId, ParsedGuid)
        || !FGuid::Parse(OutDefinition.MatchId, ParsedGuid)
        || OutDefinition.RoomCode.Len() != 6 || !OutDefinition.RoomCode.IsNumeric()
        || OutDefinition.OwnerPlayerId.TrimStartAndEnd().Len() < 1
        || OutDefinition.OwnerPlayerId.TrimStartAndEnd().Len() > 80
        || OutDefinition.RoundCount < 1 || OutDefinition.RoundCount > 16
        || OutDefinition.MaximumPlayers != 4)
        return false;

    // Bootstrap 必须与启动参数作用域一致，防止错误分配或跨房间注入。
    if (!OutDefinition.BackendRoomId.Equals(ExpectedRoomId, ESearchCase::IgnoreCase)
        || !OutDefinition.MatchId.Equals(ExpectedMatchId, ESearchCase::IgnoreCase))
    {
        OutError = TEXT("ROOM_BOOTSTRAP_SCOPE_MISMATCH");
        return false;
    }

    const TSharedPtr<FJsonObject>* RulesPointer = nullptr;
    if (!Bootstrap->TryGetObjectField(TEXT("ruleSnapshot"), RulesPointer) || !RulesPointer || !RulesPointer->IsValid())
        return false;
    const TSharedPtr<FJsonObject>& Rules = *RulesPointer;
    FMahjongRuleConfig Config;
    FString RuleId;
    if (!Rules->TryGetStringField(TEXT("ruleId"), RuleId)
        || !GuiyangManagedRoomDefinitionPrivate::IsRuleIdValid(RuleId))
        return false;
    Config.RuleId = FName(*RuleId);

    // 宏仅用于集中执行同一严格读取策略，随后仍由规则快照做语义范围校验。
#define READ_RULE_INT(JsonName, Member) \
    if (!GuiyangManagedRoomDefinitionPrivate::TryReadOptionalInteger(Rules, TEXT(JsonName), Config.Member)) return false
#define READ_RULE_BOOL(JsonName, Member) \
    if (!GuiyangManagedRoomDefinitionPrivate::TryReadOptionalBoolean(Rules, TEXT(JsonName), Config.Member)) return false
    READ_RULE_INT("ruleVersion", RuleVersion);
    READ_RULE_INT("baseScore", BaseScore);
    READ_RULE_INT("jiScore", JiScore);
    READ_RULE_INT("basicJiValue", BasicJiValue);
    READ_RULE_INT("flippedJiValue", FlippedJiValue);
    READ_RULE_INT("wuGuJiValue", WuGuJiValue);
    READ_RULE_INT("chongFengJiValue", ChongFengJiValue);
    READ_RULE_INT("wuGuChongFengJiValue", WuGuChongFengJiValue);
    READ_RULE_INT("zeRenJiValue", ZeRenJiValue);
    READ_RULE_INT("wuGuZeRenJiValue", WuGuZeRenJiValue);
    READ_RULE_INT("gangScore", GangScore);
    READ_RULE_INT("ziMoMultiplier", ZiMoMultiplier);
    READ_RULE_INT("dianPaoMultiplier", DianPaoMultiplier);
    READ_RULE_INT("reconnectTimeoutSeconds", ReconnectTimeoutSeconds);
    READ_RULE_INT("turnTimeoutSeconds", TurnTimeoutSeconds);
    READ_RULE_INT("reactionTimeoutSeconds", ReactionTimeoutSeconds);
    READ_RULE_BOOL("enableChongFengJi", bEnableChongFengJi);
    READ_RULE_BOOL("enableZeRenJi", bEnableZeRenJi);
    READ_RULE_BOOL("enableWuGuJi", bEnableWuGuJi);
    READ_RULE_BOOL("wuGuCanChongFeng", bWuGuCanChongFeng);
    READ_RULE_BOOL("wuGuCanZeRen", bWuGuCanZeRen);
    READ_RULE_BOOL("enableQiangGangHu", bEnableQiangGangHu);
    READ_RULE_BOOL("enableYiPaoDuoXiang", bEnableYiPaoDuoXiang);
    READ_RULE_BOOL("enableQiDui", bEnableQiDui);
    READ_RULE_BOOL("drawGameDealerContinues", bDrawGameDealerContinues);
    READ_RULE_BOOL("enableTimeoutAutoPlay", bEnableTimeoutAutoPlay);
#undef READ_RULE_BOOL
#undef READ_RULE_INT
    if (!GuiyangManagedRoomDefinitionPrivate::TryReadTileSetMode(Rules, Config)
        || !GuiyangManagedRoomDefinitionPrivate::TryReadJiCountingScope(Rules, Config))
        return false;

    // 保存规范化后的房主标识和带哈希的不可变规则快照。
    OutDefinition.OwnerPlayerId = OutDefinition.OwnerPlayerId.TrimStartAndEnd();
    OutDefinition.RuleSnapshot = UGuiyangRuleSnapshotLibrary::CreateSnapshot(Config);
    return UGuiyangRuleSnapshotLibrary::VerifySnapshot(OutDefinition.RuleSnapshot);
}
