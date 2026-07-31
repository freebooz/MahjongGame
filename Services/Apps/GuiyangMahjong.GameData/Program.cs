using System.Text;
using GuiyangMahjong.GameData.Api;
using GuiyangMahjong.GameData.Infrastructure;
using GuiyangMahjong.GameData.Options;
using GuiyangMahjong.GameData.ReplayEvidence;
using GuiyangMahjong.GameData.Settlement;
using GuiyangMahjong.GameData.GameRecords;
using GuiyangMahjong.GameData.Leaderboards;
using GuiyangMahjong.GameData.Administration;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.AddMahjongObservability("GuiyangMahjong.GameData");
builder.Services.AddOptions<GameDataOptions>()
    .Bind(builder.Configuration.GetSection(GameDataOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.PersistenceMode is "InMemory" or "Postgres",
        "GameData:PersistenceMode 只允许 InMemory 或 Postgres。")
    .Validate(options => options.PersistenceMode != "Postgres"
                         || !string.IsNullOrWhiteSpace(options.PostgresConnectionString),
        "Postgres 模式必须配置 GameData 独立连接字符串。")
    .Validate(options => options.EvidenceStorage.Mode is "MetadataOnly" or "FileSystem" or "HttpGateway",
        "GameData:EvidenceStorage:Mode 无效。")
    .Validate(options => options.EvidenceStorage.Mode != "FileSystem"
                         || Path.IsPathRooted(options.EvidenceStorage.RootDirectory),
        "FileSystem 证据根目录必须是绝对路径。")
    .Validate(options => options.EvidenceStorage.Mode != "HttpGateway"
                         || options.EvidenceStorage.ReadToken.Length >= 32,
        "HttpGateway 必须配置用途隔离的 32+ 字符只读凭据。")
    .Validate(options => options.SettlementSigningKey.Length >= 32
                         && options.AllocatorRecoveryToken.Length >= 32,
        "GameData 必须配置用途隔离的结算签名密钥和 Allocator 恢复凭据。")
    .Validate(options => !builder.Environment.IsProduction()
                         || (options.PersistenceMode == "Postgres"
                             && !options.ApplyDatabaseMigrations
                             && options.EvidenceStorage.Mode != "MetadataOnly"),
        "生产 GameData 必须使用 Postgres、关闭运行时 DDL 并验证真实证据对象。")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient();
var configured = builder.Configuration.GetSection(GameDataOptions.SectionName).Get<GameDataOptions>() ?? new();
if (configured.PersistenceMode == "Postgres")
{
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(configured.PostgresConnectionString));
    builder.Services.AddSingleton<IGameDataStore, PostgresGameDataStore>();
}
else
{
    builder.Services.AddSingleton<IGameDataStore, InMemoryGameDataStore>();
}
builder.Services.AddSingleton<ISettlementAuthorityClient, HttpSettlementAuthorityClient>();
builder.Services.AddSingleton<IEvidenceVerifier>(provider =>
    provider.GetRequiredService<IOptions<GameDataOptions>>().Value.EvidenceStorage.Mode switch
    {
        "FileSystem" => ActivatorUtilities.CreateInstance<FileSystemEvidenceVerifier>(provider),
        "HttpGateway" => ActivatorUtilities.CreateInstance<HttpEvidenceVerifier>(provider),
        _ => new MetadataEvidenceVerifier()
    });
builder.Services.AddSingleton<SettlementService>();
builder.Services.AddSingleton<GameRecordQueries>();
builder.Services.AddSingleton<LeaderboardQueries>();
builder.Services.AddSingleton<ReplayEvidenceQueries>();
builder.Services.AddHostedService<GameDataSchemaInitializer>();

var app = builder.Build();
app.UseMahjongObservability("GuiyangMahjong.GameData", app.Environment.EnvironmentName);
app.UseMiddleware<GameDataExceptionMiddleware>();
app.MapGameDataEndpoints();
app.Run();

/// <summary>WebApplicationFactory 集成测试入口；运行时状态只保存在注册的模块服务中。</summary>
public partial class Program;
