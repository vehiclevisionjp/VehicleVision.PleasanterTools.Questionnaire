using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>テーマとヘッダ画像の読み書きを 3 RDBMS で確かめる（Issue #56）。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// **画像は Base64 の長い文字列で持つ**（<c>M0008_SurveyTheme</c>）。
/// 3 者の binary 型は実体も既定の扱いも違うので、
/// **入れた通りのバイト列が返るかを実機で見る**のがここの主題。
/// </para>
/// </remarks>
public class SurveyThemeStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static (ISurveyDraftStore Drafts, ISurveyRepository Surveys, ISurveyAssetStore Assets)
        Create(DatabaseProvider provider, string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        return (new SurveyDraftStore(factory), new SurveyRepository(factory), new SurveyAssetStore(factory));
    }

    private static async Task<Guid> CreateSurveyAsync(ISurveyRepository surveys, long siteId = 1)
    {
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(new SurveyRecord(
            surveyId,
            $"pub-{Guid.NewGuid():N}",
            "検証用",
            PleasanterSiteId: siteId,
            ResponseJsonColumn: null,
            Status: (int)SurveyStatus.Draft,
            PublishedVersion: null));
        return surveyId;
    }

    private static SurveyDefinition Definition(Guid surveyId, SurveyTheme? theme) => new()
    {
        SurveyId = surveyId.ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("満足度調査"),
        Theme = theme,
        Pages = [new Page { PageId = "page-1" }],
    };

    /// <summary>PNG の先頭バイト。**画像として通る最小の中身。**</summary>
    private static byte[] Png() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0xFF, 0x7F];

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task テーマを保存して読み直せる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys, _) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var theme = new SurveyTheme
        {
            AccentColor = "#175cd3",
            BackgroundColor = "#ffffff",
            TextColor = "#101828",
            Font = ThemeFont.Serif,
        };

        await drafts.SaveAsync(
            surveyId, Definition(surveyId, theme), new MappingDefinition(), expectedRevision: 0);

        var draft = await drafts.LoadAsync(surveyId);

        Assert.NotNull(draft);
        Assert.Equal(theme, draft.Definition.Theme);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task テーマを指定しなければ既定のまま(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys, _) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        await drafts.SaveAsync(
            surveyId, Definition(surveyId, theme: null), new MappingDefinition(), expectedRevision: 0);

        var draft = await drafts.LoadAsync(surveyId);

        // **空のテーマではなく「無い」。** 空を返すと定義の JSON が版ごとに変わる
        Assert.NotNull(draft);
        Assert.Null(draft.Definition.Theme);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 形の違う色は保存の時点で捨てる(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys, _) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        // **入口でも弾いているが、DB にも入れない**（二重の関所）
        var theme = new SurveyTheme
        {
            AccentColor = "#175cd3",
            BackgroundColor = "#fff; } body { display: none }",
        };

        await drafts.SaveAsync(
            surveyId, Definition(surveyId, theme), new MappingDefinition(), expectedRevision: 0);

        var draft = await drafts.LoadAsync(surveyId);

        Assert.NotNull(draft);
        Assert.Equal("#175cd3", draft.Definition.Theme?.AccentColor);
        Assert.Null(draft.Definition.Theme?.BackgroundColor);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 画像は入れたバイト列がそのまま返る(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (_, surveys, assets) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);
        var content = Png();

        var assetId = await assets.AddAsync(surveyId, "image/png", "banner.png", content);
        var asset = await assets.FindAsync(surveyId, assetId);

        Assert.NotNull(asset);
        Assert.Equal("image/png", asset.ContentType);
        Assert.Equal(content, asset.Content);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 別のアンケートの画像は読めない(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (_, surveys, assets) = Create(provider, connectionString);
        var owner = await CreateSurveyAsync(surveys);
        var other = await CreateSurveyAsync(surveys, siteId: 2);

        var assetId = await assets.AddAsync(owner, "image/png", "banner.png", Png());

        // **識別子だけで引けると、下書きのままのアンケートの画像まで取り出せる**
        Assert.Null(await assets.FindAsync(other, assetId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 複製はヘッダ画像も複製先へ写す(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys, assets) = Create(provider, connectionString);
        var sourceId = await CreateSurveyAsync(surveys);
        var content = Png();
        var assetId = await assets.AddAsync(sourceId, "image/png", "banner.png", content);

        var theme = new SurveyTheme
        {
            AccentColor = "#175cd3",
            HeaderImageId = assetId.ToString(),
        };

        await drafts.SaveAsync(
            sourceId, Definition(sourceId, theme), new MappingDefinition(), expectedRevision: 0);

        var target = new SurveyDuplicationTarget(
            Guid.NewGuid(), $"pub-{Guid.NewGuid():N}", PleasanterSiteId: 99, ResponseJsonColumn: null);

        Assert.True(await drafts.DuplicateAsync(sourceId, target));

        var copied = await drafts.LoadAsync(target.SurveyId);

        Assert.NotNull(copied);
        Assert.Equal("#175cd3", copied.Definition.Theme?.AccentColor);

        // **識別子は写さず、画像そのものを写す。**
        // 写さないと、読み出しがアンケートで絞られているので複製先から見えない
        var copiedAssetId = copied.Definition.Theme?.HeaderImage();
        Assert.NotNull(copiedAssetId);
        Assert.NotEqual(assetId, copiedAssetId);

        var copiedAsset = await assets.FindAsync(target.SurveyId, copiedAssetId.Value);
        Assert.NotNull(copiedAsset);
        Assert.Equal(content, copiedAsset.Content);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 公開した版はテーマごと凍る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        var surveys = new SurveyRepository(factory);
        var snapshots = new SurveySnapshotStore(factory);

        var surveyId = await CreateSurveyAsync(surveys);
        var published = new SurveyTheme { AccentColor = "#175cd3" };

        await surveys.PublishAsync(
            surveyId, version: 1, Definition(surveyId, published), new MappingDefinition(), null);

        var snapshot = await snapshots.FindAsync(surveyId, version: 1);

        Assert.NotNull(snapshot);
        Assert.Equal("#175cd3", snapshot.Definition.Theme?.AccentColor);
    }
}
