using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>アンケート一覧をページに切って返すところ（Issue #79）。</summary>
/// <remarks>
/// **DB へは繋がない。** 確かめたいのは、**総数を数えずに「次がある」を返す**という
/// 一点（アンケートは消さずに溜まるので、開くたびに全件を数えない）。
/// </remarks>
public class AdminSurveyListTests
{
    private static SurveySummary Summary() =>
        new(Guid.NewGuid(),
            "PublicId",
            "題名",
            1L,
            0,
            null,
            new DateTime(2026, 8, 21, 3, 0, 0, DateTimeKind.Utc),
            null,
            null,
            null,
            0,
            false,
            false);

    [Fact]
    public void 次のページがあるかを数え直さずに返す()
    {
        // **1 件多く読んでいる**ので、返すのは take 件まで
        var rows = Enumerable.Range(0, 4).Select(_ => Summary()).ToList();

        var page = AdminSurveyEndpoints.ToResponse(rows, take: 3);

        Assert.Equal(3, page.Items.Count);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void 最後のページでは続きが無いと返す()
    {
        var rows = Enumerable.Range(0, 2).Select(_ => Summary()).ToList();

        Assert.False(AdminSurveyEndpoints.ToResponse(rows, take: 3).HasMore);
    }

    [Fact]
    public void 一件も無くても壊れない()
    {
        var page = AdminSurveyEndpoints.ToResponse([], take: 3);

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void 絞り込みの既定は何も掛けない()
    {
        // **指定が無ければ全部**（状態も題名も条件にしない）
        var query = new SurveyListQuery();

        Assert.Null(query.Status);
        Assert.Null(query.TitleContains);
        Assert.Equal(0, query.Offset);
        Assert.False(query.IncludeArchived);
    }

    [Fact]
    public void GetSiteの列定義を接頭辞ごとに数える()
    {
        var response = JsonNode.Parse("""
            {
              "Response": {
                "Data": {
                  "SiteSettings": {
                    "Columns": [
                      { "ColumnName": "ClassA" },
                      { "ColumnName": "Class001" },
                      { "ColumnName": "NumA" },
                      { "ColumnName": "Title" }
                    ]
                  }
                }
              }
            }
            """);

        var availableByPrefix = AdminSurveyEndpoints.AvailableColumnsFrom(response);

        Assert.NotNull(availableByPrefix);
        Assert.Equal(2, availableByPrefix["Class"]);
        Assert.Equal(1, availableByPrefix["Num"]);
        Assert.DoesNotContain("Title", availableByPrefix.Keys);
    }

    [Fact]
    public void GetSiteの項目名は利用者が付けたものだけを物理名ごとに返す()
    {
        var response = JsonNode.Parse(
            """
            {
              "Response": {
                "Data": {
                  "SiteSettings": {
                    "Columns": [
                      { "ColumnName": "ClassA", "LabelText": "部署名", "GridLabelText": "部署" },
                      { "ColumnName": "NumA", "LabelText": "numa" },
                      { "ColumnName": "ClassB", "LabelText": "" },
                      { "ColumnName": "ClassC", "GridLabelText": "区分" },
                      { "ColumnName": "DateA" },
                      { "ColumnName": "Title", "LabelText": "件名" }
                    ]
                  }
                }
              }
            }
            """);

        var labels = AdminSurveyEndpoints.ColumnDetailsFrom(response);

        // **物理名と同じ文字列は返さない。** 本アプリの同期が書く値で、添えても情報が増えない
        Assert.Equal(3, labels.Count);
        // **編集画面と一覧は別々に返す**（実機で別々に返ることを確かめた）
        Assert.Equal("部署名", labels["ClassA"].Label);
        Assert.Equal("部署", labels["classa"].GridLabel);
        Assert.Null(labels["ClassC"].Label);
        Assert.Equal("区分", labels["ClassC"].GridLabel);
        Assert.Equal("件名", labels["Title"].Label);
    }

    [Fact]
    public void GetSiteのリンクと選択肢と参照先を分けて返す()
    {
        // **実機（Pleasanter 1.5.8.1）の応答の形そのまま**（Issue #549）
        var response = JsonNode.Parse(
            """
            {
              "Response": {
                "Data": {
                  "SiteSettings": {
                    "Columns": [
                      { "ColumnName": "ClassA", "LabelText": "部署",
                        "ChoicesText": "1,営業\n2,開発\n3,総務", "ChoicesControlType": "Radio" },
                      { "ColumnName": "ClassB", "LabelText": "取引先", "ChoicesText": "[[1]]", "Link": true },
                      { "ColumnName": "ClassD", "LabelText": "選択肢とリンク",
                        "ChoicesText": "1,AAA\nBBB\n[[1]]", "Link": true }
                    ]
                  }
                }
              }
            }
            """);

        var details = AdminSurveyEndpoints.ColumnDetailsFrom(response);

        var department = details["ClassA"];
        Assert.False(department.IsLink);
        Assert.Equal(3, department.ChoiceCount);
        Assert.Equal("Radio", department.ChoicesControlType);
        Assert.Equal("1", department.Choices[0].Value);
        Assert.Equal("営業", department.Choices[0].Text);
        Assert.Empty(department.References);

        // **リンクの参照先は選択肢に数えない。** 参照先のレコードは列挙できない
        var partner = details["ClassB"];
        Assert.True(partner.IsLink);
        Assert.Equal(0, partner.ChoiceCount);
        Assert.Equal(["1"], partner.References);

        // 選択肢とリンクは混在できる。表示文字列が無い行は値をそのまま表示に使う
        var mixed = details["ClassD"];
        Assert.True(mixed.IsLink);
        Assert.Equal(2, mixed.ChoiceCount);
        Assert.Equal("BBB", mixed.Choices[1].Text);
        Assert.Equal(["1"], mixed.References);
    }

    [Fact]
    public void GetSiteのリンクはJSONの形でもサイトIDを参照先として返す()
    {
        // **実機（Pleasanter 1.5.8.1）の応答の形そのまま。** JSON の形は `Link: true` が付かない
        var response = JsonNode.Parse(
            """
            {
              "Response": {
                "Data": {
                  "SiteSettings": {
                    "Columns": [
                      { "ColumnName": "ClassA", "LabelText": "簡易", "ChoicesText": "[[1,NoAddButton]]", "Link": true },
                      { "ColumnName": "ClassB", "LabelText": "JSON",
                        "ChoicesText": "[{\"SiteId\":1,\"NoAddButton\":true,\"Priority\":1},{\"SiteId\":2}]" },
                      { "ColumnName": "ClassC", "ChoicesText": "[1,2,3]" },
                      { "ColumnName": "ClassD", "ChoicesText": "[壊れた" }
                    ]
                  }
                }
              }
            }
            """);

        var details = AdminSurveyEndpoints.ColumnDetailsFrom(response);

        // 行の形。options はそのまま参照先に残る
        Assert.True(details["ClassA"].IsLink);
        Assert.Equal("Lines", details["ClassA"].LinkFormat);
        Assert.Equal(["1,NoAddButton"], details["ClassA"].References);

        // JSON の形は `Link` が無くてもリンクと分かり、SiteId を参照先に返す。選択肢は無い
        Assert.True(details["ClassB"].IsLink);
        Assert.Equal("Json", details["ClassB"].LinkFormat);
        Assert.Equal(["1", "2"], details["ClassB"].References);
        Assert.Equal(0, details["ClassB"].ChoiceCount);

        // **SiteId を持たない配列や、読めない形は、リンクにしない。** 選択肢の行として扱い、画面を止めない
        Assert.DoesNotContain("ClassC", details.Keys.Where(key => details[key].IsLink));
        Assert.False(details["ClassD"].IsLink);
    }

    [Fact]
    public void GetSiteの選択肢は上限までしか返さず全件の数は返す()
    {
        var lines = string.Join("\\n", Enumerable.Range(1, 120).Select(i => $"{i},項目{i}"));
        var response = JsonNode.Parse(
            $$"""
            { "Response": { "Data": { "SiteSettings": { "Columns": [
              { "ColumnName": "ClassA", "ChoicesText": "{{lines}}" } ] } } } }
            """);

        var detail = AdminSurveyEndpoints.ColumnDetailsFrom(response)["ClassA"];

        Assert.Equal(120, detail.ChoiceCount);
        Assert.Equal(50, detail.Choices.Count);
    }

    [Fact]
    public void GetSiteの列定義が無ければ項目名は空で返す()
    {
        var response = JsonNode.Parse("""{ "Response": { "Data": { "SiteSettings": {} } } }""");

        Assert.Empty(AdminSurveyEndpoints.ColumnDetailsFrom(response));
        Assert.Empty(AdminSurveyEndpoints.ColumnDetailsFrom(null));
    }

    [Fact]
    public void GetSiteの列定義が無ければ標準の本数へ戻す()
    {
        var response = JsonNode.Parse("""{ "Response": { "Data": { "SiteSettings": {} } } }""");

        Assert.Null(AdminSurveyEndpoints.AvailableColumnsFrom(response));
    }

    [Fact]
    public void 同期は対象列だけを加え他の設定と列順を保つ()
    {
        var response = JsonNode.Parse("""
            {
              "Response": {
                "Data": {
                  "SiteSettings": {
                    "Columns": [
                      { "ColumnName": "ClassB", "LabelText": "既存" },
                      { "ColumnName": "ClassA", "LabelText": "リンク", "ChoicesText": "[[123]]" }
                    ],
                    "GridColumns": ["ClassB", "ClassA"],
                    "EditorColumnHash": { "General": ["ClassB", "ClassA"], "Other": ["ClassC"] },
                    "HistoryColumns": ["ClassB", "ClassA"],
                    "Scripts": { "all": "保持する" },
                    "Styles": { "all": "保持する" }
                  }
                }
              }
            }
            """)!;
        var mapping = new MappingDefinition
        {
            Assignments = [ColumnAssignment.Direct("ClassA", new MappingSource("q1"))],
        };

        var plan = SiteSettingsSynchronizer.Build(response, mapping);
        var settings = plan.SiteSettings;

        Assert.Empty(plan.AddedColumns);
        Assert.Equal(["ClassB", "ClassA"], plan.GridColumns);
        Assert.Equal(["ClassB", "ClassA"], plan.EditorColumns);
        Assert.Equal(["ClassB", "ClassA"], plan.HistoryColumns);
        Assert.Equal("[[123]]", settings["Columns"]![1]!["ChoicesText"]!.GetValue<string>());
        Assert.Equal("保持する", settings["Scripts"]!["all"]!.GetValue<string>());
        Assert.Equal("保持する", settings["Styles"]!["all"]!.GetValue<string>());
        Assert.Equal(["ClassC"], settings["EditorColumnHash"]!["Other"]!.AsArray()
            .Select(value => value!.GetValue<string>()));

        var repeated = SiteSettingsSynchronizer.Build(JsonNode.Parse($$"""
            { "Response": { "Data": { "SiteSettings": {{settings.ToJsonString()}} } } }
            """)!, mapping);
        Assert.Equal(settings.ToJsonString(), repeated.SiteSettings.ToJsonString());
    }

    [Fact]
    public void リンク設定が反映されなければ対象列を返す()
    {
        var response = JsonNode.Parse("""
            {
              "Response": {
                "Data": {
                  "SiteSettings": {
                    "Columns": [{ "ColumnName": "ClassA", "ChoicesText": "[[123]]" }],
                    "Links": []
                  }
                }
              }
            }
            """)!;

        // ⚠️ **GetSite は Links を返さない**（実機で確認。ChoicesText は戻るが Links は null）。
        // リンク先のサイト ID は ChoicesText から読む
        Assert.Equal([123L], SiteSettingsSynchronizer.LinkedSiteIds(response, ["ClassA"]));

        // **対象外の列のリンクは拾わない**
        Assert.Empty(SiteSettingsSynchronizer.LinkedSiteIds(response, ["ClassB"]));
    }
}
