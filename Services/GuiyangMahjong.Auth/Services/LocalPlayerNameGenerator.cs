using System.Security.Cryptography;

namespace GuiyangMahjong.Auth.Services;

/// <summary>
/// 为新游客身份使用加密随机数从 1,000 个贵阳主题五字昵称中选择一个。
/// 生成器不保证全局唯一，身份存储以 PlayerId/安装哈希而非展示名判定唯一性。
/// </summary>
public sealed class LocalPlayerNameGenerator
{
    private static readonly string[] LocalFeatures =
    [
        "甲秀", "黔灵", "南明", "青岩", "花溪",
        "筑城", "云岩", "观山", "苗岭", "黔中"
    ];

    private static readonly string[] Personalities =
    [
        "乐", "豪", "灵", "稳", "闲",
        "喜", "巧", "爽", "福", "旺"
    ];

    private static readonly string[] MahjongTitles =
    [
        "雀友", "牌友", "雀神", "鸡客", "听侠",
        "杠花", "满堂", "好手", "庄家", "摸客"
    ];

    public const int CandidateCount = 1_000;

    static LocalPlayerNameGenerator()
    {
        if (LocalFeatures.Length * Personalities.Length * MahjongTitles.Length != CandidateCount
            || LocalFeatures.Any(value => value.Length != 2)
            || Personalities.Any(value => value.Length != 1)
            || MahjongTitles.Any(value => value.Length != 2))
        {
            throw new InvalidOperationException(
                "The local player-name dictionary must contain 1,000 five-character names.");
        }
    }

    /// <summary>均匀生成一个五字候选昵称；不访问存储，也不保留随机状态。</summary>
    public string Generate()
    {
        var index = RandomNumberGenerator.GetInt32(CandidateCount);
        var titleIndex = index % MahjongTitles.Length;
        index /= MahjongTitles.Length;
        var personalityIndex = index % Personalities.Length;
        var featureIndex = index / Personalities.Length;
        return $"{LocalFeatures[featureIndex]}{Personalities[personalityIndex]}{MahjongTitles[titleIndex]}";
    }
}
