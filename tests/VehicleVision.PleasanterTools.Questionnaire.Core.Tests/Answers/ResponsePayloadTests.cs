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
