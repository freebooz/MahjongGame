namespace GuiyangMahjong.GameData.Tests;

/// <summary>数据库迁移静态契约测试，防止唯一约束、不可变门禁或回滚脚本在后续修改中被静默删除。</summary>
public sealed class SchemaContractTests
{
    /// <summary>验证业务幂等键、不可变触发器和五个逻辑 Schema 均存在。</summary>
    [Fact]
    public void Schema_ContainsIdempotencyAndImmutabilityGuards()
    {
        var sql = File.ReadAllText(FindRepositoryFile("Services/Apps/GuiyangMahjong.GameData/Storage/schema.sql"));
        Assert.Contains("UNIQUE (match_id, round_no, settlement_version)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reject_immutable_mutation", sql, StringComparison.OrdinalIgnoreCase);
        foreach (var schema in new[] { "settlement", "game_record", "replay", "leaderboard", "game_data_integration" })
            Assert.Contains($"CREATE SCHEMA IF NOT EXISTS {schema}", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>验证回滚脚本仅删除 GameData 自有 Schema，不触碰 Lobby、Auth 或 PlayerData。</summary>
    [Fact]
    public void Rollback_DropsOnlyOwnedSchemas()
    {
        var sql = File.ReadAllText(FindRepositoryFile(
            "Services/Apps/GuiyangMahjong.GameData/Migrations/0001_game_data.down.sql"));
        Assert.DoesNotContain("DROP SCHEMA lobby", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP SCHEMA auth", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP SCHEMA player", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
