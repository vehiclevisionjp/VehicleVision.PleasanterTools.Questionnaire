using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>編集中の定義の読み書きを 3 RDBMS で確かめる。</summary>
/// <remarks>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </remarks>
public class SurveyDraftStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static (ISurveyDraftStore Drafts, ISurveyRepository Surveys) Create(
        DatabaseProvider provider,
        string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        return (new SurveyDraftStore(factory), new SurveyRepository(factory));
    }

    private static async Task<Guid> CreateSurveyAsync(ISurveyRepository surveys)
    {
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(new SurveyRecord(
            surveyId,
            $"pub-{Guid.NewGuid():N}",
            "検証用",
            PleasanterSiteId: 1,
            ResponseJsonColumn: null,
            Status: (int)SurveyStatus.Draft,
            PublishedVersion: null));
        return surveyId;
    }

    private static SurveyDefinition Definition(Guid surveyId, params string[] questionIds) => new()
    {
        SurveyId = surveyId.ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("満足度調査"),
        Description = LocalizedText.Japanese("ご協力ください"),
        ConfirmationMessage = LocalizedText.Japanese("ありがとうございました"),
        Pages =
        [
            new Page
            {
                PageId = "page-1",
                Title = LocalizedText.Japanese("1 ページ目"),
                Questions = questionIds
                    .Select(id => new Question
                    {
                        QuestionId = id,
                        Type = QuestionType.Radio,
                        Title = LocalizedText.Japanese($"設問 {id}"),
                        IsRequired = true,
                        Choices =
                        [
                            new Choice("good", LocalizedText.Japanese("よい")),
                            new Choice("bad", LocalizedText.Japanese("わるい")),
                            new Choice("other", LocalizedText.Japanese("その他"), IsOther: true),
                        ],
                        Settings = new QuestionSettings { MaxLength = 100 },
                    })
                    .ToImmutableArray(),
            },
        ],
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task グリッドの行と割り当ても保存して読み直せる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **行は SettingsJson に入るが、割り当ての行は列で持っている**（Issue #54）。
        // 足し忘れると、行ごとの割り当てが黙って消える
        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var definition = new SurveyDefinition
        {
            SurveyId = surveyId.ToString(),
            Version = 1,
            Title = LocalizedText.Japanese("行列の見本"),
            Pages =
            [
                new Page
                {
                    PageId = "page-1",
                    Questions =
                    [
                        new Question
                        {
                            QuestionId = "q-grid",
                            Type = QuestionType.Grid,
                            Title = LocalizedText.Japanese("満足度"),
                            Choices =
                            [
                                new Choice("good", LocalizedText.Japanese("よい")),
                                new Choice("bad", LocalizedText.Japanese("わるい")),
                            ],
                            Settings = new QuestionSettings
                            {
                                Rows =
                                [
                                    new GridRow("price", LocalizedText.Japanese("価格")),
                                    new GridRow("quality", LocalizedText.Japanese("品質")),
                                ],
                            },
                        },
                    ],
                },
            ],
        };

        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct(
                    "ClassA", new MappingSource("q-grid", QuestionPort.Value, "price")),
                ColumnAssignment.Direct(
                    "ClassB", new MappingSource("q-grid", QuestionPort.Value, "quality")),
            ],
        };

        await drafts.SaveAsync(surveyId, definition, mapping, expectedRevision: 0);

        var loaded = await drafts.LoadAsync(surveyId);
        Assert.NotNull(loaded);

        var question = loaded.Definition.Pages[0].Questions[0];
        Assert.Equal(2, question.Settings.Rows.Length);
        Assert.Equal("price", question.Settings.Rows[0].RowId);
        Assert.Equal("価格", question.Settings.Rows[0].Label.Get("ja"));

        // **行の識別子が割り当てに残っていること**
        var assignments = loaded.Mapping.Assignments
            .ToDictionary(assignment => assignment.TargetColumn, StringComparer.Ordinal);

        Assert.Equal("price", Assert.Single(assignments["ClassA"].Sources).RowId);
        Assert.Equal("quality", Assert.Single(assignments["ClassB"].Sources).RowId);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 分岐も保存して読み直せる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **下書きは列で持っている。** 分岐を足したら、そこも足さないと黙って消える
        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var definition = new SurveyDefinition
        {
            SurveyId = surveyId.ToString(),
            Version = 1,
            Title = LocalizedText.Japanese("分岐の見本"),
            Pages =
            [
                new Page
                {
                    PageId = "page-1",
                    Next = PageTransition.To("page-3"),
                    Questions =
                    [
                        new Question
                        {
                            QuestionId = "q1",
                            Type = QuestionType.Radio,
                            Title = LocalizedText.Japanese("満足度"),
                            Choices =
                            [
                                new Choice(
                                    "good",
                                    LocalizedText.Japanese("よい"),
                                    Next: PageTransition.Submit),
                                new Choice("bad", LocalizedText.Japanese("わるい")),
                            ],
                        },
                        new Question
                        {
                            QuestionId = "q2",
                            Type = QuestionType.Text,
                            Title = LocalizedText.Japanese("理由"),
                            VisibleWhen = new VisibilityCondition
                            {
                                Match = ConditionMatch.Any,
                                Rules =
                                [
                                    new ConditionRule("q1", ConditionOperator.Equals, "bad"),
                                ],
                            },
                        },
                    ],
                },
                new Page { PageId = "page-3", Questions = [] },
            ],
        };

        await drafts.SaveAsync(surveyId, definition, new MappingDefinition(), expectedRevision: 0);

        var loaded = await drafts.LoadAsync(surveyId);
        Assert.NotNull(loaded);

        var page = loaded.Definition.Pages[0];
        Assert.Equal("page-3", page.Next!.PageId);
        Assert.Equal(PageTransitionKind.Submit, page.Questions[0].Choices[0].Next!.Kind);

        // **行き先を持たない選択肢は NULL のまま**
        Assert.Null(page.Questions[0].Choices[1].Next);

        var condition = page.Questions[1].VisibleWhen;
        Assert.NotNull(condition);
        Assert.Equal(ConditionMatch.Any, condition.Match);
        Assert.Equal("bad", Assert.Single(condition.Rules).Value);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 保存して読み直すと同じ定義になる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var definition = Definition(surveyId, "q1", "q2");
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct("ClassA", new MappingSource("q1")),
                ColumnAssignment.Converted(
                    "ClassB",
                    MappingConverter.Of("join", ("separator", "、")),
                    new MappingSource("q1"),
                    new MappingSource("q2", QuestionPort.OtherText)),
            ],
        };

        var revision = await drafts.SaveAsync(surveyId, definition, mapping, expectedRevision: 0);
        Assert.Equal(1, revision);

        var loaded = await drafts.LoadAsync(surveyId);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Revision);

        Assert.Equal("満足度調査", loaded.Definition.Title.Get("ja"));
        Assert.Equal("ありがとうございました", loaded.Definition.ConfirmationMessage!.Get("ja"));

        var page = Assert.Single(loaded.Definition.Pages);
        Assert.Equal("1 ページ目", page.Title!.Get("ja"));
        Assert.Equal(["q1", "q2"], page.Questions.Select(q => q.QuestionId).ToArray());

        var first = page.Questions[0];
        Assert.Equal(QuestionType.Radio, first.Type);
        Assert.True(first.IsRequired);
        Assert.Equal(100, first.Settings.MaxLength);
        // **「その他」の印が残ること。** 落ちると自由記述欄が出なくなる
        Assert.Equal(["good", "bad", "other"], first.Choices.Select(c => c.Value).ToArray());
        Assert.True(first.Choices[2].IsOther);

        Assert.Equal(2, loaded.Mapping.Assignments.Length);
        var direct = loaded.Mapping.Assignments.Single(a => a.TargetColumn == "ClassA");
        Assert.Null(direct.Converter);
        Assert.Equal("q1", Assert.Single(direct.Sources).QuestionId);

        var converted = loaded.Mapping.Assignments.Single(a => a.TargetColumn == "ClassB");
        Assert.Equal("join", converted.Converter!.Operation);
        Assert.Equal("、", converted.Converter.Config["separator"]);
        // **入力の順序が変換の結果を決める。** 崩れてはいけない
        Assert.Equal(["q1", "q2"], converted.Sources.Select(s => s.QuestionId).ToArray());
        Assert.Equal(QuestionPort.OtherText, converted.Sources[1].Port);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 消した設問と割り当ては残らない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var revision = await drafts.SaveAsync(
            surveyId,
            Definition(surveyId, "q1", "q2"),
            new MappingDefinition
            {
                Assignments = [ColumnAssignment.Direct("ClassA", new MappingSource("q2"))],
            },
            expectedRevision: 0);

        // q2 を消し、割り当ても消す
        await drafts.SaveAsync(
            surveyId,
            Definition(surveyId, "q1"),
            new MappingDefinition { Assignments = [] },
            expectedRevision: revision);

        var loaded = await drafts.LoadAsync(surveyId);

        // **入れ替える。** 残ると、消したはずの設問へ回答が付く
        Assert.Equal(["q1"], loaded!.Definition.Pages[0].Questions.Select(q => q.QuestionId).ToArray());
        Assert.Empty(loaded.Mapping.Assignments);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 古い版で保存しようとすると弾かれる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        await drafts.SaveAsync(
            surveyId, Definition(surveyId, "q1"), new MappingDefinition(), expectedRevision: 0);

        // **同じ版で 2 回目を投げる。** 先に読んだ画面から保存した状況
        var conflict = await Assert.ThrowsAsync<SurveyDraftConflictException>(() =>
            drafts.SaveAsync(
                surveyId, Definition(surveyId, "q2"), new MappingDefinition(), expectedRevision: 0));

        Assert.Equal(0, conflict.Expected);
        Assert.Equal(1, conflict.Actual);

        // **弾かれた側の変更は入っていない**
        var loaded = await drafts.LoadAsync(surveyId);
        Assert.Equal(["q1"], loaded!.Definition.Pages[0].Questions.Select(q => q.QuestionId).ToArray());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 次に公開される版が入っている(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var loaded = await drafts.LoadAsync(surveyId);
        // まだ公開していないので次は 1 版目
        Assert.Equal(1, loaded!.Definition.Version);

        await surveys.PublishAsync(
            surveyId, 1, loaded.Definition, loaded.Mapping, publishedBy: null);

        var afterPublish = await drafts.LoadAsync(surveyId);
        Assert.Equal(2, afterPublish!.Definition.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 一覧に出る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var list = await drafts.ListAsync(new SurveyListQuery());

        Assert.Contains(list, summary => summary.SurveyId == surveyId);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 無いアンケートはnullが返る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, _) = Create(provider, connectionString);

        Assert.Null(await drafts.LoadAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// 複製で設問・選択肢・**分岐**・マッピングが写ること（Issue #46）。
    /// **下書きは列で持っている**ので、写し漏れると黙って消える。
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 複製すると分岐もマッピングも写る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var definition = new SurveyDefinition
        {
            SurveyId = surveyId.ToString(),
            Version = 1,
            Title = LocalizedText.Japanese("複製の元"),
            Pages =
            [
                new Page
                {
                    PageId = "page-1",
                    Next = PageTransition.To("page-3"),
                    Questions =
                    [
                        new Question
                        {
                            QuestionId = "q1",
                            Type = QuestionType.Radio,
                            Title = LocalizedText.Japanese("満足度"),
                            Choices =
                            [
                                new Choice(
                                    "good",
                                    LocalizedText.Japanese("よい"),
                                    Next: PageTransition.Submit),
                                new Choice("bad", LocalizedText.Japanese("わるい")),
                            ],
                        },
                        new Question
                        {
                            QuestionId = "q2",
                            Type = QuestionType.Text,
                            Title = LocalizedText.Japanese("理由"),
                            VisibleWhen = new VisibilityCondition
                            {
                                Match = ConditionMatch.Any,
                                Rules = [new ConditionRule("q1", ConditionOperator.Equals, "bad")],
                            },
                        },
                    ],
                },
                new Page { PageId = "page-3", Questions = [] },
            ],
        };

        var mapping = new MappingDefinition
        {
            Assignments = [ColumnAssignment.Direct("ClassA", new MappingSource("q1"))],
        };

        await drafts.SaveAsync(surveyId, definition, mapping, expectedRevision: 0);

        var target = new SurveyDuplicationTarget(
            Guid.NewGuid(), $"pub-{Guid.NewGuid():N}", PleasanterSiteId: 2, ResponseJsonColumn: null);

        Assert.True(await drafts.DuplicateAsync(surveyId, target));

        var copy = await drafts.LoadAsync(target.SurveyId);
        Assert.NotNull(copy);

        // **題名は「〜のコピー」。** 一覧で元と見分けが付かないと取り違える
        Assert.Equal("複製の元のコピー", copy.Definition.Title.Get("ja"));

        var page = copy.Definition.Pages[0];
        Assert.Equal("page-3", page.Next!.PageId);
        Assert.Equal(PageTransitionKind.Submit, page.Questions[0].Choices[0].Next!.Kind);
        Assert.Null(page.Questions[0].Choices[1].Next);
        Assert.Equal("bad", Assert.Single(page.Questions[1].VisibleWhen!.Rules).Value);

        var assignment = Assert.Single(copy.Mapping.Assignments);
        Assert.Equal("ClassA", assignment.TargetColumn);
        Assert.Equal("q1", Assert.Single(assignment.Sources).QuestionId);

        // **元は変わらない**
        var source = await drafts.LoadAsync(surveyId);
        Assert.Equal("複製の元", source!.Definition.Title.Get("ja"));
    }

    /// <summary>
    /// **公開用 ID・サイト・公開状態・公開済みの版は写さない**（Issue #46）。
    /// 写すと 2 つのアンケートが同じ URL と同じサイトを指す。
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 複製は下書きとして作られる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        await drafts.SaveAsync(
            surveyId, Definition(surveyId, "q1"), new MappingDefinition(), expectedRevision: 0);

        // 元を公開しておく。**公開済みの版が写らないことを見る**
        var published = await drafts.LoadAsync(surveyId);
        await surveys.PublishAsync(surveyId, 1, published!.Definition, published.Mapping, null);
        var original = await surveys.FindBySurveyIdAsync(surveyId);
        await surveys.SaveAsync(original! with { Status = (int)SurveyStatus.Published });

        var target = new SurveyDuplicationTarget(
            Guid.NewGuid(), $"pub-{Guid.NewGuid():N}", PleasanterSiteId: 999, ResponseJsonColumn: null);

        Assert.True(await drafts.DuplicateAsync(surveyId, target));

        var record = await surveys.FindBySurveyIdAsync(target.SurveyId);
        Assert.NotNull(record);
        Assert.Equal((int)SurveyStatus.Draft, record.Status);
        Assert.Null(record.PublishedVersion);
        Assert.Equal(999, record.PleasanterSiteId);
        Assert.Equal(target.PublicId, record.PublicId);
        Assert.NotEqual(original!.PublicId, record.PublicId);

        // **公開していないので、次に公開されるのは 1 版目**
        var copy = await drafts.LoadAsync(target.SurveyId);
        Assert.Equal(1, copy!.Definition.Version);
        Assert.Equal(0, copy.Revision);
    }

    /// <summary>**中途半端な行を残さない**（Issue #46）。元が無ければ 1 行も入れない。</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 無いアンケートは複製できない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var target = new SurveyDuplicationTarget(
            Guid.NewGuid(), $"pub-{Guid.NewGuid():N}", PleasanterSiteId: 2, ResponseJsonColumn: null);

        Assert.False(await drafts.DuplicateAsync(Guid.NewGuid(), target));

        // **アンケートの行も作られていない**
        Assert.Null(await surveys.FindBySurveyIdAsync(target.SurveyId));
    }

    // ---- テンプレート（Issue #58） -----------------------------------------

    /// <summary>
    /// テンプレートにすると、設問も分岐もマッピングも写る（Issue #58）。
    ///
    /// **題名には「のコピー」を付けない。** そこから作るアンケートの題名になる。
    /// **書き込み先のサイトは持たない。** そこから作るときに指定させる。
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task テンプレートは題名をそのまま写す(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var definition = Definition(surveyId, "q1") with
        {
            Title = LocalizedText.Japanese("テンプレートの元"),
        };

        var mapping = new MappingDefinition
        {
            Assignments = [ColumnAssignment.Direct("ClassA", new MappingSource("q1"))],
        };

        await drafts.SaveAsync(surveyId, definition, mapping, expectedRevision: 0);

        var target = new SurveyTemplateTarget(Guid.NewGuid(), $"pub-{Guid.NewGuid():N}");
        Assert.True(await drafts.SaveAsTemplateAsync(surveyId, target));

        var template = await drafts.LoadAsync(target.TemplateId);
        Assert.NotNull(template);
        Assert.Equal("テンプレートの元", template.Definition.Title.Get("ja"));
        Assert.Equal("ClassA", Assert.Single(template.Mapping.Assignments).TargetColumn);

        // **アンケートの一覧には出ない。** 出ると「未公開のアンケート」に見える
        var listed = await drafts.ListAsync(new SurveyListQuery());
        Assert.DoesNotContain(listed, item => item.SurveyId == target.TemplateId);

        // **テンプレートの一覧には出る**
        var templates = await drafts.ListTemplatesAsync();
        Assert.Contains(templates, item => item.TemplateId == target.TemplateId);
    }

    /// <summary>
    /// テンプレートから作ったアンケートは、**下書き**で、
    /// **公開用 ID とサイトを作り直している**（Issue #58）。
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task テンプレートから作ると下書きになる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        await drafts.SaveAsync(
            surveyId, Definition(surveyId, "q1"), new MappingDefinition(), expectedRevision: 0);

        var templateTarget = new SurveyTemplateTarget(Guid.NewGuid(), $"pub-{Guid.NewGuid():N}");
        Assert.True(await drafts.SaveAsTemplateAsync(surveyId, templateTarget));

        var target = new SurveyDuplicationTarget(
            Guid.NewGuid(), $"pub-{Guid.NewGuid():N}", PleasanterSiteId: 777, ResponseJsonColumn: null);

        Assert.True(await drafts.CreateFromTemplateAsync(templateTarget.TemplateId, target));

        var record = await surveys.FindBySurveyIdAsync(target.SurveyId);
        Assert.NotNull(record);
        Assert.False(record.IsTemplate);
        Assert.Equal((int)SurveyStatus.Draft, record.Status);
        Assert.Null(record.PublishedVersion);
        Assert.Equal(777, record.PleasanterSiteId);

        // **公開用 ID は使い回さない**
        Assert.NotEqual(templateTarget.PublicId, record.PublicId);

        // **題名に「のコピー」は付かない**（回答者に見える文字列）
        var created = await drafts.LoadAsync(target.SurveyId);
        Assert.Equal("満足度調査", created!.Definition.Title.Get("ja"));
    }

    /// <summary>
    /// **アンケートとテンプレートを取り違えたら何もしない**（Issue #58）。
    /// サイトを持たないアンケートや、公開できるテンプレートができてしまう。
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 種類が違えば写さない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        await drafts.SaveAsync(
            surveyId, Definition(surveyId, "q1"), new MappingDefinition(), expectedRevision: 0);

        var templateTarget = new SurveyTemplateTarget(Guid.NewGuid(), $"pub-{Guid.NewGuid():N}");
        Assert.True(await drafts.SaveAsTemplateAsync(surveyId, templateTarget));

        // アンケートをテンプレートの口へ渡す
        var fromSurvey = new SurveyDuplicationTarget(
            Guid.NewGuid(), $"pub-{Guid.NewGuid():N}", PleasanterSiteId: 3, ResponseJsonColumn: null);
        Assert.False(await drafts.CreateFromTemplateAsync(surveyId, fromSurvey));
        Assert.Null(await surveys.FindBySurveyIdAsync(fromSurvey.SurveyId));

        // テンプレートをアンケートの口へ渡す
        var fromTemplate = new SurveyTemplateTarget(Guid.NewGuid(), $"pub-{Guid.NewGuid():N}");
        Assert.False(await drafts.SaveAsTemplateAsync(templateTarget.TemplateId, fromTemplate));
        Assert.Null(await surveys.FindBySurveyIdAsync(fromTemplate.TemplateId));

        var duplicate = new SurveyDuplicationTarget(
            Guid.NewGuid(), $"pub-{Guid.NewGuid():N}", PleasanterSiteId: 4, ResponseJsonColumn: null);
        Assert.False(await drafts.DuplicateAsync(templateTarget.TemplateId, duplicate));
        Assert.Null(await surveys.FindBySurveyIdAsync(duplicate.SurveyId));
    }

    /// <summary>**消せるのはテンプレートだけ**（Issue #58）。</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task テンプレートだけ消せる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        await drafts.SaveAsync(
            surveyId, Definition(surveyId, "q1"), new MappingDefinition(), expectedRevision: 0);

        // **アンケートは消せない。** 設問だけ消えて行が残ることもない
        Assert.False(await drafts.DeleteTemplateAsync(surveyId));
        var survived = await drafts.LoadAsync(surveyId);
        Assert.Single(survived!.Definition.Pages[0].Questions);

        var templateTarget = new SurveyTemplateTarget(Guid.NewGuid(), $"pub-{Guid.NewGuid():N}");
        Assert.True(await drafts.SaveAsTemplateAsync(surveyId, templateTarget));

        Assert.True(await drafts.DeleteTemplateAsync(templateTarget.TemplateId));
        Assert.Null(await surveys.FindBySurveyIdAsync(templateTarget.TemplateId));
        Assert.Null(await drafts.LoadAsync(templateTarget.TemplateId));

        // **2 度目は無い**
        Assert.False(await drafts.DeleteTemplateAsync(templateTarget.TemplateId));
    }
}
