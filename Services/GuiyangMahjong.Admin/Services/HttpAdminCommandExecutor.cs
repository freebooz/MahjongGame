using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

public sealed class HttpAdminCommandExecutor(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options) : IAdminCommandExecutor
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly AdminOptions admin = options.Value;

    public async Task<AdminCommandExecutionResult> ExecuteAsync(
        AdminCommandOutboxRecord command,
        CancellationToken cancellationToken)
    {
        if (command.ActionType is not (
                AdminManagementActionType.ForceLogoutPlayer
                or AdminManagementActionType.ResetAbnormalPlayerSession))
        {
            return Failure(
                false,
                "AdapterNotConfigured",
                $"No command adapter is configured for {command.ActionType}.");
        }

        var action = command.Payload.Deserialize<AdminActionRecord>(JsonOptions);
        if (action is null)
            return Failure(false, "InvalidCommandPayload", "Action payload is invalid.");
        var body = new
        {
            action.Reason,
            action.TraceId,
            EffectiveAtUtc = command.CreatedAtUtc
        };
        var auth = await SendAsync(
            admin.Auth.BaseUrl,
            $"/internal/admin/players/{Uri.EscapeDataString(command.TargetId)}/sessions/revoke",
            admin.Management.AuthCommandToken,
            command.OutboxId,
            command.TraceId,
            body,
            cancellationToken);
        if (!auth.Succeeded)
            return Failure(auth.Retryable, "AuthCommandFailed", auth.Error, auth.Body);

        var lobby = await SendAsync(
            admin.Lobby.BaseUrl,
            $"/internal/admin/players/{Uri.EscapeDataString(command.TargetId)}/disconnect",
            admin.Management.LobbyCommandToken,
            command.OutboxId,
            command.TraceId,
            body,
            cancellationToken);
        if (!lobby.Succeeded)
        {
            return new AdminCommandExecutionResult(
                false,
                lobby.Retryable,
                JsonSerializer.SerializeToElement(new
                {
                    status = "LobbyCommandFailed",
                    auth = auth.Body,
                    lobby = lobby.Body
                }, JsonOptions),
                lobby.Error);
        }

        return new AdminCommandExecutionResult(
            true,
            false,
            JsonSerializer.SerializeToElement(new
            {
                status = "PlayerSessionTerminated",
                auth = auth.Body,
                lobby = lobby.Body
            }, JsonOptions),
            null);
    }

    private async Task<CommandCallResult> SendAsync(
        string baseUrl,
        string path,
        string token,
        string idempotencyKey,
        string traceId,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("X-Trace-Id", traceId);
        request.Content = JsonContent.Create(body, options: JsonOptions);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            admin.Management.CommandTimeoutSeconds));
        try
        {
            using var response = await httpClientFactory
                .CreateClient(nameof(HttpAdminCommandExecutor))
                .SendAsync(request, timeout.Token);
            var responseBody = await ReadBodyAsync(response, timeout.Token);
            if (response.IsSuccessStatusCode)
                return new CommandCallResult(true, false, responseBody, null);
            var retryable = response.StatusCode is
                HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout
                || (int)response.StatusCode >= 500;
            return new CommandCallResult(
                false,
                retryable,
                responseBody,
                $"Command endpoint returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CommandCallResult(
                false,
                true,
                JsonSerializer.SerializeToElement(
                    new { status = "Timeout" }, JsonOptions),
                "Command endpoint timed out.");
        }
        catch (HttpRequestException exception)
        {
            return new CommandCallResult(
                false,
                true,
                JsonSerializer.SerializeToElement(
                    new { status = "TransportFailure" }, JsonOptions),
                exception.Message);
        }
    }

    private static async Task<JsonElement> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0)
            return JsonSerializer.SerializeToElement(new
            {
                statusCode = (int)response.StatusCode
            }, JsonOptions);
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonElement>(
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new
            {
                statusCode = (int)response.StatusCode,
                body = "Non-JSON response"
            }, JsonOptions);
        }
    }

    private static AdminCommandExecutionResult Failure(
        bool retryable,
        string status,
        string? error,
        JsonElement? body = null) =>
        new(
            false,
            retryable,
            JsonSerializer.SerializeToElement(new
            {
                status,
                response = body
            }, JsonOptions),
            error);

    private sealed record CommandCallResult(
        bool Succeeded,
        bool Retryable,
        JsonElement Body,
        string? Error);
}
