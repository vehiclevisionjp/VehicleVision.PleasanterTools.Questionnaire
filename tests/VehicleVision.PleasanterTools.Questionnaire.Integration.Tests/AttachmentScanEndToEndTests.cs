using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>ウイルススキャンが実際に働くことを、動いているアプリへ HTTP で当てて確かめる。</summary>
/// <remarks>
/// <para>
/// **clamd が要るので、別の環境変数で明示したときだけ実行する。**
/// </para>
/// <code>
/// DEV_VIRUSSCAN_ENABLED=true docker compose --profile sqlserver --profile clamav up -d --wait
/// QUESTIONNAIRE_INTEGRATION=1 QUESTIONNAIRE_VIRUSSCAN=1 dotnet test
/// </code>
/// <para>
/// **偽の検体は使わない。** スキャナを差し替えた単体試験では
/// 「本当に clamd へ届いているか」「本当に弾けるか」が分からない。
/// EICAR は、まさにこの確認のために公開されている**無害な検査用ファイル**で、
/// マルウェアではない。
/// </para>
/// </remarks>
public class AttachmentScanEndToEndTests
{
    private const string ConnectionString =
        "Server=localhost,11433;Database=Questionnaire;UID=sa;PWD=Questionnaire#Test1;TrustServerCertificate=True";

    /// <summary>
    /// EICAR の検査用文字列。**無害**で、どのウイルス対策製品も検出するよう申し合わせがある。
    /// </summary>
    private const string EicarText =
        "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1"
        && Environment.GetEnvironmentVariable("QUESTIONNAIRE_VIRUSSCAN") == "1";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_BASE_URL") ?? "http://localhost:8081";

    private static SurveyDefinition Definition() => new()
    {
        SurveyId = "scan",
        Version = 1,
        Title = LocalizedText.Japanese("スキャンの検証"),
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Questions =
                [
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

    private static MultipartFormDataContent Multipart(string fileName, string content)
    {
        var form = new MultipartFormDataContent
        {
            {
                new StringContent(
                    JsonSerializer.Serialize(new { answers = Array.Empty<object>() }),
                    Encoding.UTF8,
                    "application/json"),
                "answers"
            },
        };

        form.Add(new ByteArrayContent(Encoding.ASCII.GetBytes(content)), "qf", fileName);
        return form;
    }

    private static string NewToken() => Convert.ToHexStringLower(
        Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray().Take(8)).ToArray());

    [Fact]
    public async Task 検体は弾かれ無害なファイルは通る()
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
            surveyId, publicId, "スキャンの検証", 1, null, (int)SurveyStatus.Published, null));
        await surveys.PublishAsync(surveyId, 1, Definition(), new MappingDefinition(), null);

        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var cleanToken = NewToken();
        var infectedToken = NewToken();

        try
        {
            // **同じ形式・同じ拡張子で、中身だけが違う。**
            // 拡張子や先頭バイトの検査ではなく、スキャナが効いていることを見たい
            using (var clean = await http.PutAsync(
                $"/api/forms/{publicId}/responses/{cleanToken}",
                Multipart("readme.txt", "これは無害なファイルです。")))
            {
                Assert.Equal(HttpStatusCode.Accepted, clean.StatusCode);
            }

            using (var infected = await http.PutAsync(
                $"/api/forms/{publicId}/responses/{infectedToken}",
                Multipart("readme.txt", EicarText)))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, infected.StatusCode);

                // **何を検出したかは伝えない。** 手掛かりを与えない
                var body = await infected.Content.ReadAsStringAsync();
                Assert.DoesNotContain("EICAR", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Eicar-Signature", body, StringComparison.OrdinalIgnoreCase);
            }

            await using var connection = factory.Create();
            await connection.OpenAsync();

            // **弾いた添付を DB に残さない**
            Assert.Equal(
                0,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM [Responses] WHERE [ResponseToken] = @Token",
                    new { Token = infectedToken }));

            Assert.Equal(
                1,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM [Responses] WHERE [ResponseToken] = @Token",
                    new { Token = cleanToken }));
        }
        finally
        {
            // **自分で作った行だけ消す。** DB は他の作業と共有している
            await using var connection = factory.Create();
            await connection.OpenAsync();
            foreach (var token in new[] { cleanToken, infectedToken })
            {
                await connection.ExecuteAsync(
                    "DELETE FROM [Responses] WHERE [ResponseToken] = @Token", new { Token = token });
                await connection.ExecuteAsync(
                    "DELETE FROM [ResponseTokens] WHERE [ResponseToken] = @Token", new { Token = token });
            }

            await connection.ExecuteAsync(
                "DELETE FROM [SurveyVersions] WHERE [SurveyId] = @SurveyId", new { SurveyId = surveyId });
            await connection.ExecuteAsync(
                "DELETE FROM [Surveys] WHERE [SurveyId] = @SurveyId", new { SurveyId = surveyId });
        }
    }
}
