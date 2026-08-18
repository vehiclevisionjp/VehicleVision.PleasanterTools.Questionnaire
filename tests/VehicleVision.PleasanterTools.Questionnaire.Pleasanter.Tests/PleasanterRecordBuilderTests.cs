using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter.Tests;

public class PleasanterRecordBuilderTests
{
    private static PleasanterRecordBuilder Builder() =>
        new(new PleasanterDateTime("Asia/Tokyo"));

    private static Dictionary<string, ImmutableArray<string>> Columns(
        params (string Column, string[] Values)[] entries) =>
        entries.ToDictionary(
            entry => entry.Column,
            entry => ImmutableArray.Create(entry.Values),
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object?> Hash(PleasanterRecord record, string kind) =>
        (Dictionary<string, object?>)record.Body[$"{kind}Hash"]!;

    [Fact]
    public void 列種別ごとに対応するハッシュへ振り分ける()
    {
        var record = Builder().Build(Columns(
            ("ClassA", ["満足"]),
            ("NumA", ["5"]),
            ("CheckA", ["true"]),
            ("DescriptionA", ["自由記述"])));

        Assert.Equal("満足", Hash(record, "Class")["ClassA"]);
        Assert.Equal(5m, Hash(record, "Num")["NumA"]);
        Assert.Equal(true, Hash(record, "Check")["CheckA"]);
        Assert.Equal("自由記述", Hash(record, "Description")["DescriptionA"]);
        Assert.Empty(record.Problems);
    }

    [Fact]
    public void 連番の拡張列も振り分けられる()
    {
        var record = Builder().Build(Columns(("Class001", ["A"])));

        Assert.Equal("A", Hash(record, "Class")["Class001"]);
    }

    [Fact]
    public void 空の値は消すための値になる()
    {
        // **空配列は「消す」を意味する**（編集で回答を消したとき）
        var record = Builder().Build(Columns(
            ("ClassA", []),
            ("NumA", []),
            ("CheckA", []),
            ("DateA", [])));

        Assert.Equal(string.Empty, Hash(record, "Class")["ClassA"]);
        Assert.Null(Hash(record, "Num")["NumA"]);
        Assert.Equal(false, Hash(record, "Check")["CheckA"]);
        Assert.Null(Hash(record, "Date")["DateA"]);
    }

    [Fact]
    public void 値が複数あれば黙って先頭を採らずに不備とする()
    {
        // 連結が要るならマッピング側で変換を挟むべき
        var record = Builder().Build(Columns(("ClassA", ["A", "B"])));

        Assert.False(record.Body.ContainsKey("ClassHash"));
        Assert.Single(record.Problems);
    }

    [Fact]
    public void Class列の桁溢れは切らずに拒否する()
    {
        // **黙って切ると、回答の一部が失われたことに誰も気づけない**
        var tooLong = new string('あ', PleasanterColumn.ClassMaxLength + 1);

        var record = Builder().Build(Columns(("ClassA", [tooLong])));

        Assert.False(record.Body.ContainsKey("ClassHash"));
        Assert.Contains(record.Problems, problem => problem.ColumnName == "ClassA");
    }

    [Fact]
    public void Class列の上限ちょうどは通す()
    {
        var justFit = new string('あ', PleasanterColumn.ClassMaxLength);

        var record = Builder().Build(Columns(("ClassA", [justFit])));

        Assert.Equal(justFit, Hash(record, "Class")["ClassA"]);
        Assert.Empty(record.Problems);
    }

    [Fact]
    public void 数値として読めない値は不備とする()
    {
        var record = Builder().Build(Columns(("NumA", ["とても満足"])));

        Assert.False(record.Body.ContainsKey("NumHash"));
        Assert.Single(record.Problems);
    }

    [Fact]
    public void 日付は日時の形へ整えて渡す()
    {
        var record = Builder().Build(Columns(("DateA", ["2026-03-01"])));

        Assert.Equal("2026-03-01T00:00:00", Hash(record, "Date")["DateA"]);
    }

    [Fact]
    public void 日時として読めない値は不備とする()
    {
        var record = Builder().Build(Columns(("DateA", ["来週"])));

        Assert.False(record.Body.ContainsKey("DateHash"));
        Assert.Single(record.Problems);
    }

    [Fact]
    public void 列名として解釈できないものは不備とする()
    {
        var record = Builder().Build(Columns(("勝手な列", ["A"])));

        Assert.Single(record.Problems);
    }

    [Fact]
    public void 添付列はマッピングの出力先にできない()
    {
        // 添付は Base64 を伴うので、別経路で組み立てる
        var record = Builder().Build(Columns(("AttachmentsA", ["a.png"])));

        Assert.Single(record.Problems);
    }

    [Fact]
    public void 回答JSONは予約列として別枠で入る()
    {
        var record = Builder().Build(
            Columns(("ClassA", ["満足"])),
            responseJsonColumn: "DescriptionZ",
            responseJson: "{\"token\":\"tok-1\"}");

        Assert.Equal("{\"token\":\"tok-1\"}", Hash(record, "Description")["DescriptionZ"]);
    }

    [Fact]
    public void 回答JSONの保存先がDescription系でなければ不備とする()
    {
        var record = Builder().Build(
            Columns(),
            responseJsonColumn: "ClassZ",
            responseJson: "{}");

        Assert.Single(record.Problems);
    }
}
