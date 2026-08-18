using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Worker;

// **設定に書く鍵を作るための道。** 手で乱数を用意させると、短い値や使い回しが混ざる
if (args.Contains("--generate-secret-key"))
{
    Console.WriteLine(SecretProtector.GenerateKey());
    return;
}

var builder = WebApplication.CreateBuilder(args);

// ---- 設定 ------------------------------------------------------------------
// **資格情報の実値は設定ファイルへ書かない。** 環境変数か Key Vault から読む
// （App_Data/Parameters/README.md）
var provider = Enum.Parse<DatabaseProvider>(
    builder.Configuration["QUESTIONNAIRE_DB_PROVIDER"] ?? nameof(DatabaseProvider.SqlServer));
var connectionString = builder.Configuration["QUESTIONNAIRE_DB_CONNECTIONSTRING"]
    ?? throw new InvalidOperationException("QUESTIONNAIRE_DB_CONNECTIONSTRING が設定されていない");

var pleasanterOptions = new PleasanterOptions
{
    BaseUrl = builder.Configuration["QUESTIONNAIRE_PLEASANTER_BASEURL"]
        ?? throw new InvalidOperationException("QUESTIONNAIRE_PLEASANTER_BASEURL が設定されていない"),
    ApiKey = builder.Configuration["QUESTIONNAIRE_PLEASANTER_APIKEY"]
        ?? throw new InvalidOperationException("QUESTIONNAIRE_PLEASANTER_APIKEY が設定されていない"),
    ApiKeyUserTimeZoneId =
        builder.Configuration["QUESTIONNAIRE_PLEASANTER_TIMEZONE"] ?? "Asia/Tokyo",
};

// ---- サービス --------------------------------------------------------------
builder.Services.AddSingleton<IDbConnectionFactory>(
    new DbConnectionFactory(provider, connectionString));
builder.Services.AddSingleton<IResponseOutbox, ResponseOutbox>();
builder.Services.AddSingleton<IResponseTokenStore, ResponseTokenStore>();
builder.Services.AddSingleton<ISurveySnapshotStore, SurveySnapshotStore>();
builder.Services.AddSingleton<ISurveyRepository, SurveyRepository>();
builder.Services.AddSingleton<ISurveyDraftStore, SurveyDraftStore>();

builder.Services.AddSingleton(pleasanterOptions);
builder.Services.AddSingleton(new PleasanterDateTime(pleasanterOptions.ApiKeyUserTimeZoneId));
builder.Services.AddSingleton<PleasanterRecordBuilder>();
builder.Services.AddSingleton(_ => new MappingEvaluator());
builder.Services.AddHttpClient<PleasanterApiClient>(client =>
    client.Timeout = pleasanterOptions.Timeout);

builder.Services.AddSingleton<ResponseIntake>();

// **HTTP でやり取りする JSON も定義と同じ設定にする。**
// 既定のままだと LocalizedText が言語コードのオブジェクトにならず、
// **画面に文言が出ないし、管理画面から送られた定義も読めない**
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = SurveyJson.Options.Encoder;
    options.SerializerOptions.DefaultIgnoreCondition = SurveyJson.Options.DefaultIgnoreCondition;
    foreach (var converter in SurveyJson.Options.Converters)
    {
        options.SerializerOptions.Converters.Add(converter);
    }
});
// ---- 添付ファイル ----------------------------------------------------------
// **3 層で受ける**（_documents/非機能設計.md 1 章）。
// 1. 拡張子の許可リスト 2. 先頭バイトとの一致 は常に有効。3. ウイルススキャンは既定で無効
var attachmentOptions = AttachmentOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(attachmentOptions);

// **添付は multipart で届く。上限を既定値に任せない**（_documents/非機能設計.md 1 章）
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = attachmentOptions.MaxRequestBodyBytes);

// **ウイルススキャンは設定で有効にしたときだけ組み込む**（既定は無効）。
// **ClamAV 本体は GPL-2.0 なので別プロセスとして呼ぶだけ**（LICENSING.md）
if (attachmentOptions.VirusScan.Enabled)
{
    switch (attachmentOptions.VirusScan.Provider)
    {
        case VirusScanProvider.ClamAv:
            builder.Services.AddSingleton<IVirusScanner>(serviceProvider =>
                new ClamAvVirusScanner(
                    attachmentOptions.VirusScan,
                    serviceProvider.GetRequiredService<ILogger<ClamAvVirusScanner>>()));
            break;

        case VirusScanProvider.DefenderForStorage:
            // **判定は非同期で届く。** Event Grid の受け口が要る
            builder.Services.AddSingleton<MalwareScanVerdicts>();
            builder.Services.AddSingleton<IDmzBlobStore>(
                new AzureDmzBlobStore(attachmentOptions.VirusScan));
            builder.Services.AddSingleton<IVirusScanner>(serviceProvider =>
                new DefenderForStorageVirusScanner(
                    attachmentOptions.VirusScan,
                    serviceProvider.GetRequiredService<IDmzBlobStore>(),
                    serviceProvider.GetRequiredService<MalwareScanVerdicts>(),
                    serviceProvider
                        .GetRequiredService<ILogger<DefenderForStorageVirusScanner>>()));
            break;

        default:
            throw new InvalidOperationException(
                $"未対応のウイルススキャン方式: {attachmentOptions.VirusScan.Provider}");
    }
}

// **スキャナが登録されていなければ 3 層目は無効。**
// 「有効なのにスキャナが無い」場合は検査側が添付を拒否する（素通しにしない）
builder.Services.AddSingleton(serviceProvider => new AttachmentInspector(
    attachmentOptions.ToPolicy(), serviceProvider.GetService<IVirusScanner>()));

// ---- 管理者の認証 ----------------------------------------------------------
// **共有鍵を復号するための鍵。** 失うと登録済みの 2 要素が全て使えなくなるので、
// **App Service の設定か Key Vault に置き、控えを取っておくこと**
var secretKey = builder.Configuration["QUESTIONNAIRE_SECRET_KEY"]
    ?? throw new InvalidOperationException(
        "QUESTIONNAIRE_SECRET_KEY が設定されていない（Base64 の 32 バイト）");

builder.Services.AddSingleton<IAdminUserStore, AdminUserStore>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton(new SecretProtector(secretKey));
builder.Services.AddSingleton(new AdminAuthOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AdminAuthenticator>();

// **既定は厳しく。** 緩めるのは検証環境だけにすること。
// 端から端まで通す試験は 1 つの IP から大量に叩くので、既定のままだと自分で枠を使い切る
var loginPermitLimit = int.TryParse(
    builder.Configuration["QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN"], out var configuredLogin)
    ? configuredLogin
    : 10;

var requestPermitLimit = int.TryParse(
    builder.Configuration["QUESTIONNAIRE_REQUESTS_PER_MIN"], out var configuredRequests)
    ? configuredRequests
    : 60;

builder.Services
    .AddAuthentication(AdminAuthSchemes.Session)
    .AddCookie(AdminAuthSchemes.Session, options => AdminAuthSchemes.Configure(
        options, "q.admin", AdminAuthSchemes.SessionLifetime))
    .AddCookie(AdminAuthSchemes.Pending, options => AdminAuthSchemes.Configure(
        options, "q.admin.pending", AdminAuthSchemes.PendingLifetime));

builder.Services.AddAuthorization();

// **送信ワーカーは .Web に同居させる**（_documents/アプリケーション設計.md 8 章）。
// Azure App Service では別プロセス常駐の手段が限られるため。**Always On を有効にすること**
builder.Services.AddSingleton(new ResponseSenderOptions());
builder.Services.AddSingleton<ResponseSender>();
builder.Services.AddHostedService<ResponseSenderHostedService>();

// ---- レート制限 ------------------------------------------------------------
// **DB へ書く前に効かせる。** 書いてから弾いても消費は起きている
// （_documents/非機能設計.md 1 章）
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // **複数の軸で掛ける。** 1 つの軸だけでは抜けられる
    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = requestPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                })),
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Request.RouteValues["publicId"]?.ToString() ?? "none",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 600,
                    Window = TimeSpan.FromMinutes(1),
                })));

    // **ログインの試行だけは別枠で厳しくする。**
    // 全体の枠に紛れさせると、1 分に 60 回の総当たりが通ってしまう。
    //
    // **回数を設定で変えられるようにしてある。** 検証環境では端から端まで通す試験が
    // 既定の枠を使い切ってしまうため。**本番では既定のまま使うこと**
    options.AddPolicy(AdminAuthSchemes.LoginRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = loginPermitLimit,
                Window = TimeSpan.FromMinutes(5),
            }));
});

var app = builder.Build();

// **リバースプロキシ配下でも本当の送信元 IP を見る。** レート制限が効かなくなるため
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// **セキュリティヘッダを一式付ける**（_documents/非機能設計.md 1 章）
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
    // **2 要素の QR は data: URI で描く。** 外部から画像を取りに行かせない
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; frame-ancestors 'none'; "
        + "base-uri 'self'; object-src 'none'";
    await next();
});

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// 回答画面（TypeScript + Vite + Svelte のビルド成果物）
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapFormEndpoints();
app.MapAdminAuthEndpoints();
app.MapAdminSurveyEndpoints();

// **管理画面は別の入口。** 回答者へ管理画面のコードを配らない
app.MapGet("/admin", () => Results.File("admin.html", "text/html"));
app.MapFallbackToFile("/admin/{**path}", "admin.html");

// **Defender for Storage を使うときだけ受け口を生やす。**
// 使わない構成で認証の外の口を開けたままにしない
if (attachmentOptions.VirusScan is
    { Enabled: true, Provider: VirusScanProvider.DefenderForStorage } defenderOptions)
{
    app.MapMalwareScanEndpoints(defenderOptions.EventGridKey
        ?? throw new InvalidOperationException(
            "QUESTIONNAIRE_VIRUSSCAN_DEFENDER_EVENTGRIDKEY が設定されていない"
            + "（判定の受け口を守る合言葉。無いと誰でも『検出なし』を送り込める）"));
}

// **`/f/{publicId}` は画面側で解釈する。** サーバは同じ入口を返すだけ。
// 存在しない公開 ID でも同じ応答にして、総当たりで実在が分からないようにする
app.MapFallbackToFile("/f/{**path}", "index.html");

// 生存確認。**アンケートの情報を出さない**
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>結合テストから参照するための入口。</summary>
public partial class Program;
