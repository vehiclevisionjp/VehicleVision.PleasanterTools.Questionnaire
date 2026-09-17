using System.Threading.RateLimiting;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Scalar.AspNetCore;
using StackExchange.Redis;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
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

// ---- App_Data/Parameters の設定ファイル --------------------------------------
// **一番先に積む。** 以降の読み取り（DB・Pleasanter・管理者の認証・アクセス解析…）が
// すべてこれを見る。**優先順位は {名前}.local.json ＞ 環境変数 ＞ {名前}.json**
// （App_Data/Parameters/README.md。Issue #158）
builder.Configuration.AddParameterFiles();

// **CIDR の書き間違いは起動時に止める。** 無制限へ黙って落ちると、絞ったつもりの口が開く。
var endpointNetworkRestrictions =
    EndpointNetworkRestrictions.FromConfiguration(builder.Configuration);

// **既定は閉じる。** 管理 API を含む仕様から下調べを済ませられるため、
// 明示した環境だけで出す。
var openApiExposure = OpenApiExposureOptions.FromConfiguration(builder.Configuration);

// **HTTP を許す構成は運用者に明示させる。** 未設定や false では従来の保護を変えない。
var transportSecurity = TransportSecurityOptions.FromConfiguration(builder.Configuration);

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
var sharedStateOptions = SharedStateOptions.FromConfiguration(builder.Configuration);
var sessionStoreKind =
    builder.Configuration["QUESTIONNAIRE_ADMIN_SESSION_STORE"] ?? "Database";
var useRedisSessionStore =
    string.Equals(sessionStoreKind, "Redis", StringComparison.OrdinalIgnoreCase);

// **接続設定と ConnectionMultiplexer は共有状態と管理者セッションで共用する。**
// 別々に持つと、片方だけ接続先や資格情報を更新する事故が起きる。
if (sharedStateOptions.UseRedis || useRedisSessionStore)
{
    var redisConnectionString =
        builder.Configuration[SharedStateOptions.ConnectionStringKey]
        ?? throw new InvalidOperationException(
            $"{SharedStateOptions.ConnectionStringKey} is required when Redis is used.");
    var redisConfiguration = ConfigurationOptions.Parse(redisConnectionString);

    // **一時的に到達できなくてもアプリ自体は起動する。**
    // 共有状態はプロセス内へ倒し、セッション照合はログアウト扱いにする。
    redisConfiguration.AbortOnConnectFail = false;
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect(redisConfiguration));
}

builder.Services.AddSingleton(sharedStateOptions);
if (sharedStateOptions.UseRedis)
{
    builder.Services.AddSingleton<ISharedRateLimitStore, RedisSharedRateLimitStore>();
    builder.Services.AddSingleton<ISharedBacklogStateStore, RedisSharedBacklogStateStore>();
}

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

// **設定ファイルのキーは正式な名前へ写してある**（ParameterFiles）。
// ここは 1 つの名前だけを見る
var timeZoneDefault =
    builder.Configuration[ParameterFiles.TimeZoneDefaultKey] ?? "Asia/Tokyo";

var pleasanterOptions = new PleasanterOptions
{
    BaseUrl = builder.Configuration["QUESTIONNAIRE_PLEASANTER_BASEURL"]
        ?? throw new InvalidOperationException("QUESTIONNAIRE_PLEASANTER_BASEURL が設定されていない"),
    ApiKey = builder.Configuration["QUESTIONNAIRE_PLEASANTER_APIKEY"]
        ?? throw new InvalidOperationException("QUESTIONNAIRE_PLEASANTER_APIKEY が設定されていない"),

    // **書き間違いは既定へ落とす。** ここで止めると、
    // 版やタイムアウトの打ち間違いでアプリが上がらなくなる
    ApiVersion = decimal.TryParse(
        builder.Configuration["QUESTIONNAIRE_PLEASANTER_APIVERSION"],
        System.Globalization.NumberStyles.Number,
        System.Globalization.CultureInfo.InvariantCulture,
        out var apiVersion) && apiVersion > 0
        ? apiVersion
        : PleasanterOptions.DefaultApiVersion,
    Timeout = int.TryParse(
        builder.Configuration["QUESTIONNAIRE_PLEASANTER_TIMEOUTSECONDS"], out var timeoutSeconds)
        && timeoutSeconds > 0
        ? TimeSpan.FromSeconds(timeoutSeconds)
        : PleasanterOptions.DefaultTimeout,

    // **API キー側の指定が無ければ、アプリの既定タイムゾーンを使う**
    // （Pleasanter.json の ApiKeyUserTimeZoneId の但し書きと同じ）
    ApiKeyUserTimeZoneId =
        builder.Configuration["QUESTIONNAIRE_PLEASANTER_TIMEZONE"] ?? timeZoneDefault,
};

// ---- サービス --------------------------------------------------------------
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IDbConnectionFactory>(
    new DbConnectionFactory(provider, connectionString));
builder.Services.AddSingleton<IResponseOutbox, ResponseOutbox>();
builder.Services.AddSingleton<IResponseTokenStore, ResponseTokenStore>();
builder.Services.AddSingleton<IAssetTicketStore, AssetTicketStore>();
builder.Services.AddSingleton<IAssetHistoryOutbox, AssetHistoryOutbox>();
builder.Services.AddSingleton<ISurveySnapshotStore, SurveySnapshotStore>();
builder.Services.AddSingleton<ISurveyRepository, SurveyRepository>();
builder.Services.AddSingleton<IMonitoringStore, MonitoringStore>();
builder.Services.AddSingleton<ISurveyDraftStore, SurveyDraftStore>();
builder.Services.AddSurveyAssetStorage(builder.Configuration);
builder.Services.AddSingleton<ISurveyDeletionStore, SurveyDeletionStore>();
builder.Services.AddSingleton<IAuditLogStore, AuditLogStore>();
builder.Services.AddSingleton<ISamlSettingStore, SamlSettingStore>();

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
builder.Services.AddSingleton(AdminCaptchaOptions.FromConfiguration(builder.Configuration));

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
var assetOptions = AssetOptions.FromConfiguration(
    builder.Configuration, attachmentOptions.VirusScan.Enabled);
builder.Services.AddSingleton(assetOptions);

// **埋め込みを許す配信元**（Issue #104 / #107）。**既定は空＝一切埋め込めない**
var embedOptions = EmbedOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(embedOptions);

// **添付と配布資産は multipart で届く。上限を既定値に任せない**
// （_documents/非機能設計.md 1 章）。
// 大きい方に合わせ、個別の入口ではそれぞれの上限まで絞る
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = Math.Max(
        attachmentOptions.MaxRequestBodyBytes,
        assetOptions.MaxRequestBodyBytes));

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
builder.Services.AddSingleton(serviceProvider => new AssetInspector(
    assetOptions, serviceProvider.GetService<IVirusScanner>()));

// ---- アクセス解析（Issue #162）----------------------------------------------
// **既定は無効。** 設定しなければ、回答者の端末から第三者への要求は 1 つも出ない。
var analyticsOptions = AnalyticsOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(analyticsOptions);

// ---- 外部の CAPTCHA（Issue #164）--------------------------------------------
// **既定は自前設置の ALTCHA。** 何も設定しなければ外部通信は出ない
// （インターネットへ出られないイントラでも動く）。
//
// ⚠️ **秘密鍵は設定ファイルへ書かせない。** 環境変数か Key Vault から読む
var captchaOptions = CaptchaOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(captchaOptions);

// **検証の待ち時間に上限を持たせる。** 外部が遅いだけで送信が固まらないように。
// ⚠️ **到達できないときは通さない**（CaptchaVerifier の但し書き）
builder.Services
    .AddHttpClient(CaptchaVerifier.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<CaptchaVerifier>();

// ---- 管理者の認証 ----------------------------------------------------------
// **共有鍵を復号するための鍵。** 失うと登録済みの 2 要素が全て使えなくなるので、
// **App Service の設定か Key Vault に置き、控えを取っておくこと**
var secretKey = builder.Configuration["QUESTIONNAIRE_SECRET_KEY"]
    ?? throw new InvalidOperationException(
        "QUESTIONNAIRE_SECRET_KEY が設定されていない（Base64 の 32 バイト）");

builder.Services.AddSingleton<IAdminUserStore, AdminUserStore>();
builder.Services.AddSingleton<IAdminInvitationStore, AdminInvitationStore>();

// **既定は DB。** 追加の基盤なしで、個別失効と端末一覧を使えるようにする。
// 大規模構成では Redis を選べるが、停止時に cookie だけで通すことはしない。
if (string.Equals(sessionStoreKind, "Database", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAdminSessionStore, DatabaseAdminSessionStore>();
}
else if (string.Equals(sessionStoreKind, "Redis", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAdminSessionStore>(serviceProvider =>
        new RedisAdminSessionStore(
            serviceProvider.GetRequiredService<IConnectionMultiplexer>(),
            builder.Configuration["QUESTIONNAIRE_ADMIN_SESSION_REDIS_PREFIX"]
                ?? "questionnaire:admin-session:"));
}
else
{
    throw new InvalidOperationException(
        "QUESTIONNAIRE_ADMIN_SESSION_STORE must be Database or Redis "
        + $"(current value: {sessionStoreKind}).");
}

builder.Services.AddSingleton<AdminSessionManager>();
// **パスワードの条件は設定で決める**（Issue #157）。
// 実値は App_Data/Parameters/Security.json（Pleasanter 本体と同じ書き方）。
// **積む場所は上の 1 か所にまとめてある。**
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

// **外部設定だけで有効にしている従来構成は、起動時の検証も保つ。**
// 書き間違いを 500 応答になるまで見つけられない構成へ後退させない。
_ = SamlOptions.FromConfiguration(builder.Configuration);

// **要求ごとに DB を読む。** 管理画面で変えた設定を再起動なしで反映する（Issue #254）。
// 外部設定に値があれば DB より優先し、動いている構成を更新で変えない。
builder.Services.AddSingleton<ISamlOptionsProvider, SamlOptionsProvider>();
builder.Services.AddSingleton<SamlAuthenticator>();
builder.Services
    .AddHttpClient("SamlMetadata", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // **転送先も検査せず追わない。** 内部アドレスへの迂回路にしない。
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = SamlMetadataConnection.ConnectAsync,
    });
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

// **1 画面で何本も出るもの（ビルド成果物・本文画像）の枠。**
// ⚠️ **ここを固定値にすると、検証環境の緩和が効かない。**
// 端から端まで通す試験と写しの一式は 1 つの IP から大量に叩くため、
// 固定の 600 では自分で使い切る（Issue #322）
var assetPermitLimit = int.TryParse(
    builder.Configuration["QUESTIONNAIRE_ASSET_REQUESTS_PER_MIN"], out var configuredAssets)
    ? configuredAssets
    : 600;

// **アンケート 1 本あたりの枠。** 他の枠と同じく検証環境でだけ緩められるようにする。
// ⚠️ **`publicId` を持たない要求は 1 つの枠にまとめて数えられる**ので、
// これは実質「回答画面以外すべての合計」の上限にもなる（Issue #311）
var formPermitLimit = int.TryParse(
    builder.Configuration["QUESTIONNAIRE_FORM_REQUESTS_PER_MIN"], out var configuredForm)
    ? configuredForm
    : 600;

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

builder.Services.Configure<CookieAuthenticationOptions>(
    AdminAuthSchemes.Pending,
    options => options.Events.OnValidatePrincipal = AdminSessionGuard.ValidatePendingAsync);
builder.Services.Configure<CookieAuthenticationOptions>(
    AdminAuthSchemes.Reenroll,
    options => options.Events.OnValidatePrincipal = AdminSessionGuard.ValidateReenrollAsync);

builder.Services.AddAuthorization(options =>
{
    // **ログイン済み（2 要素まで通った状態）だけを通す。**
    // 途中状態の cookie では何も操作させない
    options.AddPolicy(AdminAuthSchemes.SessionPolicy, policy => policy
        .AddAuthenticationSchemes(AdminAuthSchemes.Session)
        .RequireAuthenticatedUser());

    // **他人に触れるのは特権管理者だけ**（_documents/非機能設計.md 1 章）
    options.AddPolicy(AdminAuthSchemes.AdministratorPolicy, policy => policy
        .AddAuthenticationSchemes(AdminAuthSchemes.Session)
        .RequireAuthenticatedUser()
        .RequireRole(nameof(AdminRole.Administrator)));

    // **操作は権限で要求する**（Issue #160）。
    //
    // **権限は cookie へ焼かない。** 役割の claim から、その都度対応表を引く。
    // 焼くと、役割を変えても再ログインまで効かない。
    foreach (var permission in AdminPermissions.All)
    {
        options.AddPolicy(AdminPermissions.PolicyOf(permission), policy => policy
            .AddAuthenticationSchemes(AdminAuthSchemes.Session)
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                AdminPermissions.Has(context.User.FindFirstValue(ClaimTypes.Role), permission)));
    }
});

// **送信ワーカーは .Web に同居させる**（_documents/アプリケーション設計.md 8 章）。
// Azure App Service では別プロセス常駐の手段が限られるため。**Always On を有効にすること**
builder.Services.AddSingleton(ResponseSenderOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<ResponseSender>();
builder.Services.AddHostedService<ResponseSenderHostedService>();
builder.Services.AddSingleton<AssetHistorySender>();
builder.Services.AddHostedService<AssetHistorySenderHostedService>();

// **メールの送信ワーカー**（Issue #189）。**既定は無効で、設定したときだけ常駐する。**
// 回答の送信ワーカーとは別に動く。**メールが詰まっても回答は送られ、
// 回答が詰まってもメールは出る。** どちらかの不調がもう一方を止めない
var mailOptions = MailOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(mailOptions);
builder.Services.AddSingleton<IMailOutbox, MailOutbox>();
// ⚠️ **宛先も本文も暗号化して置く。** 完全匿名の前提で、個人を指す値が
// DB に載る唯一の場所（Issue #189）
builder.Services.AddSingleton<IMailPayloadProtector, MailPayloadProtector>();

// **自動返信は、メールが無効でも組み立てられる形にしておく。**
// 有効になっていなければ積まずに記録だけ残す（設定だけ済ませて気付かない事故を防ぐ）
// **再編集リンクのトークン**（Issue #202）。
// ⚠️ **回答本体のトークンをメールへ載せないための表。** 漏れても失効させられる
builder.Services.AddSingleton<IResponseEditTokenStore, ResponseEditTokenStore>();
builder.Services.AddSingleton<AutoReplyDispatcher>();
builder.Services.AddSingleton<AutoReplyTestMailer>();
// **招待を本人へ直接送る**（Issue #189）。手渡しの途中で漏れる経路を減らす。
// **送れない構成でも招待は出せる**（画面の URL は今までどおり返る）
builder.Services.AddSingleton<AdminInvitationMailer>();

if (mailOptions.IsReady)
{
    // **送信経路は設定で選ぶ**（Issue #198）。
    // ⚠️ **SES と ACS はマネージド ID で通るので、保管する秘密が 0 になる。**
    // SMTP は必ずパスワードを 1 つ持つことになる
    switch (mailOptions.Transport)
    {
        case MailTransportKind.AmazonSes:
            builder.Services.AddSingleton<IMailTransport, SesMailTransport>();
            break;

        case MailTransportKind.AzureCommunicationServices:
            builder.Services.AddSingleton<IMailTransport, AcsMailTransport>();
            break;

        default:
            builder.Services.AddSingleton<IMailTransport, SmtpMailTransport>();
            break;
    }

    builder.Services.AddSingleton(MailSenderOptions.FromConfiguration(builder.Configuration));
    builder.Services.AddSingleton<MailSender>();
    builder.Services.AddHostedService<MailSenderHostedService>();
}

// **設定したときだけ監視の口を生やす。** 既定で外部から DB の状態を読める口を作らない。
var monitoringTokenValue = builder.Configuration[MonitoringToken.Setting];
MonitoringToken? monitoringToken = string.IsNullOrWhiteSpace(monitoringTokenValue)
    ? null
    : new MonitoringToken(monitoringTokenValue);
if (monitoringToken is not null)
{
    builder.Services.AddSingleton(serviceProvider => new MonitoringService(
        provider,
        connectionString,
        serviceProvider.GetRequiredService<IResponseOutbox>(),
        serviceProvider.GetRequiredService<IMonitoringStore>(),
        serviceProvider.GetRequiredService<IMailOutbox>(),
        mailOptions,
        serviceProvider.GetRequiredService<TimeProvider>()));
}

// **どの設定ファイルを読んだかを記録に残す**（Issue #158）。
// **optional なので、置き場を間違えても黙って既定で動いてしまう。**
// 「読めているつもりで読めていない」を起動時に見せる
builder.Services.AddHostedService(serviceProvider => new ParameterFilesReport(
    serviceProvider.GetRequiredService<IConfiguration>(),
    serviceProvider.GetRequiredService<ILogger<ParameterFilesReport>>()));

// **管理操作の記録は放っておくと増え続ける**（_documents/データモデル設計.md）。
// 期限を過ぎた分を消す係を常駐させる。**既定は 365 日残す**
builder.Services.AddSingleton(AuditLogRetentionOptions.FromConfiguration(builder.Configuration));
builder.Services.AddHostedService<AuditLogRetentionService>();

// ---- レート制限 ------------------------------------------------------------
// **DB へ書く前に効かせる。** 書いてから弾いても消費は起きている
// （_documents/非機能設計.md 1 章）
builder.Services.AddRateLimiter();
builder.Services
    .AddOptions<RateLimiterOptions>()
    .Configure<IServiceProvider, ILoggerFactory>((options, serviceProvider, loggerFactory) =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var sharedRateLimits = serviceProvider.GetService<ISharedRateLimitStore>();
    var rateLimitLogger = loggerFactory.CreateLogger("SharedRateLimit");

    // **複数の軸で掛ける。** 1 つの軸だけでは抜けられる
    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            // **本文画像とビルド成果物は 1 画面で複数要求される。**
            // 通常 API の 60 件枠を食わせると、同じ NAT 配下の回答者が
            // 数人開いただけでフォーム本体まで止まる（Issue #316）
            var isAsset = RateLimitPartitions.IsBulkAsset(context.Request.Path);
            var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var kind = isAsset ? "asset" : "api";
            return RateLimitPartitions.FixedWindow(
                $"{address}|{kind}",
                "request",
                isAsset ? assetPermitLimit : requestPermitLimit,
                TimeSpan.FromMinutes(1),
                sharedRateLimits,
                rateLimitLogger);
        }),
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            // ⚠️ **publicId を持たない要求はこの段で数えない**（Issue #311）。
            // 経路の値は UseRouting が利用者のミドルウェアより前に入るため、ここで取れる
            RateLimitPartitions.Survey(
                context.Request.RouteValues["publicId"]?.ToString(),
                formPermitLimit,
                TimeSpan.FromMinutes(1),
                sharedRateLimits,
                rateLimitLogger)));

    // **回答の送信だけ別枠にする。** 書き込みは読み取りより高くつくので、
    // 画面を開くだけの要求と同じ枠で数えない。
    // **NAT の内側から大勢が答えることがある**ので、締めすぎないこと
    options.AddPolicy(FormEndpoints.SubmitRateLimitPolicy, context =>
        RateLimitPartitions.FixedWindow(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            "submit",
            submitPermitLimit,
            TimeSpan.FromMinutes(1),
            sharedRateLimits,
            rateLimitLogger));

    // **ログインの試行だけは別枠で厳しくする。**
    // 全体の枠に紛れさせると、1 分に 60 回の総当たりが通ってしまう。
    //
    // **回数を設定で変えられるようにしてある。** 検証環境では端から端まで通す試験が
    // 既定の枠を使い切ってしまうため。**本番では既定のまま使うこと**
    options.AddPolicy(AdminAuthSchemes.LoginRateLimitPolicy, context =>
        RateLimitPartitions.FixedWindow(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            "login",
            loginPermitLimit,
            TimeSpan.FromMinutes(5),
            sharedRateLimits,
            rateLimitLogger));

    // **試し送信は 1 分に 3 通まで。** 任意の宛先へは送れないが、
    // 管理者本人のメールボックスや送信基盤を連打で埋めさせない。
    options.AddPolicy(AdminAutoReplyEndpoints.TestSendRateLimitPolicy, context =>
        RateLimitPartitions.FixedWindow(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            "auto-reply-test-send",
            3,
            TimeSpan.FromMinutes(1),
            sharedRateLimits,
            rateLimitLogger));
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
            // **英語で書く。** Azure の Kudu の Debug console で日本語が化ける（Issue #225）
            "The database schema is out of date. Pending migrations: "
            + string.Join(" / ", pendingMigrations)
            + ". Run this executable with --migrate to apply them"
            + " (see _documents/導入-更新運用手順書.md).");
    }
}

var app = builder.Build();

if (transportSecurity.AllowInsecure)
{
    // **英語で書く。** Azure の Kudu の Debug console で日本語が化ける（Issue #225）
    app.Logger.LogWarning(
        "Insecure HTTP mode is enabled by QUESTIONNAIRE_ALLOW_INSECURE. "
        + "HTTPS redirection and HSTS are disabled. "
        + "Use this mode only in a closed network because passwords are sent in plaintext "
        + "and SAML may not work.");
}

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

// **アクセス解析を有効にしたときだけ広げる**（Issue #162）。
// 既定では 1 つも足さないので、今までと同じ CSP になる。
//
// ⚠️ **`https:` のようには広げない。** 許すのは選んだサービスの配信元だけ。
// **inline script は許さない。** タグは同梱した JS から DOM へ差し込む
var analyticsSources = analyticsOptions.CspSources;

// **CAPTCHA も、外部を選んだときだけ広げる**（Issue #164）。
// どのサービスも iframe で課題を出すので、script-src と frame-src の両方に要る
var captchaSources = captchaOptions.CspSources;
var externalScriptSources = analyticsSources.AddRange(captchaSources);
const string scalarCspNonceKey = "ScalarCspNonce";
var contentSecurityPolicy = string.Join("; ",
[
    "default-src 'self'",
    // **画像の埋め込み先も設定で許した配信元だけ**（2 要素の QR は data: URI で描く）。
    // 解析は計測を画像で送ることがあるので、有効なときはその送信先も許す
    "img-src 'self' data:" + Join(embedSources) + Join(analyticsSources) + Join(captchaSources),
    // **設定が空なら 'none'。** 指定そのものを省くと default-src へ落ちる
    // **CAPTCHA は iframe で出る。** 埋め込みの許可と同じ枠へ足す
    "frame-src "
        + (embedSources.IsEmpty && captchaSources.IsEmpty
            ? "'none'"
            : string.Join(' ', embedSources.AddRange(captchaSources))),
    "frame-ancestors 'none'",
    "base-uri 'self'",
    "object-src 'none'",
    "script-src 'self'" + Join(externalScriptSources),
    "connect-src 'self'" + Join(externalScriptSources),
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

// **転送ヘッダから本当の送信元へ直した後で照合する。**
// 先に置くと、リバースプロキシ配下では全要求がプロキシ自身の IP に見える。
app.UseEndpointNetworkRestrictions(endpointNetworkRestrictions);

if (!app.Environment.IsDevelopment() && !transportSecurity.AllowInsecure)
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
    var csp = contentSecurityPolicy;
    if (context.Request.Path.StartsWithSegments("/scalar", StringComparison.OrdinalIgnoreCase))
    {
        // Scalar は画面を組み立てる inline script を返す。要求ごとの nonce だけを許して CSP を緩めない。
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        context.Items[scalarCspNonceKey] = nonce;
        csp = csp.Replace(
            "script-src 'self'",
            $"script-src 'self' 'nonce-{nonce}'",
            StringComparison.Ordinal);
    }

    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
    // **2 要素の QR は data: URI で描く。** 外部から画像を取りに行かせない
    headers["Content-Security-Policy"] = csp;
    await next();
});

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// 回答画面（TypeScript + Vite + Svelte のビルド成果物）
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapFormEndpoints();
app.MapAnalyticsEndpoints();
app.MapAdminAuthEndpoints();
app.MapAdminSessionEndpoints();
// **経路は常に登録し、無効な間は各入口が 404 にする。**
// ⚠️ **元は「有効なときだけ生やす」だった**（Issue #166。使わない構成で
// 認証の外の口を開けたままにしないため）が、**管理画面から設定を変えられるように
// した**ので、起動時に決めると変更のたびに再起動が要る。
// **無効な間は各入口が 404 を返すことで、外から見た姿は変わらない**
app.MapAdminSamlEndpoints();
app.MapAdminUserEndpoints();
app.MapAdminSurveyEndpoints();
app.MapAdminNoteEndpoints();
app.MapAdminAutoReplyEndpoints();
app.MapAdminTemplateEndpoints();
app.MapAdminAuditLogEndpoints();
app.MapAdminOutboxEndpoints();
app.MapAdminNotificationEndpoints();
app.MapAdminVersionEndpoints(transportSecurity.AllowInsecure);
if (monitoringToken is not null)
{
    app.MapMonitoringEndpoints(monitoringToken);
}

if (openApiExposure.Enabled)
{
    app.MapOpenApi();
    app.MapScalarApiReference((options, context) =>
    {
        // CDN の既定フォントと利用状況テレメトリーを止め、画面から第三者へ要求を出さない。
        options.DisableDefaultFonts().DisableTelemetry().DisableAgent().WithNonce(
            context.Items[scalarCspNonceKey] as string
            ?? throw new InvalidOperationException("Scalar の CSP nonce を設定できなかった"));
    });
}

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
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
    .WithTags("生存確認")
    .DisableRateLimiting();

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
})
    .WithTags("生存確認")
    .DisableRateLimiting();

app.Run();

/// <summary>結合テストから参照するための入口。</summary>
public partial class Program;
