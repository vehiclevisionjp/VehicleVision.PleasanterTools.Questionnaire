using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter.Tests;

/// <summary>添付を <c>AttachmentsHash</c> へ組み立てる。</summary>
/// <remarks>
/// **フィールド名は <c>Name</c> ＋ <c>Base64</c> ＋ <c>Added</c>。**
/// <c>FileName</c> ＋ <c>Base64Binary</c> だと <c>400 Invalid json data</c> になる
/// （<c>_documents/実機検証結果.md</c> 6 章）。
/// </remarks>
public class PleasanterAttachmentBuildTests
{
    private static readonly PleasanterRecordBuilder Builder = new(new PleasanterDateTime("Asia/Tokyo"));

    private static readonly Dictionary<string, ImmutableArray<string>> NoColumns = [];

    private static IReadOnlyList<Dictionary<string, object?>> AttachmentsOf(
        PleasanterRecord record,
        string columnName)
    {
        var hash = Assert.IsType<Dictionary<string, object?>>(record.Body["AttachmentsHash"]);
        var files = Assert.IsType<Dictionary<string, object?>[]>(hash[columnName]);
        return files;
    }

    [Fact]
    public void 名前と中身と追加の印を載せる()
    {
        var record = Builder.Build(
            NoColumns,
            attachments: new Dictionary<string, IReadOnlyList<PleasanterAttachment>>
            {
                ["AttachmentsA"] = [new PleasanterAttachment("a.png", "aGVsbG8=")],
            });

        Assert.Empty(record.Problems);

        var file = Assert.Single(AttachmentsOf(record, "AttachmentsA"));
        Assert.Equal("a.png", file["Name"]);
        Assert.Equal("aGVsbG8=", file["Base64"]);
        Assert.Equal(true, file["Added"]);
    }

    [Fact]
    public void 複数の添付をそのまま並べる()
    {
        var record = Builder.Build(
            NoColumns,
            attachments: new Dictionary<string, IReadOnlyList<PleasanterAttachment>>
            {
                ["AttachmentsA"] =
                [
                    new PleasanterAttachment("a.png", "aGVsbG8="),
                    new PleasanterAttachment("b.png", "d29ybGQ="),
                ],
            });

        var files = AttachmentsOf(record, "AttachmentsA");

        // **添付列だけは 1 列に複数入る**
        Assert.Equal(["a.png", "b.png"], files.Select(file => file["Name"]).ToArray());
    }

    [Fact]
    public void 添付が無くても列は載せる()
    {
        var record = Builder.Build(
            NoColumns,
            attachments: new Dictionary<string, IReadOnlyList<PleasanterAttachment>>
            {
                ["AttachmentsA"] = [],
            });

        // **入れないと「添付を外した」編集が伝わらず、前の添付が残る**
        Assert.Empty(AttachmentsOf(record, "AttachmentsA"));
    }

    [Fact]
    public void 添付列でない書き込み先は弾く()
    {
        var record = Builder.Build(
            NoColumns,
            attachments: new Dictionary<string, IReadOnlyList<PleasanterAttachment>>
            {
                ["ClassA"] = [new PleasanterAttachment("a.png", "aGVsbG8=")],
            });

        Assert.Contains(record.Problems, problem => problem.ColumnName == "ClassA");
        Assert.False(record.Body.ContainsKey("AttachmentsHash"));
    }

    [Fact]
    public void 値のマッピングから添付列へは写せない()
    {
        // 名前だけを添付列へ入れてもファイルとしては取り出せない
        var record = Builder.Build(
            new Dictionary<string, ImmutableArray<string>> { ["AttachmentsA"] = ["a.png"] });

        var problem = Assert.Single(record.Problems);
        Assert.Equal("AttachmentsA", problem.ColumnName);
    }

    [Fact]
    public void 値の列と添付列は同じ本体に同居できる()
    {
        var record = Builder.Build(
            new Dictionary<string, ImmutableArray<string>> { ["ClassA"] = ["満足"] },
            attachments: new Dictionary<string, IReadOnlyList<PleasanterAttachment>>
            {
                ["AttachmentsA"] = [new PleasanterAttachment("a.png", "aGVsbG8=")],
            });

        Assert.Empty(record.Problems);
        Assert.True(record.Body.ContainsKey("ClassHash"));
        Assert.True(record.Body.ContainsKey("AttachmentsHash"));
    }
}
