namespace GuiyangMahjong.Auth.Devices;

/// <summary>
/// 设备风险信号摘要。DeviceId 仅用于会话风控和审计，不能代替账号身份认证，
/// 也不得保存原始硬件指纹、广告标识或其他可逆设备秘密。
/// </summary>
public sealed record DeviceSummary(
    string PlayerId,
    string DeviceId,
    string TrustState,
    string[] RiskLabelReferences,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastUsedAtUtc);

/// <summary>
/// 玩家设备切换审计事实。记录只包含内部设备引用，不包含 Refresh Token、
/// Access Token、完整 IP 或原始 User-Agent。
/// </summary>
public sealed record DeviceSwitchEvent(
    string EventId,
    string PlayerId,
    string? PreviousDeviceId,
    string CurrentDeviceId,
    DateTimeOffset OccurredAtUtc);
