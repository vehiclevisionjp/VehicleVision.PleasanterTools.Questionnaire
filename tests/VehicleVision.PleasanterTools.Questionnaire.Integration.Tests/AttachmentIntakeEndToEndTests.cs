using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>添付を受け取る入口を、動いているアプリへ HTTP で当てて確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 起動は <c>docker compose --profile sqlserver up -d --wait</c>。
/// </para>
/// <para>
/// **部品の試験では見えない所を見る。** multipart の解釈、欄の名前から設問への割り当て、
/// 拒否したときの応答は、実際にミドルウェアを通してみないと分からない。
/// </para>
/// <para>
/// **ウイルススキャンは既定の無効のまま試す。** clamd を要求すると、
/// 検証環境の都合でこの試験が落ちる。スキャナ側の振る舞いは単体試験で見ている。
/// </para>
/// </remarks>
public class AttachmentIntakeEndToEndTests
{
    /// <summary>アプリが繋いでいる DB。**用意と片付けのために直接触る。**</summary>
    private const string ConnectionString =
        "Server=localhost,11433;Database=Questionnaire;UID=sa;PWD=Questionnaire#Test1;TrustServerCertificate=True";

    /// <summary>PNG の先頭バイト。**拡張子と中身の一致を見る検査を通すため。**</summary>
    private static readonly byte[] PngHeader =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_BASE_URL") ?? "http://localhost:8081";

    private static SurveyDefinition Definition() => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("添付の検証"),
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Questions =
                [
                    new Question
                    {
                        QuestionId = "q1",
                        Type = QuestionType.Text,
                        Title = LocalizedText.Japanese("ご意見"),
                    },
                    new Question
                    {
                        QuestionId = "qf",
                        Type = QuestionType.File,
                        Title = LocalizedText.Japanese("資料"),
                    },
                ],
            },
        ],
    };

    private static MultipartFormDataContent Multipart(string fileName, byte[] content)
    {
        var form = new MultipartFormDataContent
        {
            {
                new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        answers = new[] { new { questionId = "q1", values = new[] { "満足" } } },
                    }),
                    Encoding.UTF8,
                    "application/json"),
                "answers"
            },
        };

        // **欄の名前が設問 ID を表す**
        form.Add(new ByteArrayContent(content), "qf", fileName);
        return form;
    }

    private static string NewToken() => Convert.ToHexStringLower(
        Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray().Take(8)).ToArray());

    [Fact]
    public async Task 添付付きの回答を受け付けて送信待ちへ入れる()
    {
        if (!Enabled)
        {
            return;
        }

        var factory = new DbConnectionFactory(DatabaseProvider.SqlServer, ConnectionString);
        var surveys = new SurveyRepository(factory);

        var surveyId = Guid.NewGuid();
        var publicId = $"pub-{Guid.NewGuid():N}";
        await surveys.SaveAsync(new SurveyRecord(
            surveyId, publicId, "添付の検証", 1, "DescriptionA", (int)SurveyStatus.Published, null));
        await surveys.PublishAsync(surveyId, 1, Definition(), new MappingDefinition(), null);

        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var token = NewToken();

        try
        {
            using (var accepted = await http.PutAsync(
                $"/api/forms/{publicId}/responses/{token}", Multipart("a.png", PngHeader)))
            {
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            }

            // **送信待ちに Base64 で載っていること**
            await using var connection = factory.Create();
            await connection.OpenAsync();
            var payload = await connection.QuerySingleAsync<string>(
                "SELECT [PayloadJson] FROM [Responses] WHERE [ResponseToken] = @Token",
                new { Token = token });

            Assert.Contains(Convert.ToBase64String(PngHeader), payload, StringComparison.Ordinal);
            Assert.Contains("a.png", payload, StringComparison.Ordinal);

            // 中身が拡張子と食い違う添付は受け付けない
            var rejectedToken = NewToken();
            using var rejected = await http.PutAsync(
                $"/api/forms/{publicId}/responses/{rejectedToken}",
                Multipart("a.png", [0x4D, 0x5A, 0x00]));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.Contains(
                "contentDoesNotMatchExtension",
                await rejected.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            // **未検査のバイナリを DB に載せない**
            var stored = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM [Responses] WHERE [ResponseToken] = @Token",
                new { Token = rejectedToken });
            Assert.Equal(0, stored);
        }
        finally
        {
            // **自分で作った行だけ消す。** DB は他の作業と共有している
            await using var connection = factory.Create();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "DELETE FROM [Responses] WHERE [ResponseToken] = @Token", new { Token = token });
            await connection.ExecuteAsync(
                "DELETE FROM [ResponseTokens] WHERE [ResponseToken] = @Token", new { Token = token });
            await connection.ExecuteAsync(
                "DELETE FROM [SurveyVersions] WHERE [SurveyId] = @SurveyId", new { SurveyId = surveyId });
            await connection.ExecuteAsync(
                "DELETE FROM [Surveys] WHERE [SurveyId] = @SurveyId", new { SurveyId = surveyId });
        }
    }
}
