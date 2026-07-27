using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Auth.Options;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [MinLength(32)] public string TokenSigningKey { get; init; } = string.Empty;
    [MinLength(32)] public string GuestIdentityPepper { get; init; } = string.Empty;
    [Range(1, 60)] public int AccessTokenMinutes { get; init; } = 15;
    [Range(1, 90)] public int RefreshTokenDays { get; init; } = 30;
    [Required] public string PersistenceMode { get; init; } = "InMemory";
    public string PostgresConnectionString { get; init; } = string.Empty;
    public string MonitoringReadOnlyToken { get; init; } = string.Empty;
    public string ManagementCommandToken { get; init; } = string.Empty;
    public bool EnableHttpsRedirection { get; init; }
}
