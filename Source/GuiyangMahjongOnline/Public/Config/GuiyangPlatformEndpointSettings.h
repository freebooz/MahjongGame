#pragma once

#include "CoreMinimal.h"
#include "Interfaces/IHttpRequest.h"

/**
 * 旧公网地址所属业务，用于统一 ApiBaseUrl 未配置时的有限兼容回退。
 * 该枚举只决定读取哪个旧配置名，不会把 Auth、Lobby 或 DS 地址互相推导。
 */
enum class EGuiyangLegacyEndpointRole : uint8
{
    None,
    Auth,
    Lobby
};

/**
 * UE 客户端平台统一后端配置。
 * ApiBaseUrl 承载玩家 HTTP，RealtimeBaseUrl 预留实时控制面，PatchBaseUrl 承载补丁下载；
 * Dedicated Server 的 IP、UDP 端口和 Unreal travel URL 不属于本结构。
 */
struct GUIYANGMAHJONGONLINE_API FGuiyangPlatformEndpointSettings
{
    /** 玩家 HTTP 网关基地址；正式包必须使用 HTTPS。 */
    FString ApiBaseUrl;

    /** 实时控制面基地址；为空时回退到 ApiBaseUrl，当前 Lobby WebSocket 尚未独立连接。 */
    FString RealtimeBaseUrl;

    /** 补丁服务基地址；当前阶段只统一配置，不发起补丁请求。 */
    FString PatchBaseUrl;

    /** 发给 EdgeGateway 的客户端版本、协议、平台和渠道，均为受控低基数字段。 */
    FString ClientVersion = TEXT("1.0.0");
    FString ProtocolVersion = TEXT("1");
    FString Platform;
    FString Channel = TEXT("default");

    /** 是否通过旧 AuthBaseUrl/RemoteBaseUrl 直接访问服务；仅用于可回滚兼容窗口。 */
    bool bUsingLegacyDirectEndpoint = false;

    /**
     * 从统一 ini/命令行加载并验证端点。
     * 当统一 ApiBaseUrl 缺失时，可按 LegacyRole 读取一个旧入口并输出不含地址的废弃警告；
     * 返回 false 表示远程 HTTP 模式必须失败关闭。
     */
    static bool Load(
        EGuiyangLegacyEndpointRole LegacyRole,
        FGuiyangPlatformEndpointSettings& OutSettings);

    /**
     * 构造玩家 API URL。
     * 新配置自动添加 `/api` 外部前缀，旧直连配置保留原 `/v1` 路径。
     */
    FString BuildApiUrl(const FString& LegacyV1Path) const;

    /**
     * 添加网关契约头和请求关联标识。
     * RequestId 为空时生成随机 ID；不会添加玩家身份、权限或任何 Token。
     */
    void ApplyStandardHeaders(
        IHttpRequest& Request,
        const FString& RequestId = FString()) const;

    /** 规范化 HTTP(S) 基地址；正式包拒绝非 loopback 明文 HTTP。 */
    static bool NormalizeHttpBaseUrl(
        const FString& Candidate,
        bool bAllowLoopbackHttp,
        FString& OutBaseUrl);
};
