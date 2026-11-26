// 酷狗把逐音节罗马音放在 KRC 的 [language:] 里，解析库不读它，只能自己取。
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AFMediaBar.Classes.Services.Lyrics;

/// <summary>
/// 从酷狗 KRC 提取罗马音。KRC 用一行 <c>[language:base64]</c> 携带逐音节数据，值是 JSON：
/// <c>{"content":[{"lyricContent":[[音节...],...],"type":0,"language":0}],"version":1}</c>，
/// 每个子数组对应一行歌词，拼起来就是这一行的罗马音。
/// Extracts romanization from a Kugou KRC: one <c>[language:base64]</c> line carries the per-syllable payload, and each
/// inner array of <c>lyricContent</c> is one lyric line's syllables.
/// </summary>
internal static partial class KugouKrcRomanization
{
    // 兼容三位分钟上限，避免畸形大时间戳在解析或格式化时溢出并影响主歌词。
    private const long MaximumTimestampMilliseconds = 999L * 60 * 1000 + 59_999;

    /// <summary>
    /// 返回带时间戳的罗马音文本，可直接交给解析器按时间贴合；没有数据、行数与歌词对不上或内容损坏时返回 null。
    /// 行数对不上就不猜——时间戳和发音行只能靠行序对齐，错一行整首都会错位。
    /// Returns timestamped romanization text for the parser to attach by time, or null when the payload is missing,
    /// unreadable, or has a different line count than the lyrics.
    /// </summary>
    internal static string? Extract(string? krc)
    {
        if (string.IsNullOrWhiteSpace(krc)) return null;
        var tag = LanguageTag().Match(krc);
        if (!tag.Success) return null;

        KrcLanguage? payload;
        try
        {
            payload = JsonSerializer.Deserialize<KrcLanguage>(Convert.FromBase64String(tag.Groups[1].Value));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or NotSupportedException)
        {
            return null;
        }

        // type 0 是罗马音；type 1（行译文）等其它项不在本轮当作音译混入。
        var rows = payload?.Content?.FirstOrDefault(entry => entry.Type == 0)?.LyricContent;
        if (rows is not { Count: > 0 }) return null;

        var matches = LineTime().Matches(krc);
        if (matches.Count != rows.Count) return null;

        var times = new long[matches.Count];
        for (var index = 0; index < matches.Count; index++)
        {
            if (!long.TryParse(matches[index].Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ||
                ms > MaximumTimestampMilliseconds)
            {
                return null;
            }

            times[index] = ms;
        }

        var builder = new StringBuilder();
        var hasLines = false;
        for (var index = 0; index < rows.Count; index++)
        {
            var text = string.Concat(rows[index] ?? []).Trim();
            if (text.Length == 0) continue;
            if (!hasLines)
            {
                // 保留 KRC 原文的 [offset:]，确保罗马音与主歌词应用一致的时间偏移。
                foreach (Match offset in OffsetTag().Matches(krc))
                    builder.Append(offset.Value.TrimEnd('\r')).Append('\n');
                hasLines = true;
            }

            var ms = times[index];
            var totalMinutes = ms / 60_000;
            var seconds = (ms % 60_000) / 1000;
            var milliseconds = ms % 1000;
            builder.Append('[')
                .Append(totalMinutes.ToString("D2", CultureInfo.InvariantCulture))
                .Append(':')
                .Append(seconds.ToString("D2", CultureInfo.InvariantCulture))
                .Append('.')
                .Append(milliseconds.ToString("D3", CultureInfo.InvariantCulture))
                .Append(']').Append(text).Append('\n');
        }

        return hasLines ? builder.ToString() : null;
    }

    [GeneratedRegex(@"\[language:([A-Za-z0-9+/=]+)\]")]
    private static partial Regex LanguageTag();

    /// <summary>KRC 原文的全局偏移标签。</summary>
    [GeneratedRegex(@"^\[offset:[^\]]+\]", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    /// <summary>KRC 正文行首的时间戳，单位毫秒；元数据行不匹配。</summary>
    [GeneratedRegex(@"^\[(\d+),\d+\]", RegexOptions.Multiline)]
    private static partial Regex LineTime();

    private sealed class KrcLanguage
    {
        [JsonPropertyName("content")]
        public List<KrcLanguageEntry>? Content { get; init; }
    }

    private sealed class KrcLanguageEntry
    {
        [JsonPropertyName("lyricContent")]
        public List<List<string>?>? LyricContent { get; init; }

        [JsonPropertyName("type")]
        public int Type { get; init; }
    }
}
