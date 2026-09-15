using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Answers;

public class ResponsePayloadTests
{
    private static readonly byte[] Content = [0x89, 0x50, 0x4E, 0x47];

    private static ResponsePayload WithAttachment() => ResponsePayload.Create(
        "token1",
        [new Answer("q1", []) { FileNames = ["a.png"] }],
        [new AnsweredAttachment("q1", new IncomingAttachment("a.png", Content))]);

    // ---- 行ごとの回答（Issue #74）--------------------------------------------

    private static Answer GridAnswer() => new("q1", [])
    {
        Rows = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["r1"] = ["満足"],
            ["r2"] = ["普通", "どちらでもない"],
        }.ToImmutableDictionary(StringComparer.Ordinal),
    };

    [Fact]
    public void グリッドの回答は正本へ載る()
    {
        // **ここが落ちると回答が丸ごと消える。** グリッドは Values を使わない
        var payload = ResponsePayload.Create("token1", [GridAnswer()]);

        var rows = payload.Answers.Single().Rows;

        Assert.NotNull(rows);
        Assert.Equal(["満足"], rows["r1"].ToArray());
        Assert.Equal(["普通", "どちらでもない"], rows["r2"].ToArray());
    }

    [Fact]
    public void グリッドの回答は往復しても残る()
    {
        var restored = ResponsePayload.FromJson(
            ResponsePayload.Create("token1", [GridAnswer()]).ToJson());

        var answer = Assert.Single(restored!.ToAnswers());

        Assert.Equal(["満足"], answer.Row("r1").ToArray());
        Assert.Equal(["普通", "どちらでもない"], answer.Row("r2").ToArray());
    }

    [Fact]
    public void 行を持たない設問では正本に行を書かない()
    {
        // **空の入れ物を並べない。** 大多数の設問は行を持たない
        var json = ResponsePayload.Create("token1", [Answer.Of("q1", "満足")]).ToJson();

        Assert.DoesNotContain("\"rows\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 行の無い正本を読んでも落ちない()
    {
        // **`Rows` を null のまま入れると、読むたびに落ちる**（未初期化の辞書になる）。
        // 古い正本や、行を持たない設問はこの形で届く
        var restored = ResponsePayload.FromJson(
            """{"token":"t","answers":[{"questionId":"q1","values":["満足"]}]}""");

        var answer = Assert.Single(restored!.ToAnswers());

        Assert.Empty(answer.Row("r1"));
        Assert.False(answer.IsEmpty);
    }

    [Fact]
    public void 添付は送信待ちの中身へBase64で載る()
    {
        var file = WithAttachment().Answers.Single().Files.Single();

        Assert.Equal("a.png", file.Name);
        Assert.Equal(Convert.ToBase64String(Content), file.Base64);
    }

    [Fact]
    public void 送信待ちの中身は往復しても添付を保つ()
    {
        var restored = ResponsePayload.FromJson(WithAttachment().ToJson());

        Assert.Equal(
            Convert.ToBase64String(Content),
            restored!.Answers.Single().Files.Single().Base64);
    }

    [Fact]
    public void 正本にはBase64を載せずファイル名だけ残す()
    {
        // **Pleasanter の回答 JSON 列を添付本文で埋めない**
        var record = WithAttachment().WithoutFileContent();
        var json = record.ToJson();

        Assert.Null(record.Answers.Single().Files.Single().Base64);
        Assert.Equal("a.png", record.Answers.Single().Files.Single().Name);
        Assert.DoesNotContain(Convert.ToBase64String(Content), json, StringComparison.Ordinal);
        Assert.Contains("a.png", json, StringComparison.Ordinal);
        // 応答不明時の照合に使うトークンは残っていること
        Assert.Contains("token1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 添付が無ければ添付の欄は空になる()
    {
        var payload = ResponsePayload.Create("token1", [Answer.Of("q1", "満足")]);

        Assert.Empty(payload.Answers.Single().Files);
    }
}
