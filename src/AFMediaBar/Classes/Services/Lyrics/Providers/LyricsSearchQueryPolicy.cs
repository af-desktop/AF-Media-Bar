// 构造歌词搜索关键词与播放器译名别名；保留请求原文用于评分和展示，不持有外部资源。
using System.Text.RegularExpressions;
using AFMediaBar.Classes.Models;

namespace AFMediaBar.Classes.Services.Lyrics;

/// <summary>带括号译名的标题和歌手的额外搜索词，不修改媒体快照。/ Search variants for translated metadata.</summary>
internal static partial class LyricsSearchQueryPolicy
{
    public static string WithoutTranslation(string value)
    {
        var match = TranslationSuffix().Match(value);
        // 汉字后缀通常是译名，但已知版本语义不能当作译名去掉。
        return match.Success && !VersionSuffix().IsMatch(match.Value) ? value[..match.Index].Trim() : value.Trim();
    }

    public static IReadOnlyList<string> Build(LyricsRequest request)
    {
        var title = WithoutTranslation(request.Title);
        var artist = string.Join(" ", LyricsArtistPolicy.Split(request).Select(WithoutTranslation));
        var queries = new[] { $"{title} {artist}".Trim(), $"{request.Title} {request.Artist}".Trim() }
            .Concat(string.IsNullOrWhiteSpace(request.ArtistSeparators) ? [] :
                LyricsArtistPolicy.Split(request).Select(value => $"{title} {WithoutTranslation(value)}".Trim()))
            .Append(title)
            .Where(query => query.Length > 0)
            .ToArray();

        // 请求侧归一化：半角中点 ･/全角空格等写法差异折叠成在线曲库常用写法，
        // 解决「ふ･れ･ん･ど･し･た･い」这类 SMTC 标题搜不到候选的问题。保留原写法兜底。
        var normalized = queries.Select(LyricsMetadataNormalizer.Normalize)
            .Where(query => query.Length > 0);
        return queries.Concat(normalized).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// LRCLIB 的标题搜索词。QQ 音乐把译名和假名都写进括号，而 LRCLIB 按标题检索，多一个括号就一条都搜不到，
    /// 所以只对 QQ 来源额外给一个去掉全部别名的写法，并把原写法留到最后兜底。
    /// 其它播放器的标题格式不做假设，沿用只去末尾汉字译名的旧规则。
    /// Title search terms for LRCLIB. QQ Music writes translations and kana readings into brackets, which LRCLIB's title
    /// search never matches, so QQ-sourced requests also try a fully alias-stripped spelling and keep the original last.
    /// Other players keep the trailing-translation rule; their formats are not assumed.
    /// </summary>
    internal static IReadOnlyList<string> BuildTitleVariants(LyricsRequest request)
    {
        if (!UsesQQMusicTitles(request))
        {
            var plain = WithoutTranslation(request.Title);
            return new[] { plain, LyricsMetadataNormalizer.Normalize(plain) }
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var title = WithoutAliases(request.Title);
        return new[] { title, LyricsMetadataNormalizer.Normalize(title), request.Title.Trim() }
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// LRCLIB 的歌手搜索词，规则与标题一致：QQ 来源拆分后去掉全部别名并保留原写法，其它来源沿用旧规则。
    /// Artist search terms for LRCLIB, widened only for QQ Music exactly like the title; other players unchanged.
    /// </summary>
    internal static IReadOnlyList<string> BuildArtists(LyricsRequest request)
    {
        var widened = UsesQQMusicTitles(request);
        if (string.IsNullOrWhiteSpace(request.ArtistSeparators))
        {
            // 没配分隔符就整串当一个人；QQ 来源额外保留原写法。
            var single = widened ? WithoutAliases(request.Artist) : WithoutTranslation(request.Artist);
            return widened
                ? new[] { single, request.Artist.Trim() }.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray()
                : [single];
        }

        var artists = LyricsArtistPolicy.Split(request)
            .Select(value => widened ? WithoutAliases(value) : WithoutTranslation(value))
            .ToArray();
        var ordered = artists.Prepend(string.Join(" ", artists));
        // QQ 来源：拆分后的组合在前，未拆分的原串最后兜底。
        var candidates = widened ? ordered.Append(request.Artist.Trim()) : ordered;
        return candidates.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// QQ 音乐把译名和假名都塞进括号，LRCLIB 按标题检索时一条都搜不到，所以对它把括号当别名去掉。
    /// 版本描述必须留着，否则会拿原版歌词顶替 Live、Remix 这些确实没有词的版本。
    /// QQ Music packs translations and kana readings into brackets, which LRCLIB never matches; version markers stay so an
    /// original-version lyric does not stand in for a Live or Remix release that has none.
    /// </summary>
    private static string WithoutAliases(string value)
    {
        var cleared = BracketGroup().Replace(value,
            match => AliasVersionSuffix().IsMatch(match.Value) ? match.Value : string.Empty);
        return WhitespaceRun().Replace(cleared, " ").Trim();
    }

    /// <summary>宽规则只对 QQ 音乐生效：译名和假名的括号写法是它的习惯，别的播放器不假设。
    /// The wider rule applies to QQ Music alone; other players' title formats are not assumed.</summary>
    private static bool UsesQQMusicTitles(LyricsRequest request) =>
        LyricsSourcePolicy.IsCurrentPlaybackSource(request.PlaybackSourceId, LyricsSourceCatalog.QQMusic);

    [GeneratedRegex(@"\s*[（(][^（）()]*[\p{IsCJKUnifiedIdeographs}][^（）()]*[）)]\s*$")]
    private static partial Regex TranslationSuffix();

    [GeneratedRegex("现场|現場|伴奏|翻唱|混音|重制|重製|倍速|live|remix|cover|acoustic|instrumental", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffix();

    [GeneratedRegex(@"[（(][^（）()]*[）)]")]
    private static partial Regex BracketGroup();

    /// <summary>去掉括号后剩下的连续空格。</summary>
    [GeneratedRegex(@"[ 　]{2,}")]
    private static partial Regex WhitespaceRun();

    /// <summary>QQ 来源的版本标记比旧规则多认一些写法：Ver.、TV size、试听、カラオケ等。
    /// Version markers recognised for QQ-sourced titles, wider than the legacy rule.</summary>
    [GeneratedRegex(
        @"现场|現場|伴奏|翻唱|混音|重制|重製|倍速|试听|試聽|カラオケ|\b(?:live|remix|cover|acoustic|instrumental|inst|karaoke|tv\s?size|tvsize|short|demo|remaster|ver|version)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AliasVersionSuffix();
}
