using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>ルックアップの公開 API の部品（Issue #832）。トークン・枠・同時実行数・応答の再射影。</summary>
public sealed class LookupPublicApiUnitTests
{
    private static readonly string Key = SecretProtector.GenerateKey();
    private static readonly DateTimeOffset Origin = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static (LookupSessionTokens Tokens, FakeTimeProvider Time) Tokens()
    {
        var time = new FakeTimeProvider(Origin);
        return (new LookupSessionTokens(Key, new LookupPublicOptions(), time), time);
    }

    [Fact]
    public void 発行したトークンは同じフォームで通り_回答者の識別子が入る()
    {
        var (tokens, _) = Tokens();

        var check = tokens.Verify(tokens.Issue("form-a"), "form-a");

        Assert.True(check.IsValid);
        Assert.False(string.IsNullOrEmpty(check.SessionId));
    }

    [Fact]
    public void 別のフォーム_改竄_欠落_別の用途の署名は通らない()
    {
        var (tokens, time) = Tokens();
        var token = tokens.Issue("form-a");
        var guard = new SubmissionGuard(Key, new SubmissionGuardOptions(), time);

        Assert.False(tokens.Verify(token, "form-b").IsValid);
        Assert.False(tokens.Verify(token[..^2] + "xx", "form-a").IsValid);
        Assert.False(tokens.Verify(null, "form-a").IsValid);
        Assert.False(tokens.Verify(string.Empty, "form-a").IsValid);
        Assert.False(tokens.Verify(new string('a', 300), "form-a").IsValid);

        // 送信チケット・資産アクセス許可では検索できない（目的を分ける）
        Assert.False(tokens.Verify(guard.Issue("form-a", "r"), "form-a").IsValid);
        Assert.False(tokens.Verify(guard.IssueAssetAccess("form-a", "r"), "form-a").IsValid);
    }

    [Fact]
    public void 三十分を過ぎたトークンは通らない()
    {
        var (tokens, time) = Tokens();
        var token = tokens.Issue("form-a");

        time.Advance(TimeSpan.FromMinutes(30));
        Assert.True(tokens.Verify(token, "form-a").IsValid);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tokens.Verify(token, "form-a").IsValid);
    }

    [Fact]
    public void 未来の発行時刻は通らず_発行ごとに識別子が違う()
    {
        var (tokens, _) = Tokens();
        var token = tokens.Issue("form-a");
        var earlier = new LookupSessionTokens(
            Key, new LookupPublicOptions(), new FakeTimeProvider(Origin.AddMinutes(-5)));

        Assert.False(earlier.Verify(token, "form-a").IsValid);
        Assert.NotEqual(
            tokens.Verify(tokens.Issue("form-a"), "form-a").SessionId,
            tokens.Verify(tokens.Issue("form-a"), "form-a").SessionId);
    }

    [Fact]
    public async Task 枠を超えたら拒否し_数え先ごとに独立する()
    {
        using var gate = new LookupRateGate(null, NullLogger<LookupRateGate>.Instance);
        var window = TimeSpan.FromMinutes(5);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(LookupRateOutcome.Allowed, (await gate.AcquireAsync("ip", "1.2.3.4", 3, window)).Outcome);
        }

        var limited = await gate.AcquireAsync("ip", "1.2.3.4", 3, window);
        Assert.Equal(LookupRateOutcome.Limited, limited.Outcome);
        Assert.True(limited.RetryAfter > TimeSpan.Zero);
        Assert.Equal(LookupRateOutcome.Allowed, (await gate.AcquireAsync("ip", "5.6.7.8", 3, window)).Outcome);
        Assert.Equal(LookupRateOutcome.Allowed, (await gate.AcquireAsync("form", "1.2.3.4", 3, window)).Outcome);
    }

    private sealed class FakeStore(Func<SharedRateLimitResult> behave) : ISharedRateLimitStore
    {
        public List<string> Partitions { get; } = [];

        public ValueTask<SharedRateLimitResult> AcquireAsync(
            string partition,
            int permitCount,
            int permitLimit,
            TimeSpan window,
            CancellationToken cancellationToken = default)
        {
            Partitions.Add(partition);
            return ValueTask.FromResult(behave());
        }
    }

    [Fact]
    public async Task 共有ストアの結果に従い_障害時はプロセス内へ退避せず拒否する()
    {
        var logger = NullLogger<LookupRateGate>.Instance;
        var window = TimeSpan.FromMinutes(1);

        var ok = new FakeStore(() => new SharedRateLimitResult(true, TimeSpan.Zero));
        using var allowGate = new LookupRateGate(ok, logger);
        Assert.Equal(LookupRateOutcome.Allowed, (await allowGate.AcquireAsync("form", "p", 1, window)).Outcome);
        Assert.Equal("lookup:form:p", Assert.Single(ok.Partitions));

        var full = new FakeStore(() => new SharedRateLimitResult(false, TimeSpan.FromSeconds(7)));
        using var limitGate = new LookupRateGate(full, logger);
        var limited = await limitGate.AcquireAsync("form", "p", 1, window);
        Assert.Equal(LookupRateOutcome.Limited, limited.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(7), limited.RetryAfter);

        var broken = new FakeStore(() => throw new InvalidOperationException("redis down"));
        using var downGate = new LookupRateGate(broken, logger);
        for (var i = 0; i < 5; i++)
        {
            // 上限が 100 でも、障害のあいだは毎回拒否する（プロセス内の枠で通さない）
            Assert.Equal(
                LookupRateOutcome.Unavailable,
                (await downGate.AcquireAsync("form", "p", 100, window)).Outcome);
        }
    }

    [Fact]
    public void 同時実行数はフォームごとに数え_解放で戻る()
    {
        var gate = new LookupConcurrencyGate();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var leases = Enumerable.Range(0, 4).Select(_ => gate.TryEnter(a, 4)).ToList();
        Assert.All(leases, Assert.NotNull);
        Assert.Null(gate.TryEnter(a, 4));
        Assert.NotNull(gate.TryEnter(b, 4));

        leases[0]!.Dispose();
        leases[0]!.Dispose();
        Assert.Equal(3, gate.RunningOf(a));
        Assert.NotNull(gate.TryEnter(a, 4));
    }

    private static (SurveyDefinition Survey, LookupDefinition Lookup) Definitions()
    {
        var lookup = new LookupDefinition
        {
            LookupId = "l1",
            AnchorQuestionId = "a",
            SourceSiteId = 5,
            CredentialId = "c",
            Keys = [new LookupKey { QuestionId = "k", SourceColumn = "ClassA" }],
            Targets =
            [
                new LookupTarget { QuestionId = "t1", SourceColumn = "ClassB" },
                new LookupTarget { QuestionId = "t2", SourceColumn = "NumA" },
                new LookupTarget { QuestionId = "t3", SourceColumn = "ClassC" },
                new LookupTarget { QuestionId = "t4", SourceColumn = "ClassD" },
            ],
            AllowedColumns = ["ClassB", "NumA", "ClassC", "ClassD"],
            DisplayFields =
            [
                new LookupDisplayField { DisplayId = "city", SourceColumn = "ClassB" },
                new LookupDisplayField { DisplayId = "hidden", SourceColumn = "Hidden" },
            ],
        };

        static Question Q(string id, QuestionType type) => new()
        {
            QuestionId = id,
            Type = type,
            Title = LocalizedText.Japanese(id),
        };

        var survey = new SurveyDefinition
        {
            SurveyId = Guid.NewGuid().ToString(),
            Version = 3,
            Title = LocalizedText.Japanese("s"),
            Pages =
            [
                new Page
                {
                    PageId = "p",
                    Questions =
                    [
                        Q("a", QuestionType.Text),
                        Q("k", QuestionType.Text),
                        Q("t1", QuestionType.Text),
                        Q("t2", QuestionType.Text) with { Settings = new QuestionSettings { MaxLength = 2 } },
                        Q("t3", QuestionType.Text),
                    ],
                },
            ],
            Lookups = [lookup],
        };
        return (survey, lookup);
    }

    [Fact]
    public void 応答は許可列だけに再射影し_不適合と空は値へ入れない()
    {
        var (survey, lookup) = Definitions();
        var result = new LookupSearchResult
        {
            Status = LookupSearchStatus.Success,
            Candidates =
            [
                new LookupCandidateRow(new Dictionary<string, string?>
                {
                    ["ClassB"] = "東京",
                    ["NumA"] = "abc",
                    ["ClassC"] = "  ",
                    ["ClassD"] = "x",
                    ["Title"] = "内部の題名",
                    ["ClassA"] = "キー値",
                    ["Hidden"] = "許可外",
                }),
            ],
        };

        var response = LookupResponseBuilder.Build(3, survey, lookup, result);

        Assert.Equal("single", response.Outcome);
        var candidate = Assert.Single(response.Candidates);
        Assert.Equal("1", candidate.CandidateKey);
        var display = Assert.Single(candidate.Display);
        // 鍵は表示 ID。参照元の列名ではない（Issue #835）
        Assert.Equal("city", display.Key);
        Assert.Equal("東京", display.Value);
        Assert.Equal("東京", candidate.Values["t1"]);
        Assert.False(candidate.Values.ContainsKey("t2"));
        Assert.False(candidate.Values.ContainsKey("t3"));

        // 設問が定義に無い入力先は不適合として扱う
        Assert.False(candidate.Values.ContainsKey("t4"));
        Assert.Equal(
            ["t2", "t4"],
            response.Warnings.Select(warning => warning.FieldId).Order().ToArray());
        Assert.All(response.Warnings, warning => Assert.Equal("ValueIncompatible", warning.Code));
        var serialized = System.Text.Json.JsonSerializer.Serialize(response);
        foreach (var leaked in new[] { "内部の題名", "キー値", "許可外", "abc" })
        {
            Assert.DoesNotContain(leaked, serialized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 件数の区分は_notFound_single_multiple()
    {
        var (survey, lookup) = Definitions();
        static LookupCandidateRow Row() => new(new Dictionary<string, string?> { ["ClassB"] = "a" });

        Assert.Equal(
            "notFound",
            LookupResponseBuilder.Build(
                3, survey, lookup, new LookupSearchResult { Status = LookupSearchStatus.Success }).Outcome);
        var multiple = LookupResponseBuilder.Build(
            3,
            survey,
            lookup,
            new LookupSearchResult { Status = LookupSearchStatus.Success, Candidates = [Row(), Row()] });
        Assert.Equal("multiple", multiple.Outcome);
        Assert.Equal(["1", "2"], multiple.Candidates.Select(candidate => candidate.CandidateKey).ToArray());
    }
}
