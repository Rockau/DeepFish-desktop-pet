using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FishPet;

// 英文读法（本机加，2026-11-09）：GPT-SoVITS 是日语模型，句子里夹英文容易念成怪音。
// 送去合成之前先把英文单词换成片假名：常用词查表，查不到就按字母逐个拼（A→エー、B→ビー…）。
// 只影响「念出来的声音」，字幕里还是原来的英文。
internal static partial class English
{
    private static readonly Dictionary<string, string> Words = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["bug"] = "バグ", ["bugs"] = "バグ", ["debug"] = "デバッグ",
        ["token"] = "トークン", ["tokens"] = "トークン",
        ["deadline"] = "デッドライン", ["api"] = "エーピーアイ", ["ai"] = "エーアイ",
        ["gpt"] = "ジーピーティー", ["chatgpt"] = "チャットジーピーティー",
        ["deepseek"] = "ディープシーク", ["deepfish"] = "ディープフィッシュ", ["claude"] = "クロード", ["llm"] = "エルエルエム",
        ["image"] = "イメージ", ["fish"] = "フィッシュ", ["model"] = "モデル",
        ["code"] = "コード", ["error"] = "エラー", ["log"] = "ログ", ["logs"] = "ログ",
        ["server"] = "サーバー", ["data"] = "データ", ["test"] = "テスト", ["tests"] = "テスト",
        ["app"] = "アプリ", ["pc"] = "ピーシー", ["cpu"] = "シーピーユー", ["gpu"] = "ジーピーユー",
        ["ok"] = "オーケー", ["ng"] = "エヌジー", ["yes"] = "イエス", ["no"] = "ノー",
        ["hello"] = "ハロー", ["bye"] = "バイバイ", ["good"] = "グッド", ["nice"] = "ナイス",
        ["new"] = "ニュー", ["old"] = "オールド", ["sns"] = "エスエヌエス",
        ["url"] = "ユーアールエル", ["id"] = "アイディー", ["pdf"] = "ピーディーエフ",
        ["csv"] = "シーエスブイ", ["json"] = "ジェイソン", ["html"] = "エイチティーエムエル",
        ["ver"] = "バージョン", ["version"] = "バージョン",
    };

    private static readonly string[] Letters =
    [
        "エー", "ビー", "シー", "ディー", "イー", "エフ", "ジー", "エイチ", "アイ", "ジェー",
        "ケー", "エル", "エム", "エヌ", "オー", "ピー", "キュー", "アール", "エス", "ティー",
        "ユー", "ブイ", "ダブリュー", "エックス", "ワイ", "ゼット",
    ];

    [GeneratedRegex(@"[A-Za-z]+(?:['\-][A-Za-z]+)?")]
    private static partial Regex Word();

    // 把句子里的英文换成片假名读法；没有英文就原样返回。
    internal static string ToKana(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return Word().Replace(text, match => Convert(match.Value));
    }

    private static string Convert(string word)
    {
        if (Words.TryGetValue(word, out var known)) return known;
        // 查不到就按字母拼：gpu → ジーピーユー
        var builder = new StringBuilder();
        foreach (var ch in word.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z') builder.Append(Letters[ch - 'a']);
            else if (ch is '-' or '\'') continue;
        }
        return builder.Length > 0 ? builder.ToString() : word;
    }
}


