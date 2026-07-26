using System.Security.Cryptography;

namespace GuiyangMahjong.Auth.Services;

/// <summary>Randomly selects one of 1,000 Guiyang-themed names for a new guest identity.</summary>
public sealed class LocalPlayerNameGenerator
{
    private static readonly string[] LocalFeatures =
    [
        "甲秀楼", "黔灵山", "南明河", "青岩", "花溪",
        "筑城", "云岩", "观山湖", "苗岭", "黔中"
    ];

    private static readonly string[] Personalities =
    [
        "热心", "豪爽", "机灵", "沉稳", "从容",
        "欢喜", "自在", "灵巧", "爽朗", "好运"
    ];

    private static readonly string[] MahjongTitles =
    [
        "雀友", "牌友", "小雀神", "捉鸡客", "听牌侠",
        "杠上花", "满堂彩", "好牌手", "守庄人", "摸牌客"
    ];

    public const int CandidateCount = 1_000;

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
