namespace GuiyangMahjong.Auth.Administration;

/// <summary>
/// Identity 管理模块边界。只允许执行封禁、解封、强制下线、会话查询和身份审计，
/// 禁止直接修改 Room、Settlement、Inventory 或对局结果数据。
/// </summary>
public static class AdministrationModuleBoundary
{
    /// <summary>模块稳定名称，用于审计事件的调用方标识。</summary>
    public const string Name = "Administration";
}
