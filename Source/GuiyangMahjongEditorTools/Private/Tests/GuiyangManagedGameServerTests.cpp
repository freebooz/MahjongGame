#include "Misc/AutomationTest.h"


#include "Dom/JsonObject.h"
#include "Runtime/Launch/Resources/Version.h"
#include "Room/GuiyangManagedRoomDefinition.h"
#include "Room/GuiyangRoomManager.h"
#include "Server/GuiyangGameServerBridge.h"
#include "Server/GuiyangAgonesLifecycleSubsystem.h"

#if WITH_DEV_AUTOMATION_TESTS

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangAgonesActivationTest,
    "GuiyangMahjong.GameServer.AgonesActivation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FGuiyangAgonesActivationTest::RunTest(const FString& Parameters)
{
    TestFalse(TEXT("Agones must be opt-in"),
        UGuiyangAgonesLifecycleSubsystem::IsAgonesRequested(TEXT(""), TEXT("")));
    TestTrue(TEXT("Command line can select Agones"),
        UGuiyangAgonesLifecycleSubsystem::IsAgonesRequested(
            TEXT("-MahjongOrchestrator=Agones"), TEXT("")));
    TestTrue(TEXT("Environment can select Agones case-insensitively"),
        UGuiyangAgonesLifecycleSubsystem::IsAgonesRequested(TEXT(""), TEXT("agones")));
    TestFalse(TEXT("Local allocator selection must not activate Agones"),
        UGuiyangAgonesLifecycleSubsystem::IsAgonesRequested(
            TEXT("-MahjongOrchestrator=Allocator"), TEXT("agones")));

    FGameServerResponse Response;
    Response.Status.State = TEXT("Allocated");
    Response.Status.Address = TEXT("203.0.113.25");
    FPort Port;
    Port.Name = TEXT("game");
    Port.Port = 30123;
    Response.Status.Ports.Add(Port);
    Response.ObjectMeta.Annotations = {
        { TEXT("mahjong.freebooz/room-id"), TEXT("11111111-1111-1111-1111-111111111111") },
        { TEXT("mahjong.freebooz/match-id"), TEXT("22222222-2222-2222-2222-222222222222") },
        { TEXT("mahjong.freebooz/server-instance-id"), TEXT("33333333-3333-3333-3333-333333333333") },
        { TEXT("mahjong.freebooz/registration-credential"), TEXT("registration-credential-long-enough") },
        { TEXT("mahjong.freebooz/lobby-internal-url"), TEXT("http://mahjong-lobby:8080") },
        { TEXT("mahjong.freebooz/gamedata-internal-url"), TEXT("http://mahjong-gamedata:8080") },
        { TEXT("mahjong.freebooz/build-version"), TEXT("test-build") },
        { TEXT("mahjong.freebooz/room-epoch"), TEXT("1") },
        { TEXT("mahjong.freebooz/fencing-token"), TEXT("1") }
    };
    FGuiyangGameServerLaunchConfig Config;
    FString Error;
    TestTrue(TEXT("Allocated metadata should produce a managed launch config"),
        UGuiyangAgonesLifecycleSubsystem::TryBuildLaunchConfig(
            Response,
            TEXT("test-only-join-ticket-signing-key-long-enough"),
            TEXT("/tmp/mahjong-result.json"),
            Config,
            Error));
    TestEqual(TEXT("Agones route must use the allocated host port"), Config.Port, 30123);
    TestEqual(TEXT("Agones route must use the allocated address"),
        Config.AdvertisedIp, FString(TEXT("203.0.113.25")));

    Response.ObjectMeta.Annotations.Remove(TEXT("mahjong.freebooz/room-id"));
    TestFalse(TEXT("Incomplete allocation metadata must be rejected"),
        UGuiyangAgonesLifecycleSubsystem::TryBuildLaunchConfig(
            Response,
            TEXT("test-only-join-ticket-signing-key-long-enough"),
            TEXT("/tmp/mahjong-result.json"),
            Config,
            Error));
    return true;
}

namespace GuiyangManagedGameServerTests
{
    constexpr const TCHAR* SigningKey = TEXT("test-only-join-ticket-signing-key-long-enough");
    constexpr const TCHAR* RoomId = TEXT("11111111-1111-1111-1111-111111111111");
    constexpr const TCHAR* MatchId = TEXT("22222222-2222-2222-2222-222222222222");
    constexpr const TCHAR* InstanceId = TEXT("33333333-3333-3333-3333-333333333333");
    constexpr const TCHAR* ValidTicket = TEXT(
        "eyJ0aWNrZXRJZCI6ImFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhIiwicGxheWVySWQiOiJwbGF5ZXItMSIsImRpc3BsYXlOYW1lIjoi5rWL6K-V546p5a62Iiwicm9vbUlkIjoiMTExMTExMTEtMTExMS0xMTExLTExMTEtMTExMTExMTExMTExIiwibWF0Y2hJZCI6IjIyMjIyMjIyLTIyMjItMjIyMi0yMjIyLTIyMjIyMjIyMjIyMiIsInNlYXRJZCI6MCwic2Vzc2lvbklkIjoiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmIiLCJzZXNzaW9uRXBvY2giOjQsInNlY3VyaXR5RXBvY2giOjIsInNlcnZlckluc3RhbmNlSWQiOiIzMzMzMzMzMy0zMzMzLTMzMzMtMzMzMy0zMzMzMzMzMzMzMzMiLCJyb29tRXBvY2giOjEsImNsaWVudEJ1aWxkIjoidGVzdC1idWlsZCIsInByb3RvY29sVmVyc2lvbiI6MSwicnVsZVNldFZlcnNpb24iOiJndWl5YW5nLXpodW9qaS12MSIsImlzc3VlZEF0VW5peFNlY29uZHMiOjIwMDAwMDAwMDAsImV4cGlyZXNBdFVuaXhTZWNvbmRzIjoyMDAwMDAwMDMwLCJub25jZSI6IjQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0In0."
        "QQSkPSO63p5BRSmDgf8FtNTGOIqZ2d4--0YIzaHbONg");

    FGuiyangGameServerLaunchConfig MakeConfig()
    {
        FGuiyangGameServerLaunchConfig Config;
        Config.RoomId = RoomId;
        Config.MatchId = MatchId;
        Config.ServerInstanceId = InstanceId;
        Config.JoinTicketSigningKey = SigningKey;
        Config.BuildVersion = TEXT("test-build");
        Config.CompatibleClientBuilds = { TEXT("test-build") };
        Config.RuleSetVersion = TEXT("guiyang-zhuoji-v1");
        Config.ProtocolVersion = TEXT("1");
        Config.MatchResultOutboxPath = FString::Printf(TEXT("C:/mahjong-outbox/%s.json"), InstanceId);
        return Config;
    }

    TSharedRef<FJsonObject> MakeBootstrap(const FString& BackendRoomId, const FString& Match,
        const FString& RoomCode, const int32 BaseScore = 3)
    {
        const TSharedRef<FJsonObject> Rules = MakeShared<FJsonObject>();
        Rules->SetStringField(TEXT("ruleId"), TEXT("GuiyangMainstreamV1"));
        Rules->SetNumberField(TEXT("ruleVersion"), 2);
        Rules->SetStringField(TEXT("tileSetMode"), TEXT("Suited108"));
        Rules->SetStringField(TEXT("jiCountingScope"), TEXT("HandAndMeld"));
        Rules->SetNumberField(TEXT("baseScore"), BaseScore);
        Rules->SetNumberField(TEXT("turnTimeoutSeconds"), 21);
        Rules->SetNumberField(TEXT("reactionTimeoutSeconds"), 9);
        Rules->SetBoolField(TEXT("enableChongFengJi"), true);

        const TSharedRef<FJsonObject> Bootstrap = MakeShared<FJsonObject>();
        Bootstrap->SetStringField(TEXT("roomId"), BackendRoomId);
        Bootstrap->SetStringField(TEXT("roomCode"), RoomCode);
        Bootstrap->SetStringField(TEXT("matchId"), Match);
        Bootstrap->SetStringField(TEXT("ownerPlayerId"), TEXT("owner-1"));
        Bootstrap->SetNumberField(TEXT("roundCount"), 8);
        Bootstrap->SetNumberField(TEXT("maximumPlayers"), 4);
        Bootstrap->SetBoolField(TEXT("publicRoom"), true);
        Bootstrap->SetBoolField(TEXT("autoStart"), true);
        Bootstrap->SetBoolField(TEXT("passwordProtected"), false);
        Bootstrap->SetObjectField(TEXT("ruleSnapshot"), Rules);
        return Bootstrap;
    }
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangManagedLaunchConfigTest,
    "GuiyangMahjong.GameServer.ManagedLaunchConfig",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::ServerContext
        | EAutomationTestFlags::EngineFilter)

bool FGuiyangManagedLaunchConfigTest::RunTest(const FString& Parameters)
{
    const FString CommandLine = FString::Printf(
        TEXT("-MahjongManagedGameServer -RoomId=%s -MatchId=%s -ServerInstanceId=%s -Port=19000 ")
        TEXT("-LobbyInternalUrl=http://127.0.0.1:18080 ")
        TEXT("-BuildVersion=test-build -RuleSetVersion=guiyang-zhuoji-v1 -ProtocolVersion=1 ")
        TEXT("-AdvertisedIp=127.0.0.1 -RoomEpoch=1 -LeaseFencingToken=1"),
        GuiyangManagedGameServerTests::RoomId,
        GuiyangManagedGameServerTests::MatchId,
        GuiyangManagedGameServerTests::InstanceId);
    FGuiyangGameServerLaunchConfig Config;
    FString Error;
    TestTrue(TEXT("完整托管启动参数应通过"), FGuiyangGameServerLaunchConfig::TryParse(
        *CommandLine, GuiyangManagedGameServerTests::SigningKey,
        TEXT("registration-credential-which-is-long-enough"),
        FString::Printf(TEXT("C:/mahjong-outbox/%s.json"), GuiyangManagedGameServerTests::InstanceId),
        Config, Error));
    TestEqual(TEXT("端口必须保持"), Config.Port, 19000);
    TestEqual(TEXT("房间绑定必须保持"), Config.RoomId, FString(GuiyangManagedGameServerTests::RoomId));

    FGuiyangGameServerLaunchConfig Invalid;
    TestFalse(TEXT("缺少签名密钥必须拒绝"), FGuiyangGameServerLaunchConfig::TryParse(
        *CommandLine, FString(), TEXT("registration-credential-which-is-long-enough"),
        FString::Printf(TEXT("C:/mahjong-outbox/%s.json"), GuiyangManagedGameServerTests::InstanceId),
        Invalid, Error));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangJoinTicketValidationTest,
    "GuiyangMahjong.GameServer.JoinTicketValidation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::ServerContext
        | EAutomationTestFlags::EngineFilter)

bool FGuiyangJoinTicketValidationTest::RunTest(const FString& Parameters)
{
    FGuiyangJoinTicketValidator Validator(GuiyangManagedGameServerTests::MakeConfig());
    FGuiyangJoinTicketClaims Claims;
    FString Error;
    TestTrue(TEXT("签名、范围和期限正确的票据应通过"), Validator.ValidateAndConsume(
        GuiyangManagedGameServerTests::ValidTicket, TEXT("player-1"), 2000000000, Claims, Error));
    TestEqual(TEXT("PlayerId 必须来自已签名载荷"), Claims.PlayerId, FString(TEXT("player-1")));
    TestEqual(TEXT("显示名必须来自已签名载荷"), Claims.DisplayName, FString(TEXT("测试玩家")));
    TestEqual(TEXT("座位必须来自已签名载荷"), Claims.SeatId, 0);
    TestEqual(TEXT("会话必须来自已签名载荷"), Claims.SessionId,
        FString(TEXT("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")));
    TestEqual(TEXT("会话 Epoch 必须保持"), Claims.SessionEpoch, static_cast<int64>(4));

    FGuiyangJoinTicketClaims ReplayClaims;
    TestFalse(TEXT("同一 nonce 不得重复消费"), Validator.ValidateAndConsume(
        GuiyangManagedGameServerTests::ValidTicket, TEXT("player-1"), 2000000001, ReplayClaims, Error));
    TestEqual(TEXT("重放必须返回稳定错误"), Error, FString(TEXT("JOIN_TICKET_REPLAYED")));

    FGuiyangJoinTicketValidator WrongPlayerValidator(GuiyangManagedGameServerTests::MakeConfig());
    TestFalse(TEXT("票据不得切换玩家"), WrongPlayerValidator.ValidateAndConsume(
        GuiyangManagedGameServerTests::ValidTicket, TEXT("player-2"), 2000000000, Claims, Error));
    TestEqual(TEXT("玩家不匹配必须归类为范围错误"), Error, FString(TEXT("JOIN_TICKET_SCOPE_MISMATCH")));

    FGuiyangGameServerLaunchConfig WrongScopeConfig = GuiyangManagedGameServerTests::MakeConfig();
    WrongScopeConfig.ServerInstanceId = TEXT("55555555-5555-5555-5555-555555555555");
    FGuiyangJoinTicketValidator WrongScopeValidator(WrongScopeConfig);
    TestFalse(TEXT("票据不得跨 GameServer 实例使用"), WrongScopeValidator.ValidateAndConsume(
        GuiyangManagedGameServerTests::ValidTicket, TEXT("player-1"), 2000000000, Claims, Error));
    TestEqual(TEXT("实例不匹配必须归类为范围错误"), Error, FString(TEXT("JOIN_TICKET_SCOPE_MISMATCH")));

    FGuiyangGameServerLaunchConfig WrongEpochConfig = GuiyangManagedGameServerTests::MakeConfig();
    WrongEpochConfig.RoomEpoch = 2;
    FGuiyangJoinTicketValidator WrongEpochValidator(WrongEpochConfig);
    TestFalse(TEXT("旧 Room Epoch 票据不得进入新权威实例"), WrongEpochValidator.ValidateAndConsume(
        GuiyangManagedGameServerTests::ValidTicket, TEXT("player-1"), 2000000000, Claims, Error));
    TestEqual(TEXT("Epoch 不匹配必须归类为范围错误"), Error, FString(TEXT("JOIN_TICKET_SCOPE_MISMATCH")));

    FGuiyangGameServerLaunchConfig WrongProtocolConfig = GuiyangManagedGameServerTests::MakeConfig();
    WrongProtocolConfig.ProtocolVersion = TEXT("2");
    FGuiyangJoinTicketValidator WrongProtocolValidator(WrongProtocolConfig);
    TestFalse(TEXT("协议版本不匹配的票据必须拒绝"), WrongProtocolValidator.ValidateAndConsume(
        GuiyangManagedGameServerTests::ValidTicket, TEXT("player-1"), 2000000000, Claims, Error));
    TestEqual(TEXT("协议不匹配必须归类为范围错误"), Error, FString(TEXT("JOIN_TICKET_SCOPE_MISMATCH")));

    FGuiyangGameServerLaunchConfig WrongBuildConfig = GuiyangManagedGameServerTests::MakeConfig();
    WrongBuildConfig.CompatibleClientBuilds = { TEXT("2.0.0") };
    FGuiyangJoinTicketValidator WrongBuildValidator(WrongBuildConfig);
    TestFalse(TEXT("不在白名单中的客户端 Build 必须拒绝"), WrongBuildValidator.ValidateAndConsume(
        GuiyangManagedGameServerTests::ValidTicket, TEXT("player-1"), 2000000000, Claims, Error));
    TestEqual(TEXT("Build 不匹配必须归类为范围错误"), Error, FString(TEXT("JOIN_TICKET_SCOPE_MISMATCH")));

    FGuiyangGameServerLaunchConfig WrongRulesConfig = GuiyangManagedGameServerTests::MakeConfig();
    WrongRulesConfig.RuleSetVersion = TEXT("guiyang-zhuoji-v2");
    FGuiyangJoinTicketValidator WrongRulesValidator(WrongRulesConfig);
    TestFalse(TEXT("规则版本不匹配的票据必须拒绝"), WrongRulesValidator.ValidateAndConsume(
        GuiyangManagedGameServerTests::ValidTicket, TEXT("player-1"), 2000000000, Claims, Error));
    TestEqual(TEXT("规则不匹配必须归类为范围错误"), Error, FString(TEXT("JOIN_TICKET_SCOPE_MISMATCH")));

    FString TamperedTicket = GuiyangManagedGameServerTests::ValidTicket;
    TamperedTicket[0] = TamperedTicket[0] == TEXT('e') ? TEXT('f') : TEXT('e');
    FGuiyangJoinTicketValidator TamperedValidator(GuiyangManagedGameServerTests::MakeConfig());
    TestFalse(TEXT("篡改载荷必须导致签名失败"), TamperedValidator.ValidateAndConsume(
        TamperedTicket, TEXT("player-1"), 2000000000, Claims, Error));
    TestEqual(TEXT("签名失败必须返回稳定错误"), Error, FString(TEXT("JOIN_TICKET_SIGNATURE_INVALID")));

    FGuiyangJoinTicketValidator ExpiredValidator(GuiyangManagedGameServerTests::MakeConfig());
    TestFalse(TEXT("过期票据必须拒绝"), ExpiredValidator.ValidateAndConsume(
        GuiyangManagedGameServerTests::ValidTicket, TEXT("player-1"), 2000000030, Claims, Error));
    TestEqual(TEXT("过期必须返回稳定错误"), Error, FString(TEXT("JOIN_TICKET_EXPIRED")));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangManagedRoomBootstrapTest,
    "GuiyangMahjong.GameServer.ManagedRoomBootstrap",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::ServerContext
        | EAutomationTestFlags::EngineFilter)

bool FGuiyangManagedRoomBootstrapTest::RunTest(const FString& Parameters)
{
    const TSharedRef<FJsonObject> Bootstrap = GuiyangManagedGameServerTests::MakeBootstrap(
        GuiyangManagedGameServerTests::RoomId, GuiyangManagedGameServerTests::MatchId, TEXT("654321"));
    FGuiyangManagedRoomDefinition Definition;
    FString Error;
    TestTrue(TEXT("权威房间定义应被解析"), FGuiyangManagedRoomDefinition::TryParse(Bootstrap,
        GuiyangManagedGameServerTests::RoomId, GuiyangManagedGameServerTests::MatchId, Definition, Error));
    TestEqual(TEXT("固定房号不得重生成"), Definition.RoomCode, FString(TEXT("654321")));
    TestEqual(TEXT("规则快照应采用 Lobby 分数"), Definition.RuleSnapshot.Config.BaseScore, 3);
    TestEqual(TEXT("规则快照应采用 Lobby 超时"), Definition.RuleSnapshot.Config.TurnTimeoutSeconds, 21);
    TestTrue(TEXT("自动开始属性必须保持"), Definition.bAutoStart);
    TestTrue(TEXT("解析后的规则快照必须自洽"),
        UGuiyangRuleSnapshotLibrary::VerifySnapshot(Definition.RuleSnapshot));

    FGuiyangManagedRoomDefinition Rejected;
    TestFalse(TEXT("不得绑定到其他启动房间"), FGuiyangManagedRoomDefinition::TryParse(Bootstrap,
        TEXT("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), GuiyangManagedGameServerTests::MatchId, Rejected, Error));
    TestEqual(TEXT("跨范围应返回稳定错误"), Error, FString(TEXT("ROOM_BOOTSTRAP_SCOPE_MISMATCH")));

    const TSharedRef<FJsonObject> Malformed = GuiyangManagedGameServerTests::MakeBootstrap(
        GuiyangManagedGameServerTests::RoomId, GuiyangManagedGameServerTests::MatchId, TEXT("12345x"));
    TestFalse(TEXT("非六位数字房号必须拒绝"), FGuiyangManagedRoomDefinition::TryParse(Malformed,
        GuiyangManagedGameServerTests::RoomId, GuiyangManagedGameServerTests::MatchId, Rejected, Error));

    const TSharedRef<FJsonObject> MalformedRule = GuiyangManagedGameServerTests::MakeBootstrap(
        GuiyangManagedGameServerTests::RoomId, GuiyangManagedGameServerTests::MatchId, TEXT("123456"));
    const TSharedPtr<FJsonObject>* Rules = nullptr;
    TestTrue(TEXT("测试规则对象应存在"), MalformedRule->TryGetObjectField(TEXT("ruleSnapshot"), Rules));
    if (Rules && Rules->IsValid()) (*Rules)->SetStringField(TEXT("turnTimeoutSeconds"), TEXT("twenty"));
    TestFalse(TEXT("类型错误的已知规则字段必须拒绝"), FGuiyangManagedRoomDefinition::TryParse(MalformedRule,
        GuiyangManagedGameServerTests::RoomId, GuiyangManagedGameServerTests::MatchId, Rejected, Error));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangManagedRoomIsolationTest,
    "GuiyangMahjong.GameServer.ManagedRoomIsolation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::ServerContext
        | EAutomationTestFlags::EngineFilter)

bool FGuiyangManagedRoomIsolationTest::RunTest(const FString& Parameters)
{
    constexpr const TCHAR* SecondRoomId = TEXT("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    constexpr const TCHAR* SecondMatchId = TEXT("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    FGuiyangManagedRoomDefinition First;
    FGuiyangManagedRoomDefinition Second;
    FString ErrorText;
    TestTrue(TEXT("第一桌定义可解析"), FGuiyangManagedRoomDefinition::TryParse(
        GuiyangManagedGameServerTests::MakeBootstrap(GuiyangManagedGameServerTests::RoomId,
            GuiyangManagedGameServerTests::MatchId, TEXT("100001"), 2),
        GuiyangManagedGameServerTests::RoomId, GuiyangManagedGameServerTests::MatchId, First, ErrorText));
    TestTrue(TEXT("第二桌定义可解析"), FGuiyangManagedRoomDefinition::TryParse(
        GuiyangManagedGameServerTests::MakeBootstrap(SecondRoomId, SecondMatchId, TEXT("200002"), 5),
        SecondRoomId, SecondMatchId, Second, ErrorText));

    UGuiyangRoomManager* FirstManager = NewObject<UGuiyangRoomManager>();
    UGuiyangRoomManager* SecondManager = NewObject<UGuiyangRoomManager>();
    FMahjongRoomState FirstState;
    FMahjongRoomState SecondState;
    EMahjongRoomError Error;
    TestTrue(TEXT("第一实例只创建第一桌"), FirstManager->CreateManagedRoom(First, FirstState, Error));
    TestTrue(TEXT("第二实例只创建第二桌"), SecondManager->CreateManagedRoom(Second, SecondState, Error));
    TestTrue(TEXT("房间公共状态必须保留自动开始属性"), FirstState.RoomInfo.bAutoStart);
    TestNotEqual(TEXT("两桌规则哈希必须隔离"), FirstState.RuleSnapshot.RuleHash, SecondState.RuleSnapshot.RuleHash);
    TestFalse(TEXT("第一实例不得访问第二桌"), FirstManager->GetRoomState(TEXT("200002"), FirstState));
    TestFalse(TEXT("同一实例不得再初始化第二桌"), FirstManager->CreateManagedRoom(Second, FirstState, Error));

    for (int32 PlayerIndex = 0; PlayerIndex < 4; ++PlayerIndex)
    {
        const FString PlayerId = PlayerIndex == 0 ? TEXT("owner-1") : FString::Printf(TEXT("player-%d"), PlayerIndex);
        TestTrue(FString::Printf(TEXT("玩家 %d 应进入权威桌"), PlayerIndex),
            FirstManager->AdmitManagedPlayer(TEXT("100001"), PlayerId,
                FString::Printf(TEXT("玩家%d"), PlayerIndex), FirstState, Error, PlayerIndex));
    }
    TestFalse(TEXT("已绑定玩家不得使用另一张座位票据重新进入"), FirstManager->AdmitManagedPlayer(
        TEXT("100001"), TEXT("owner-1"), TEXT("玩家0"), FirstState, Error, 1));
    TestEqual(TEXT("错误座位必须返回无效请求"), Error, EMahjongRoomError::InvalidRequest);
    TestEqual(TEXT("四位玩家必须占用四个唯一座位"),
        FirstState.Seats.FilterByPredicate([](const FMahjongSeatInfo& Seat) { return Seat.bOccupied; }).Num(), 4);
    TestFalse(TEXT("满桌必须拒绝第五位玩家"), FirstManager->AdmitManagedPlayer(
        TEXT("100001"), TEXT("player-5"), TEXT("玩家5"), FirstState, Error));

    for (int32 PlayerIndex = 0; PlayerIndex < 4; ++PlayerIndex)
    {
        const FString PlayerId = PlayerIndex == 0 ? TEXT("owner-1") : FString::Printf(TEXT("player-%d"), PlayerIndex);
        TestTrue(TEXT("等待阶段玩家应可离桌"), FirstManager->LeaveRoom(PlayerId, FirstState, Error));
    }
    TestEqual(TEXT("最后玩家离开后托管房间仍应保留"), FirstManager->GetRoomCount(), 1);
    TestEqual(TEXT("空托管房应回到等待状态"), FirstState.Lifecycle, EMahjongRoomLifecycle::WaitingForPlayers);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangConnectionTelemetryIdempotencyTest,
    "GuiyangMahjong.GameServer.ConnectionTelemetryIdempotency",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::ServerContext
        | EAutomationTestFlags::EngineFilter)

/** 验证连接序号与 EventId 只随真实状态变化推进，重复网络通知不得制造重复事件。 */
bool FGuiyangConnectionTelemetryIdempotencyTest::RunTest(const FString& Parameters)
{
    const TSharedRef<FJsonObject> Bootstrap = GuiyangManagedGameServerTests::MakeBootstrap(
        GuiyangManagedGameServerTests::RoomId,
        GuiyangManagedGameServerTests::MatchId,
        TEXT("333333"));
    FGuiyangManagedRoomDefinition Definition;
    FString ParseError;
    TestTrue(TEXT("连接遥测测试房间应可解析"), FGuiyangManagedRoomDefinition::TryParse(
        Bootstrap,
        GuiyangManagedGameServerTests::RoomId,
        GuiyangManagedGameServerTests::MatchId,
        Definition,
        ParseError));
    UGuiyangRoomManager* Manager = NewObject<UGuiyangRoomManager>();
    FMahjongRoomState State;
    EMahjongRoomError Error;
    TestTrue(TEXT("连接遥测测试房间应可创建"), Manager->CreateManagedRoom(Definition, State, Error));
    TestTrue(TEXT("测试玩家应进入权威桌"), Manager->AdmitManagedPlayer(
        TEXT("333333"), TEXT("owner-1"), TEXT("房主"), State, Error));

    FGuiyangPlayerConnectionTelemetry Initial;
    TestTrue(TEXT("入座应产生初始连接事件"), Manager->GetPlayerConnectionTelemetry(TEXT("owner-1"), Initial));
    TestFalse(TEXT("初始状态应为已连接"), Initial.bDisconnected);
    TestEqual(TEXT("初始连接序号应为一"), Initial.Sequence, static_cast<int64>(1));

    TestTrue(TEXT("网络中断应保留座位"), Manager->MarkDisconnected(
        TEXT("owner-1"), State, Error, TEXT("NetworkInterrupted")));
    FGuiyangPlayerConnectionTelemetry Disconnected;
    TestTrue(TEXT("应可读取掉线状态"), Manager->GetPlayerConnectionTelemetry(
        TEXT("owner-1"), Disconnected));
    TestTrue(TEXT("掉线标志应为真"), Disconnected.bDisconnected);
    TestEqual(TEXT("掉线序号应推进一次"), Disconnected.Sequence, static_cast<int64>(2));
    TestEqual(TEXT("掉线原因应保持受控分类"),
        Disconnected.DisconnectReason, FString(TEXT("NetworkInterrupted")));

    TestTrue(TEXT("重复掉线通知应幂等成功"), Manager->MarkDisconnected(
        TEXT("owner-1"), State, Error, TEXT("NetworkInterrupted")));
    FGuiyangPlayerConnectionTelemetry Duplicate;
    TestTrue(TEXT("应可读取重复通知后的状态"), Manager->GetPlayerConnectionTelemetry(
        TEXT("owner-1"), Duplicate));
    TestEqual(TEXT("重复通知不得推进序号"), Duplicate.Sequence, Disconnected.Sequence);
    TestEqual(TEXT("重复通知不得更换 EventId"), Duplicate.EventId, Disconnected.EventId);

    int32 RemainingSeconds = 0;
    TestTrue(TEXT("宽限期内应可重连"), Manager->ReconnectPlayer(
        TEXT("owner-1"), State, RemainingSeconds, Error));
    FGuiyangPlayerConnectionTelemetry Reconnected;
    TestTrue(TEXT("应可读取重连状态"), Manager->GetPlayerConnectionTelemetry(
        TEXT("owner-1"), Reconnected));
    TestFalse(TEXT("重连后掉线标志应清除"), Reconnected.bDisconnected);
    TestEqual(TEXT("重连只推进一个序号"), Reconnected.Sequence, static_cast<int64>(3));
    TestTrue(TEXT("重连时间应存在"), Reconnected.ReconnectedAtUtc.GetTicks() > 0);
    return true;
}

#endif
