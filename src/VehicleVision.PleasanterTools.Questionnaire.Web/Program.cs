using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Scripting;
using VehicleVision.PleasanterTools.Questionnaire.Web;
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

// ---- 複数インスタンスの認証 --------------------------------------------------
// 管理画面の Cookie は ASP.NET Core Data Protection で保護される。AKS で複数 Pod にすると、
// 鍵束を共有しない限り「別 Pod へ振られた途端にログアウト」になる。
// App Service／IIS の既定動作は変えず、共有先を明示した環境だけ永続化する。
var dataProtectionKeysPath = builder.Configuration[DataProtectionKeys.PathSetting];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    var keysDirectory = new DirectoryInfo(Path.GetFullPath(dataProtectionKeysPath));
    Directory.CreateDirectory(keysDirectory.FullName);
    builder.Services
        .AddDataProtection()
        .SetApplicationName(DataProtectionKeys.ApplicationName)
        .PersistKeysToFileSystem(keysDirectory);
}

// ---- 設定 ------------------------------------------------------------------
// **資格情報の実値は設定ファイルへ書かない。** 環境変数か Key Vault から読む
// （App_Data/Parameters/README.md）
var provider = Enum.Parse<DatabaseProvider>(
    builder.Configuration["QUESTIONNAIRE_DB_PROVIDER"] ?? nameof(DatabaseProvider.SqlServer));
var connectionString = builder.Configuration["QUESTIONNAIRE_DB_CONNECTIONSTRING"]
    ?? throw new InvalidOperationException("QUESTIONNAIRE_DB_CONNECTIONSTRING が設定されていない");

// **DB への通信が平文で流れていないかを起動時に見る。**
// 接続文字列は運用者が与えるのでコードからは中身が見えず、
// 暗号化を切った設定のまま本番へ出ても気付けない（CodeQL の cs/insecure-sql-connection）。
// **既定は厳しい側。** 検証環境は自己署名の証明書を使うので明示して緩める
ConnectionSecurity.EnsureSecure(
    provider,
    connectionString,
    allowInsecure: string.Equals(
        builder.Configuration["QUESTIONNAIRE_DB_ALLOW_INSECURE"],
        "true",
        StringComparison.OrdinalIgnoreCase));

// **マイグレーションを当てる口。** アプリ起動時の自動適用はしない
// （_documents/データモデル設計.md 5 章。スケールアウト時に同時実行され得る）。
// **当てる道具を別に作らない。** 接続文字列の読み方が二重になり、片方だけ直す事故が起きる
if (MigrationCommand.IsRequested(args))
{
    Environment.ExitCode = await MigrationCommand.RunAsync(provider, connectionString, args);
    return;
}

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
// **ヘッダ画像の置き場**（Issue #56）。外部のストレージへは置かない
builder.Services.AddSingleton<ISurveyAssetStore, SurveyAssetStore>();
builder.Services.AddSingleton<IAuditLogStore, AuditLogStore>();

// **添付を弾いた記録は監査ログと別の表**（Issue #39）。
// あちらは IpAddress を持つ。**弾いた記録は回答者側の出来事**なので、
// 同じ表へ入れると「回答者を完全匿名にする」前提と衝突する
builder.Services.AddSingleton<IAttachmentRejectionStore, AttachmentRejectionStore>();

// **異常はログにしか出ていなかった**（Issue #80）。
// **Pleasanter を経由せず本アプリの DB へ溜める。**
// 知らせの多くは「Pleasanter へ届かない」事象そのもので、届け先にはできない
builder.Services.AddSingleton<IAdminNotificationStore, AdminNotificationStore>();
builder.Services.AddSingleton<IAltchaChallengeStore, AltchaChallengeStore>();

// **bot 対策の 4 枚目**（Issue #55）。送信チケット・最短時間・honeypot と重ねる。
// **自前設置なので、回答者の情報を第三者へ送らない**（完全匿名と両立する）
builder.Services.AddSingleton(AltchaOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(serviceProvider => new AltchaGuard(
    builder.Configuration["QUESTIONNAIRE_SECRET_KEY"]
        ?? throw new InvalidOperationException("QUESTIONNAIRE_SECRET_KEY が設定されていない"),
    serviceProvider.GetRequiredService<AltchaOptions>(),
    serviceProvider.GetRequiredService<IAltchaChallengeStore>()));

builder.Services.AddSingleton(pleasanterOptions);
builder.Services.AddSingleton(new PleasanterDateTime(pleasanterOptions.ApiKeyUserTimeZoneId));
builder.Services.AddSingleton<PleasanterRecordBuilder>();
// **変換スクリプトは上限付きで走らせる**（Issue #83、_documents/アーキテクチャ方針.md 8 章）。
// **上限が無いと、書き間違えた `while (true)` 1 つで送信ワーカーが永久に固まる。**
// 打ち切ったものはマッピングの不備になり、回答はデッドレターへ回る
builder.Services.AddSingleton(ScriptConverterOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<IScriptConverter>(serviceProvider =>
    new JintScriptConverter(serviceProvider.GetRequiredService<ScriptConverterOptions>()));
builder.Services.AddSingleton(serviceProvider =>
    new MappingEvaluator(serviceProvider.GetRequiredService<IScriptConverter>()));
builder.Services.AddHttpClient<PleasanterApiClient>(client =>
    client.Timeout = pleasanterOptions.Timeout);

// **溜まりすぎたら受付を止める**（Issue #72、_documents/非機能設計.md 2 章）。
// **WAF が無い導入先を想定した最後の壁。** 分散した相手にはレート制限が効かない。
// 攻撃が無くても、Pleasanter が長く落ちれば同じように溜まる
builder.Services.AddSingleton(BacklogGuardOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<ResponseBacklogGuard>();

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

// **埋め込みを許す配信元**（Issue #104 / #107）。**既定は空＝一切埋め込めない**
var embedOptions = EmbedOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(embedOptions);

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
builder.Services.AddSingleton<IAdminInvitationStore, AdminInvitationStore>();
// **パスワードの条件は設定で決める**（Issue #157）。
// 実値は App_Data/Parameters/Security.json（Pleasanter 本体と同じ書き方）。
//
// **優先順位は Security.local.json ＞ 環境変数 ＞ Security.json**
// （App_Data/Parameters/README.md）。既定の並びは環境変数が後ろなので、
// **JSON を先に積んでから環境変数を積み直す。**
//
// ⚠️ **ここだけ JSON を読んでいる。** Service.json と Pleasanter.json は
// 環境変数から読む作りのままで、そちらの読み込みは別課題（Issue #158）
builder.Configuration
    .AddJsonFile("App_Data/Parameters/Security.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddJsonFile("App_Data/Parameters/Security.local.json", optional: true, reloadOnChange: false);

var passwordPolicyOptions = new AdminPasswordPolicyOptions
{
    MinimumLength = int.TryParse(builder.Configuration["PasswordMinimumLength"], out var minimumLength)
        ? minimumLength
        : new AdminPasswordPolicyOptions().MinimumLength,
    AllowSameAsLoginId =
        bool.TryParse(builder.Configuration["PasswordAllowSameAsLoginId"], out var allowSameAsLoginId)
        && allowSameAsLoginId,
    Policies = builder.Configuration.GetSection("PasswordPolicies").Get<List<AdminPasswordRule>>() ?? [],
};

// **組み立てられない正規表現は、ここで落ちる。** 起動前に気付ける
builder.Services.AddSingleton(new AdminPasswordPolicy(passwordPolicyOptions));

builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton(new SecretProtector(secretKey));
// **2 要素認証をどこまで求めるか**（Issue #154）。**既定は任意。**
//
// **知らない値は落とす。** 黙って既定へ落ちると、必須にしたつもりで任意のまま動く。
var twoFactorPolicy = TwoFactorPolicy.Optional;
if (builder.Configuration[AdminAuthOptions.TwoFactorSetting] is { Length: > 0 } twoFactorSetting)
{
    if (!Enum.TryParse(twoFactorSetting, ignoreCase: true, out twoFactorPolicy)
        || !Enum.IsDefined(twoFactorPolicy))
    {
        throw new InvalidOperationException(
            $"{AdminAuthOptions.TwoFactorSetting} は required / optional / disabled のいずれかにする"
            + $"（今の値: {twoFactorSetting}）");
    }
}

builder.Services.AddSingleton(new AdminAuthOptions { TwoFactor = twoFactorPolicy });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AdminAuthenticator>();
builder.Services.AddSingleton<AdminUserService>();

// ---- bot 対策 --------------------------------------------------------------
// **外部の CAPTCHA を使わない**（_documents/非機能設計.md 1 章）。
// 完全匿名を掲げている以上、回答者の IP や操作の癖を第三者へ送れない。
// 代わりに送信チケット・最短時間・ハニーポットを重ねる（Services/SubmissionGuard.cs）
var submissionGuardOptions = new SubmissionGuardOptions
{
    // **切れるのは検証環境のため。** 本番で切らないこと
    Enabled = !string.Equals(
        builder.Configuration["QUESTIONNAIRE_BOT_MITIGATION"], "off", StringComparison.OrdinalIgnoreCase),
    MinimumElapsed = TimeSpan.FromSeconds(
        int.TryParse(builder.Configuration["QUESTIONNAIRE_SUBMIT_MIN_SECONDS"], out var minSeconds)
            ? minSeconds
            : 3),
    Lifetime = TimeSpan.FromHours(
        int.TryParse(builder.Configuration["QUESTIONNAIRE_SUBMIT_TICKET_HOURS"], out var ticketHours)
            ? ticketHours
            : 24),
};

builder.Services.AddSingleton(submissionGuardOptions);
builder.Services.AddSingleton(serviceProvider => new SubmissionGuard(
    secretKey, submissionGuardOptions, serviceProvider.GetRequiredService<TimeProvider>()));

// **既定は厳しく。** 緩めるのは検証環境だけにすること。
// 端から端まで通す試験は 1 つの IP から大量に叩くので、既定のままだと自分で枠を使い切る
var loginPermitLimit = int.TryParse(
    builder.Configuration["QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN"], out var configuredLogin)
    ? configuredLogin
    : 10;

var submitPermitLimit = int.TryParse(
    builder.Configuration["QUESTIONNAIRE_SUBMITS_PER_MIN"], out var configuredSubmits)
    ? configuredSubmits
    : 20;
var requestPermitLimit = int.TryParse(
    builder.Configuration["QUESTIONNAIRE_REQUESTS_PER_MIN"], out var configuredRequests)
    ? configuredRequests
    : 60;

builder.Services
    .AddAuthentication(AdminAuthSchemes.Session)
    .AddCookie(AdminAuthSchemes.Session, options =>
    {
        AdminAuthSchemes.Configure(options, "q.admin", AdminAuthSchemes.SessionLifetime);

        // **止めた管理者を、その場で追い出す。** cookie は 8 時間有効なので、
        // これが無いと止めたのに最大 8 時間は操作できてしまう
        options.Events.OnValidatePrincipal = AdminSessionGuard.ValidateAsync;
    })
    .AddCookie(AdminAuthSchemes.Pending, options => AdminAuthSchemes.Configure(
        options, "q.admin.pending", AdminAuthSchemes.PendingLifetime))
    .AddCookie(AdminAuthSchemes.Reenroll, options => AdminAuthSchemes.Configure(
        options, "q.admin.reenroll", AdminAuthSchemes.ReenrollLifetime));

builder.Services.AddAuthorization(options =>
{
    // **ログイン済み（2 要素まで通った状態）だけを通す。**
    // 途中状態の cookie では何も操作させない
    options.AddPolicy(AdminAuthSchemes.SessionPolicy, policy => policy
        .AddAuthenticationSchemes(AdminAuthSchemes.Session)
        .RequireAuthenticatedUser());

    // **他人に触れるのは Administrator だけ**（_documents/非機能設計.md 1 章）
    options.AddPolicy(AdminAuthSchemes.AdministratorPolicy, policy => policy
        .AddAuthenticationSchemes(AdminAuthSchemes.Session)
        .RequireAuthenticatedUser()
        .RequireRole(nameof(AdminRole.Administrator)));
});

// **送信ワーカーは .Web に同居させる**（_documents/アプリケーション設計.md 8 章）。
// Azure App Service では別プロセス常駐の手段が限られるため。**Always On を有効にすること**
builder.Services.AddSingleton(ResponseSenderOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<ResponseSender>();
builder.Services.AddHostedService<ResponseSenderHostedService>();

// **管理操作の記録は放っておくと増え続ける**（_documents/データモデル設計.md）。
// 期限を過ぎた分を消す係を常駐させる。**既定は 365 日残す**
builder.Services.AddSingleton(AuditLogRetentionOptions.FromConfiguration(builder.Configuration));
builder.Services.AddHostedService<AuditLogRetentionService>();

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

    // **回答の送信だけ別枠にする。** 書き込みは読み取りより高くつくので、
    // 画面を開くだけの要求と同じ枠で数えない。
    // **NAT の内側から大勢が答えることがある**ので、締めすぎないこと
    options.AddPolicy(FormEndpoints.SubmitRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = submitPermitLimit,
                Window = TimeSpan.FromMinutes(1),
            }));

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

// **スキーマが揃っていないまま起動しない。**
// 列の無い状態で動くと `Invalid column name` としか出ず、
// 「マイグレーションを当て忘れている」とは分からない（Issue #32。実際に 21 件落ちた）。
// **自動では当てない。** 何が足りないかを言って止まる
if (!string.Equals(
    builder.Configuration["QUESTIONNAIRE_DB_SKIP_MIGRATION_CHECK"],
    "true",
    StringComparison.OrdinalIgnoreCase))
{
    var pendingMigrations = DatabaseMigrator.PendingMigrations(provider, connectionString);
    if (pendingMigrations.Count > 0)
    {
        throw new InvalidOperationException(
            "DB のスキーマが古い。当たっていないマイグレーションがある: "
            + string.Join(" / ", pendingMigrations)
            + "。--migrate を付けて起動すると当たる"
            + "（開発環境の手順は _documents/開発環境.md）");
    }
}

var app = builder.Build();

// **CSP は起動時に 1 度だけ組み立てる**（Issue #104 / #107）。
//
// **アンケートごとには出し分けない。** 出し分けるには、回答画面の HTML を返す時点で
// DB を引くことになり、**ヘッダの違いから公開 ID の実在が分かってしまう**
// （`_documents/非機能設計.md` 1 章「識別子の秘匿」）。
//
// **代わりに、許す配信元を運用側だけが決められる場所（設定）へ置く。**
// 既定は空なので、設定しなければ従来と同じ CSP になる。
// **`frame-src https:` のようには絶対に広げない**
var embedSources = embedOptions.CspSources;
var contentSecurityPolicy = string.Join("; ",
[
    "default-src 'self'",
    // **画像の埋め込み先も設定で許した配信元だけ**（2 要素の QR は data: URI で描く）
    "img-src 'self' data:" + Join(embedSources),
    // **設定が空なら 'none'。** 指定そのものを省くと default-src へ落ちる
    "frame-src " + (embedSources.IsEmpty ? "'none'" : string.Join(' ', embedSources)),
    "frame-ancestors 'none'",
    "base-uri 'self'",
    "object-src 'none'",
]);

static string Join(System.Collections.Immutable.ImmutableArray<string> sources) =>
    sources.IsEmpty ? string.Empty : " " + string.Join(' ', sources);

// **リバースプロキシ配下でも本当の送信元 IP を見る。** レート制限が効かなくなるため。
// 転送ヘッダを無条件には信じず、運用者が指定した Ingress の CIDR だけを追加する。
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
foreach (var network in ForwardedProxyNetworks.Parse(
             builder.Configuration[ForwardedProxyNetworks.Setting]))
{
    forwardedHeadersOptions.KnownIPNetworks.Add(network);
}
app.UseForwardedHeaders(forwardedHeadersOptions);

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    // Kubelet の HTTP probe は 3xx も成功と扱う。probe をリダイレクトすると、
    // DB 障害時の /ready=503 を見ずに 307 を成功扱いするため、この2経路だけ除外する。
    app.UseWhen(
        context => !string.Equals(context.Request.Path.Value, "/healthz", StringComparison.Ordinal)
            && !string.Equals(context.Request.Path.Value, "/ready", StringComparison.Ordinal),
        branch => branch.UseHttpsRedirection());
}

// **セキュリティヘッダを一式付ける**（_documents/非機能設計.md 1 章）
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
    // **2 要素の QR は data: URI で描く。** 外部から画像を取りに行かせない
    headers["Content-Security-Policy"] = contentSecurityPolicy;
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
app.MapAdminUserEndpoints();
app.MapAdminSurveyEndpoints();
app.MapAdminNoteEndpoints();
app.MapAdminTemplateEndpoints();
app.MapAdminAuditLogEndpoints();
app.MapAdminOutboxEndpoints();
app.MapAdminNotificationEndpoints();

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
            + "（判定の受け口を守るパスワード。無いと誰でも『検出なし』を送り込める）"));
}

// **`/f/{publicId}` は画面側で解釈する。** サーバは同じ入口を返すだけ。
// 存在しない公開 ID でも同じ応答にして、総当たりで実在が分からないようにする
app.MapFallbackToFile("/f/{**path}", "index.html");

// 生存確認。**アンケートの情報を出さない**
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// 受付可能かの確認。Pleasanter が止まっても回答は DB に積んで再送できるため、
// **ここで見る依存先は本アプリの DB だけ。** 例外の中身は接続先や資格情報を
// 含み得るので応答へ出さない。
app.MapGet("/ready", async (CancellationToken cancellationToken) =>
{
    var failure = await DatabaseMigrator.WaitForDatabaseAsync(
        provider,
        connectionString,
        TimeSpan.Zero,
        cancellationToken);

    return failure is null
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.Run();

/// <summary>結合テストから参照するための入口。</summary>
public partial class Program;
