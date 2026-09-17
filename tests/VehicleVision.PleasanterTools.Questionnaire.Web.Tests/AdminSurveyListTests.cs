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
                    "Columns": [{ "ColumnName": "ClassA", "ControlType": "ChoicesText", "ChoicesText": "[[123]]" }],
                    "Links": []
                  }
                }
              }
            }
            """)!;

        Assert.Equal(["ClassA"], SiteSettingsSynchronizer.MissingLinks(response, ["ClassA"]));
    }
}
