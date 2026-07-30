using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 提供管理端点共享的标识符、TraceId 与幂等键校验，避免各领域端点产生不一致的安全边界。
/// </summary>
public static partial class AdminEndpoints
{
    private static void ValidateSafeIdentifier(
        string value,
        string name)
    {
        if (!IsSafeIdentifier(value))
            throw AdminOperationException.Invalid(
                $"{name} contains invalid characters or length.");
    }

    private static string GetTraceId(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Trace-Id"].ToString().Trim();
        if (supplied.Length == 0) return context.TraceIdentifier;
        if (supplied.Length > 64
            || supplied.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '_' or ':' or '-')))
        {
            throw AdminOperationException.Invalid(
                "X-Trace-Id contains invalid characters or length.");
        }
        return supplied;
    }

    private static string GetIdempotencyKey(HttpContext context)
    {
        var value =
            context.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (value.Length is < 16 or > 128
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '_' or ':' or '-')))
        {
            throw AdminOperationException.Invalid(
                "Idempotency-Key must contain 16 to 128 safe characters.");
        }
        return value;
    }

    private static bool IsSafeIdentifier(string value) =>
        value.Length is >= 3 and <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '-');
}

