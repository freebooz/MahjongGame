using System.Text;
using GuiyangMahjong.Configuration.Api;
using GuiyangMahjong.Configuration.Infrastructure;
using GuiyangMahjong.Configuration.Options;
using GuiyangMahjong.Configuration.Services;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.AddMahjongObservability("GuiyangMahjong.Configuration");
builder.Services.AddOptions<ConfigurationOptions>()
    .Bind(builder.Configuration.GetSection(ConfigurationOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.PersistenceMode is "InMemory" or "Postgres", "PersistenceMode 仅允许 InMemory 或 Postgres。")
    .Validate(options => options.PersistenceMode != "Postgres" || !string.IsNullOrWhiteSpace(options.PostgresConnectionString), "Postgres 模式必须配置独立连接。")
    .Validate(options => !builder.Environment.IsProduction() || (options.PersistenceMode == "Postgres" && !options.ApplyDatabaseMigrations), "生产环境必须使用 Postgres 且关闭运行时 DDL。")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
var startup = builder.Configuration.GetSection(ConfigurationOptions.SectionName).Get<ConfigurationOptions>() ?? new();
if (startup.PersistenceMode == "Postgres")
{
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(startup.PostgresConnectionString));
    builder.Services.AddSingleton<IConfigurationStore, PostgresConfigurationStore>();
}
else
{
    builder.Services.AddSingleton<IConfigurationStore, InMemoryConfigurationStore>();
}
builder.Services.AddSingleton<PlatformConfigurationService>();
builder.Services.AddHostedService<ConfigurationSchemaInitializer>();

var app = builder.Build();
app.UseMahjongObservability("GuiyangMahjong.Configuration", app.Environment.EnvironmentName);
app.UseMiddleware<ConfigurationExceptionMiddleware>();
app.MapConfigurationEndpoints();
app.Run();

/// <summary>供 WebApplicationFactory 进行真实 HTTP 边界测试的公开程序集入口。</summary>
public partial class Program;
