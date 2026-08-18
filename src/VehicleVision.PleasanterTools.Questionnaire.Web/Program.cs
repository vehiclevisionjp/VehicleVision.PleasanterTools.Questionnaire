using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
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

builder.Services.AddSingleton(pleasanterOptions);
builder.Services.AddSingleton(new PleasanterDateTime(pleasanterOptions.ApiKeyUserTimeZoneId));
builder.Services.AddSingleton<PleasanterRecordBuilder>();
builder.Services.AddSingleton(_ => new MappingEvaluator());
builder.Services.AddHttpClient<PleasanterApiClient>(client =>
    client.Timeout = pleasanterOptions.Timeout);

builder.Services.AddSingleton<ResponseIntake>();

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
                    PermitLimit = 60,
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
    // 全体の枠に紛れさせると、1 分に 60 回の総当たりが通ってしまう
    options.AddPolicy(AdminAuthSchemes.LoginRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
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
    headers["Content-Security-Policy"] =
        "default-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
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

// **`/f/{publicId}` は画面側で解釈する。** サーバは同じ入口を返すだけ。
// 存在しない公開 ID でも同じ応答にして、総当たりで実在が分からないようにする
app.MapFallbackToFile("/f/{**path}", "index.html");

// 生存確認。**アンケートの情報を出さない**
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>結合テストから参照するための入口。</summary>
public partial class Program;
