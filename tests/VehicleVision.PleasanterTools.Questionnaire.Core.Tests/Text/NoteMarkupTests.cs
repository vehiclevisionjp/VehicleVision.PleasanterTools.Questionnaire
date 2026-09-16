using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Text;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Text;

/// <summary>説明文ブロックの記法（Issue #108）。</summary>
public sealed class NoteMarkupTests
{
    private static string PlainText(NoteBlock block) =>
        string.Concat(block.Inlines.Select(inline => inline.Text));

    [Fact]
    public void 空文字は段落を作らない()
    {
        Assert.Empty(NoteMarkup.Parse(""));
        Assert.Empty(NoteMarkup.Parse("   \n  \n"));
        Assert.Empty(NoteMarkup.Parse(null));
    }

    [Fact]
    public void 空行で段落が分かれる()
    {
        var blocks = NoteMarkup.Parse("一つ目\n\n二つ目");

        Assert.Equal(2, blocks.Length);
        Assert.All(blocks, block => Assert.Equal(NoteBlockKind.Paragraph, block.Kind));
        Assert.Equal("一つ目", PlainText(blocks[0]));
        Assert.Equal("二つ目", PlainText(blocks[1]));
    }

    [Fact]
    public void 段落の中の改行は空白へ潰れる()
    {
        var blocks = NoteMarkup.Parse("前半\n後半");

        Assert.Single(blocks);
        Assert.Equal("前半 後半", PlainText(blocks[0]));
    }

    [Theory]
    [InlineData("## 見出し", 2)]
    [InlineData("### 見出し", 3)]
    [InlineData("#### 見出し", 4)]
    [InlineData("##### 見出し", 4)]
    public void 見出しは深さ2から4に収まる(string markup, int expected)
    {
        var blocks = NoteMarkup.Parse(markup);

        Assert.Single(blocks);
        Assert.Equal(NoteBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(expected, blocks[0].Level);
    }

    [Fact]
    public void 井桁1つは見出しにしない()
    {
        // アンケートの題名が最上位。説明文がそこへ並ぶと読み上げの構造が壊れる
        var blocks = NoteMarkup.Parse("# 題名のつもり");

        Assert.Single(blocks);
        Assert.Equal(NoteBlockKind.Paragraph, blocks[0].Kind);
        Assert.Equal("# 題名のつもり", PlainText(blocks[0]));
    }

    [Fact]
    public void 井桁のあとに空白が無ければ見出しにしない()
    {
        var blocks = NoteMarkup.Parse("##見出しではない");

        Assert.Equal(NoteBlockKind.Paragraph, blocks[0].Kind);
    }

    [Fact]
    public void 点の箇条書きを読む()
    {
        var blocks = NoteMarkup.Parse("- 一つ目\n- 二つ目");

        Assert.Single(blocks);
        Assert.Equal(NoteBlockKind.BulletList, blocks[0].Kind);
        Assert.Equal(2, blocks[0].Items.Length);
        Assert.Equal("一つ目", string.Concat(blocks[0].Items[0].Inlines.Select(i => i.Text)));
    }

    [Fact]
    public void 番号の箇条書きを読む()
    {
        var blocks = NoteMarkup.Parse("1. 一つ目\n2. 二つ目");

        Assert.Single(blocks);
        Assert.Equal(NoteBlockKind.NumberedList, blocks[0].Kind);
        Assert.Equal(2, blocks[0].Items.Length);
    }

    [Fact]
    public void 点と番号は別の箇条書きになる()
    {
        var blocks = NoteMarkup.Parse("- 点\n1. 番号");

        Assert.Equal(2, blocks.Length);
        Assert.Equal(NoteBlockKind.BulletList, blocks[0].Kind);
        Assert.Equal(NoteBlockKind.NumberedList, blocks[1].Kind);
    }

    [Fact]
    public void 太字と斜体を読む()
    {
        var blocks = NoteMarkup.Parse("これは**太字**と*斜体*です");

        var kinds = blocks[0].Inlines.Select(inline => inline.Kind).ToArray();
        Assert.Equal(
            [
                NoteInlineKind.Text, NoteInlineKind.Bold, NoteInlineKind.Text,
                NoteInlineKind.Italic, NoteInlineKind.Text,
            ],
            kinds);
        Assert.Equal("太字", blocks[0].Inlines[1].Text);
        Assert.Equal("斜体", blocks[0].Inlines[3].Text);
    }

    [Fact]
    public void 閉じていない装飾は平文のまま残る()
    {
        // 消すと書き手が誤りに気付けない
        var blocks = NoteMarkup.Parse("**閉じ忘れ");

        var inline = Assert.Single(blocks[0].Inlines);
        Assert.Equal(NoteInlineKind.Text, inline.Kind);
        Assert.Equal("**閉じ忘れ", inline.Text);
    }

    [Fact]
    public void httpsのリンクだけを受け付ける()
    {
        var blocks = NoteMarkup.Parse("[会社案内](https://example.com/about)");

        var inline = Assert.Single(blocks[0].Inlines);
        Assert.Equal(NoteInlineKind.Link, inline.Kind);
        Assert.Equal("https://example.com/about", inline.Href);
    }

    [Fact]
    public void 自前資産の画像とリンクだけを構造へ変換する()
    {
        var imageId = Guid.NewGuid();
        var linkId = Guid.NewGuid();

        var blocks = NoteMarkup.Parse(
            $"![案内図](asset:{imageId:D}) [大きく見る](asset:{linkId:D})");

        Assert.Equal(3, blocks[0].Inlines.Length);
        Assert.Equal(NoteInlineKind.AssetImage, blocks[0].Inlines[0].Kind);
        Assert.Equal(imageId, blocks[0].Inlines[0].AssetId);
        Assert.Equal(NoteInlineKind.AssetLink, blocks[0].Inlines[2].Kind);
        Assert.Equal(linkId, blocks[0].Inlines[2].AssetId);
        Assert.All(blocks[0].Inlines, inline => Assert.Null(inline.Href));
    }

    [Theory]
    [InlineData("![外部](https://example.com/image.png)")]
    [InlineData("![データ](data:image/png;base64,AAAA)")]
    [InlineData("![不正](asset:not-a-guid)")]
    public void 外部画像と不正な資産IDは画像にしない(string markup)
    {
        var blocks = NoteMarkup.Parse(markup);

        Assert.DoesNotContain(
            blocks.SelectMany(block => block.Inlines),
            inline => inline.Kind == NoteInlineKind.AssetImage);
    }

    [Fact]
    public void 資産IDを書き換えても他の原文は変えない()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var markup = $"前 ![画像](asset:{source:D}) 後";

        var rewritten = NoteMarkup.RewriteAssetIds(
            markup, new Dictionary<Guid, Guid> { [source] = target });

        Assert.Equal($"前 ![画像](asset:{target:D}) 後", rewritten);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("http://example.com")]
    [InlineData("/admin/users")]
    [InlineData("//example.com")]
    [InlineData("vbscript:msgbox")]
    public void https以外はリンクにせず文字だけ残す(string href)
    {
        var blocks = NoteMarkup.Parse($"[踏ませたい]({href})");

        // 括弧を含む URL は途中で切れるため、断片が平文として残ることがある。
        // **確かめたいのは「リンクが 1 つも作られないこと」**
        var inlines = blocks[0].Inlines;
        Assert.All(inlines, inline => Assert.NotEqual(NoteInlineKind.Link, inline.Kind));
        Assert.All(inlines, inline => Assert.Null(inline.Href));
        Assert.Contains("踏ませたい", string.Concat(inlines.Select(i => i.Text)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void HTMLは平文として残る()
    {
        // **記法にしか意味を持たせない。** 消すのではなく、ただの文字として扱う
        var blocks = NoteMarkup.Parse("<script>alert(1)</script>");

        var inline = Assert.Single(blocks[0].Inlines);
        Assert.Equal(NoteInlineKind.Text, inline.Kind);
        Assert.Equal("<script>alert(1)</script>", inline.Text);
    }

    [Fact]
    public void 上限を超えた分は切り落とす()
    {
        var markup = new string('あ', NoteMarkup.MaximumLength + 100);

        var blocks = NoteMarkup.Parse(markup);

        Assert.Equal(NoteMarkup.MaximumLength, PlainText(blocks[0]).Length);
    }

    [Fact]
    public void 記法へ戻しても同じ結果になる()
    {
        const string markup =
            "## ご案内\n"
            + "**当社**のアンケートです。\n"
            + "\n"
            + "- [会社案内](https://example.com/)\n"
            + "- *任意*の設問もあります\n"
            + "\n"
            + "1. 一つ目\n"
            + "2. 二つ目";

        var once = NoteMarkup.Render(NoteMarkup.Parse(markup));
        var twice = NoteMarkup.Render(NoteMarkup.Parse(once));

        Assert.Equal(once, twice);
        Assert.Contains("## ご案内", once, StringComparison.Ordinal);
        Assert.Contains("- [会社案内](https://example.com/)", once, StringComparison.Ordinal);
        Assert.Contains("2. 二つ目", once, StringComparison.Ordinal);
    }

    [Fact]
    public void 空の並びを戻すと空文字になる()
    {
        Assert.Equal(string.Empty, NoteMarkup.Render([]));
        Assert.Equal(string.Empty, NoteMarkup.Render(default));
    }

    [Fact]
    public void 説明文ブロックと記法を選んだ設問だけが書式を持つ()
    {
        var note = new Question
        {
            QuestionId = "n1",
            Type = QuestionType.Note,
            Title = LocalizedText.Japanese("ご案内"),
            Description = LocalizedText.Japanese("**太字**"),
        };
        var text = note with { Type = QuestionType.Text };
        var confirm = note with
        {
            Type = QuestionType.Confirm,
            Settings = new QuestionSettings { DescriptionFormat = DescriptionFormat.Markup },
        };

        Assert.NotNull(note.NoteBlocks);
        Assert.Equal(NoteInlineKind.Bold, note.NoteBlocks!["ja"][0].Inlines[0].Kind);
        Assert.Null(text.NoteBlocks);
        Assert.Null(text.DescriptionBlocks);
        Assert.Equal(
            NoteInlineKind.Bold,
            confirm.DescriptionBlocks!["ja"][0].Inlines[0].Kind);
    }

    [Fact]
    public void 通常の設問は既定で説明文の記法を解釈しない()
    {
        var question = new Question
        {
            QuestionId = "q1",
            Type = QuestionType.Text,
            Title = LocalizedText.Japanese("設問"),
            Description = LocalizedText.Japanese("**そのまま**"),
        };

        Assert.Null(question.DescriptionBlocks);
    }

    [Fact]
    public void 説明文ブロックは設定がプレーンでも記法を解釈する()
    {
        var note = new Question
        {
            QuestionId = "n1",
            Type = QuestionType.Note,
            Title = LocalizedText.Japanese("ご案内"),
            Description = LocalizedText.Japanese("**太字**"),
            Settings = new QuestionSettings { DescriptionFormat = DescriptionFormat.Plain },
        };

        Assert.Equal(NoteInlineKind.Bold, note.NoteBlocks!["ja"][0].Inlines[0].Kind);
    }

    [Fact]
    public void 説明が空なら書式を持たない()
    {
        var note = new Question
        {
            QuestionId = "n1",
            Type = QuestionType.Note,
            Title = LocalizedText.Japanese("ご案内"),
        };

        Assert.Null(note.NoteBlocks);
    }

    [Fact]
    public void 言語ごとに書式を持つ()
    {
        var note = new Question
        {
            QuestionId = "n1",
            Type = QuestionType.Note,
            Title = LocalizedText.Japanese("ご案内"),
            Description = new LocalizedText(new Dictionary<string, string>
            {
                ["ja"] = "## ご案内",
                ["en"] = "- item",
            }),
        };

        var blocks = note.NoteBlocks;

        Assert.NotNull(blocks);
        Assert.Equal(NoteBlockKind.Heading, blocks!["ja"][0].Kind);
        Assert.Equal(NoteBlockKind.BulletList, blocks["en"][0].Kind);
    }

    [Fact]
    public void 既定のImmutableArrayは空として扱う()
    {
        // JSON から読んだ値がそのまま列挙されても落ちないこと
        var block = new NoteBlock(NoteBlockKind.Paragraph);

        Assert.Empty(block.Inlines);
        Assert.Empty(block.Items);
        Assert.Empty(new NoteListItem(default).Inlines);
    }

    [Fact]
    public void 定義のJSONに書式が載る()
    {
        var note = new Question
        {
            QuestionId = "n1",
            Type = QuestionType.Note,
            Title = LocalizedText.Japanese("ご案内"),
            Description = LocalizedText.Japanese("[案内](https://example.com/)"),
        };

        var json = SurveyJson.Serialize(note);

        Assert.Contains("\"noteBlocks\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Link\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 完了画面も説明文と同じ安全な書式を持つ()
    {
        var assetId = Guid.NewGuid();
        var definition = new SurveyDefinition
        {
            SurveyId = Guid.NewGuid().ToString(),
            Version = 1,
            Title = LocalizedText.Japanese("調査"),
            ConfirmationMessage = new LocalizedText(new Dictionary<string, string>
            {
                ["ja"] = $"![資料](asset:{assetId:D})",
                ["en"] = $"[Download](asset:{assetId:D})",
            }),
        };

        Assert.Equal(
            NoteInlineKind.AssetImage,
            definition.ConfirmationBlocks!["ja"][0].Inlines[0].Kind);
        Assert.Equal(
            NoteInlineKind.AssetLink,
            definition.ConfirmationBlocks["en"][0].Inlines[0].Kind);
        Assert.True(SurveyAssetReferences.Contains(definition, assetId));
    }

}
