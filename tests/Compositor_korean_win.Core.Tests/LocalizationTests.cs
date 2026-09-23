using System.Text.RegularExpressions;
using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The text table, and the rule that every word the user reads comes from it.
/// </summary>
/// <remarks>
/// M6's goal is that switching to Korean leaves nothing in English. These are the checks that can
/// run without a window: every phrase has both languages, the Korean really is Korean, and no
/// source file outside the table carries words of its own. What the window shows is checked by the
/// self-test, which draws it (docs/progress.md 7).
/// </remarks>
public partial class LocalizationTests
{
    [GeneratedRegex("[가-힣]")]
    private static partial Regex Hangul();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    [Fact]
    public void EveryKeyHasExactlyOneRow()
    {
        var counts = TextTable.Entries.GroupBy(entry => entry.Key).ToDictionary(group => group.Key, group => group.Count());

        foreach (TextKey key in Enum.GetValues<TextKey>())
        {
            Assert.True(counts.TryGetValue(key, out int count), $"{key} has no row");
            Assert.True(count == 1, $"{key} has {count} rows");
        }
    }

    [Fact]
    public void NoPhraseIsEmpty()
    {
        foreach ((TextKey key, string english, string korean) in TextTable.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(english), $"{key} has no English");
            Assert.False(string.IsNullOrWhiteSpace(korean), $"{key} has no Korean");
        }
    }

    [Fact]
    public void TheKoreanIsKorean()
    {
        foreach ((TextKey key, string _, string korean) in TextTable.Entries)
        {
            if (TextTable.Untranslated.Contains(key)) continue;
            Assert.True(Hangul().IsMatch(korean), $"{key}'s Korean is \"{korean}\", which has no Korean in it");
        }
    }

    [Fact]
    public void TheEnglishIsNotKorean()
    {
        foreach ((TextKey key, string english, string _) in TextTable.Entries)
        {
            if (TextTable.Untranslated.Contains(key)) continue;
            Assert.False(Hangul().IsMatch(english), $"{key}'s English is \"{english}\"");
        }
    }

    [Fact]
    public void BothLanguagesTakeTheSamePlaceholders()
    {
        foreach ((TextKey key, string english, string korean) in TextTable.Entries)
        {
            string[] inEnglish = [.. Placeholder().Matches(english).Select(match => match.Value).Order()];
            string[] inKorean = [.. Placeholder().Matches(korean).Select(match => match.Value).Order()];
            Assert.True(inEnglish.SequenceEqual(inKorean), $"{key} fills {string.Join(",", inEnglish)} in English "
                                                           + $"but {string.Join(",", inKorean)} in Korean");
        }
    }

    [Fact]
    public void NoSourceFileOutsideTheTableSpeaksKorean()
    {
        // Korean anywhere else is a phrase the English interface would show untranslated — or, in a
        // comment, a break with the rule that comments are English (CLAUDE.md 4).
        string root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "TextTable.cs") continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (Hangul().IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
        }

        Assert.True(offenders.Count == 0, "Korean outside the text table: " + string.Join(", ", offenders));
    }

    [Fact]
    public void AHistoryStepIsNamedInTheLanguageItIsReadIn()
    {
        Language before = Localizer.Current;
        try
        {
            var history = new DocumentHistory();
            CanvasDocument first = RenderFixture.Document(4, 4);
            CanvasDocument second = first with { Width = 8 };

            history.Begin(HistoryName.Of(TextKey.HistoryAdjustmentLayer, TextKey.AdjustLevels), first, null);
            history.End(second, null);

            Localizer.Current = Language.English;
            Assert.Equal("New Levels Layer", history.UndoName);

            Localizer.Current = Language.Korean;
            Assert.Equal(Localizer.Format(TextKey.HistoryAdjustmentLayer, Localizer.Text(TextKey.AdjustLevels)),
                         history.UndoName);
            Assert.Matches(Hangul(), history.UndoName);
        }
        finally
        {
            Localizer.Current = before;
        }
    }

    [Fact]
    public void ChangingTheLanguageSaysSo()
    {
        Language before = Localizer.Current;
        int heard = 0;
        void Listen() => heard++;

        Localizer.Changed += Listen;
        try
        {
            Localizer.Current = Language.English;
            heard = 0;
            Localizer.Current = Language.Korean;
            Localizer.Current = Language.Korean; // No change, no news.
            Assert.Equal(1, heard);
        }
        finally
        {
            Localizer.Changed -= Listen;
            Localizer.Current = before;
        }
    }

    [Theory]
    [InlineData(0x0412, Language.Korean)]   // ko-KR
    [InlineData(0x0409, Language.English)]  // en-US
    [InlineData(0x0411, Language.English)]  // ja-JP: not Korean, so English
    public void WindowsLanguageDecidesTheFirstRun(int languageId, Language expected) =>
        Assert.Equal(expected, Localizer.ForLanguageId(languageId));

    [Fact]
    public void ALanguageSurvivesBeingSaved()
    {
        foreach (Language language in Enum.GetValues<Language>())
            Assert.Equal(language, Localizer.Parse(Localizer.Code(language)));

        Assert.Null(Localizer.Parse("fr"));
        Assert.Null(Localizer.Parse(null));
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Compositor_korean_win.sln")))
                return directory.FullName;

        throw new DirectoryNotFoundException("the repository root was not found above the test binaries");
    }
}
