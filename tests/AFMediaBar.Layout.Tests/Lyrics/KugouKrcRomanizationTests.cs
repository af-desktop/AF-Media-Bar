// 酷狗 KRC 的罗马音藏在 [language:] 里，这里固定解析与放弃的边界。
using System.Text;
using AFMediaBar.Classes.Services.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

[TestClass]
public sealed class KugouKrcRomanizationTests
{
    [TestMethod]
    public void ExtractsSyllablesAsTimestampedLines()
    {
        var krc = BuildKrc(
            """[["a ","na ","ta "],["ta ","ni "]]""",
            [1000, 2500]);

        var result = KugouKrcRomanization.Extract(krc);

        Assert.IsNotNull(result);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(2, lines.Length);
        Assert.AreEqual("[00:01.000]a na ta", lines[0]);
        Assert.AreEqual("[00:02.500]ta ni", lines[1]);
    }

    [TestMethod]
    public void ReturnsNullWhenLineCountDoesNotMatch()
    {
        // 时间戳三行、发音两行：只能按行序对齐，错一行整首都错，宁可不要。
        var krc = BuildKrc("""[["a "],["b "]]""", [1000, 2000, 3000]);

        Assert.IsNull(KugouKrcRomanization.Extract(krc));
    }

    [TestMethod]
    public void ReturnsNullWithoutLanguageTag()
    {
        const string krc = "[ti:标题]\n[1000,500]<0,100,0>あ\n";

        Assert.IsNull(KugouKrcRomanization.Extract(krc));
    }

    [TestMethod]
    public void ReturnsNullForBrokenBase64()
    {
        const string krc = "[language:不是base64]\n[1000,500]<0,100,0>あ\n";

        Assert.IsNull(KugouKrcRomanization.Extract(krc));
    }

    [TestMethod]
    public void SkipsEmptyPronunciationRows()
    {
        var krc = BuildKrc("""[["a "],[""],["c "]]""", [1000, 2000, 3000]);

        var result = KugouKrcRomanization.Extract(krc);

        Assert.IsNotNull(result);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(2, lines.Length);
        Assert.AreEqual("[00:01.000]a", lines[0]);
        Assert.AreEqual("[00:03.000]c", lines[1]);
    }

    [TestMethod]
    public void PreservesOffsetAndLongDurationWhenAttachedByParser()
    {
        var krc = "[offset:1500]\n" + BuildKrc("""[["a "],["b "]]""", [1000, 3_665_250]);

        var romanization = KugouKrcRomanization.Extract(krc);

        Assert.IsNotNull(romanization);
        StringAssert.Contains(romanization, "[offset:1500]");
        StringAssert.Contains(romanization, "[61:05.250]b");

        var document = LyricsTextParser.Parse(krc, romanizationText: romanization, filterInfoLines: false);
        Assert.AreEqual(2, document.Lines.Count);
        Assert.AreEqual("a", document.Lines[0].Romanization);
        Assert.AreEqual("b", document.Lines[1].Romanization);
    }

    [TestMethod]
    public void ReturnsNullForOverflowTimestamp()
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"content":[{"lyricContent":[["a "]],"type":0,"language":0}],"version":1}"""));
        var krc = $"[ti:测试]\n[language:{base64}]\n[99999999999999999999999,500]<0,100,0>词\n";

        Assert.IsNull(KugouKrcRomanization.Extract(krc));
    }

    [TestMethod]
    public void IgnoresNonRomanizationEntries()
    {
        // type 不是 0 的语言项含义未知，不能当成音译。
        var json = """{"content":[{"lyricContent":[["zh "]],"type":1,"language":1}],"version":1}""";
        var krc = BuildKrcFromJson(json, [1000]);

        Assert.IsNull(KugouKrcRomanization.Extract(krc));
    }

    private static string BuildKrc(string lyricContent, int[] times) =>
        BuildKrcFromJson(
            $$"""{"content":[{"lyricContent":{{lyricContent}},"type":0,"language":0}],"version":1}""",
            times);

    private static string BuildKrcFromJson(string json, int[] times)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var builder = new StringBuilder();
        builder.Append("[ti:测试]\n[language:").Append(base64).Append("]\n");
        for (var index = 0; index < times.Length; index++)
        {
            builder.Append('[').Append(times[index]).Append(",500]<0,100,0>词\n");
        }

        return builder.ToString();
    }
}
