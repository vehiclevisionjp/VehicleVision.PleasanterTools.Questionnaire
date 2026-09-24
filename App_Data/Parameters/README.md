# Parameters（設定ファイル）

**Pleasanter 本体と同じ方式**で、設定は `App_Data/Parameters/*.json` に置く
（参照: `_reference/Implem.Pleasanter/Implem.Pleasanter/App_Data/Parameters/`、
`_reference/Implem.Pleasanter/Implem.ParameterAccessor/Parameters.cs`）。

## 規約

- **キーの説明は、同じ階層に `"//キー名"` の項目を置いて書く。** Pleasanter 本体および
  `EizoGiken.PleasanterTools.McpServer` と同じ書き方
- **資格情報（API キー・接続文字列）の実値をこのフォルダへコミットしないこと。**
  実値は次のいずれかで与える
    1. 環境変数（Azure App Service のアプリケーション設定）
    2. Azure Key Vault
    3. `{名前}.local.json`（`.gitignore` 済み。ローカル開発用）
- 上書きの優先順位は **`{名前}.local.json` ＞ 環境変数 ＞ `{名前}.json`** とする

## ファイル

| ファイル | 内容 | 読まれているか |
|---|---|---|
| `Service.json` | 既定タイムゾーン | **読まれている** |
| `Pleasanter.json` | 接続先 Pleasanter の URL・API キー・版・タイムアウト | **読まれている** |
| `Security.json` | **管理者のパスワードに求める条件**（Issue #157） | **読まれている** |
| `Analytics.json` | **回答画面のアクセス解析**（Issue #162） | **読まれている** |

**読み込みは 1 か所**（`Web/Services/ParameterFiles.cs`）。アプリの一番先で積むので、
DB・Pleasanter・管理者の認証・アクセス解析のすべてがこれを見る。

> ⚠️ **`Service.json` の `Name` と `Description` はどこからも読んでいない。**
> 表示名を出す画面が無いため。消してはいないが、変えても何も変わらない。

### ファイルのキーと環境変数の対応

**`Pleasanter.json` と `Service.json` は、キーの名前が環境変数と違う**
（Pleasanter 本体に合わせた短い名前を使っているため）。
**読み込み時に下表のとおり写している**ので、どちらで書いても効く。

| ファイルのキー | 環境変数 |
|---|---|
| `Service.json` の `TimeZoneDefault` | `QUESTIONNAIRE_TIMEZONE_DEFAULT` |
| `Pleasanter.json` の `BaseUrl` | `QUESTIONNAIRE_PLEASANTER_BASEURL` |
| `Pleasanter.json` の `ApiKey` | `QUESTIONNAIRE_PLEASANTER_APIKEY` |
| `Pleasanter.json` の `ApiVersion` | `QUESTIONNAIRE_PLEASANTER_APIVERSION` |
| `Pleasanter.json` の `TimeoutSeconds` | `QUESTIONNAIRE_PLEASANTER_TIMEOUTSECONDS` |
| `Pleasanter.json` の `ApiKeyUserTimeZoneId` | `QUESTIONNAIRE_PLEASANTER_TIMEZONE` |

`Security.json` と `Analytics.json` は環境変数と同じ名前なので、写していない。

> ⚠️ **値が `null` や空のキーは無視する。** `Pleasanter.json` の `ApiKey` は既定で `null`
> （ここへ書かせないため）なので、写すと**環境変数で与えた API キーを空で塗り潰してしまう。**

## 環境変数

| 変数 | 内容 |
|---|---|
| `QUESTIONNAIRE_DB_PROVIDER` | `SqlServer` / `PostgreSql` / `MySql` / `Sqlite`。`Sqlite` は簡易セットアップ・デバッグ専用 |
| `QUESTIONNAIRE_DB_CONNECTIONSTRING` | 本アプリの DB への接続文字列。`Sqlite` だけは省略でき、`App_Data/questionnaire.db` を使う |
| `QUESTIONNAIRE_PLEASANTER_BASEURL` | 接続先 Pleasanter の URL |
| `QUESTIONNAIRE_PLEASANTER_APIKEY` | Pleasanter の API キー |
| `QUESTIONNAIRE_PLEASANTER_APIVERSION` | Pleasanter の API バージョン（既定 1.1）。**読めない値は既定へ落とす** |
| `QUESTIONNAIRE_PLEASANTER_TIMEOUTSECONDS` | Pleasanter API 呼び出しのタイムアウト（秒・既定 30） |
| `QUESTIONNAIRE_PLEASANTER_TIMEZONE` | API キーに紐づくユーザのタイムゾーン。**未設定なら `QUESTIONNAIRE_TIMEZONE_DEFAULT`** |
| `QUESTIONNAIRE_TIMEZONE_DEFAULT` | 本アプリの既定タイムゾーン（既定 `Asia/Tokyo`）。`Service.json` の `TimeZoneDefault` と同じ |
| `QUESTIONNAIRE_SECRET_KEY` | 管理者の 2 要素の共有鍵を守る鍵（Base64・32 バイト）。**送信チケットの署名鍵もここから派生させる** |
| `QUESTIONNAIRE_DATA_PROTECTION_KEYS_PATH` | 複数インスタンスで管理画面の Cookie を共有する鍵束ディレクトリ。AKS では ReadWriteMany の永続ボリュームを指定する |
| `QUESTIONNAIRE_ADMIN_SESSION_STORE` | 管理者セッションの保存先。`Database`（既定）/ `Redis`。⚠️ Redis 停止時は全管理者をログアウト扱いにする |
| `QUESTIONNAIRE_ADMIN_SESSION_REDIS_CONNECTIONSTRING` | 管理者セッションまたは共有状態で `Redis` を選んだときの接続文字列。両方で同じ接続を使う。資格情報を含むため環境変数か Key Vault だけで与える |
| `QUESTIONNAIRE_ADMIN_SESSION_REDIS_PREFIX` | Redis キーの接頭辞。既定 `questionnaire:admin-session:`。同じ Redis を複数環境で共用するときに分ける |
| `QUESTIONNAIRE_SHARED_STATE_STORE` | レート制限と滞留の見張りの保存先。`Process`（既定）/ `Redis`。⚠️ 未設定時はプロセスごとに数える |
| `QUESTIONNAIRE_SHARED_STATE_REDIS_PREFIX` | 共有状態の Redis キー接頭辞。既定 `questionnaire:shared-state:`。同じ Redis を複数環境で共用するときに分ける |
| `QUESTIONNAIRE_ASSET_STORE` | 資産の保存先。`Database`（既定）/ `Path` / `AzureBlob` / `S3` |
| `QUESTIONNAIRE_ASSET_PATH` | `Path` の保存ディレクトリ。複数インスタンスでは全インスタンスが同じ共有領域を参照する |
| `QUESTIONNAIRE_ASSET_AZURE_CONTAINERURI` | `AzureBlob` のコンテナー URI。マネージド ID を使う既定の指定方法 |
| `QUESTIONNAIRE_ASSET_AZURE_CONNECTIONSTRING` | `AzureBlob` の接続文字列。鍵を保管するため非推奨。環境変数か Key Vault だけで与える |
| `QUESTIONNAIRE_ASSET_AZURE_CONTAINERNAME` | 接続文字列を使う場合のコンテナー名 |
| `QUESTIONNAIRE_ASSET_S3_BUCKET` | `S3` のバケット名 |
| `QUESTIONNAIRE_ASSET_S3_SERVICEURL` | S3 互換サービスの入口 URL。AWS S3 では省略できる |
| `QUESTIONNAIRE_ASSET_S3_FORCEPATHSTYLE` | パス形式のアドレスを使うか。MinIO など必要な環境で `true` |
| `QUESTIONNAIRE_ASSET_S3_REGION` | S3 の署名に使う region。任意の region 名を指定できる |
| `QUESTIONNAIRE_ASSET_S3_ACCESSKEY` | S3 のアクセスキー。IAM ロールを使えない場合だけ環境変数か Key Vault で与える |
| `QUESTIONNAIRE_ASSET_S3_SECRETKEY` | S3 の秘密鍵。アクセスキーと組で指定する |
| `QUESTIONNAIRE_ASSET_ALLOWEDEXTENSIONS` | 説明文・完了画面で配る資産の許可拡張子。回答添付とは別設定 |
| `QUESTIONNAIRE_ASSET_MAXFILESIZEBYTES` | 配布資産 1 件の上限。既定 `10485760`（10 MB） |
| `QUESTIONNAIRE_ASSET_MAXFILECOUNT` | アンケート 1 件の配布資産数。既定 `20` |
| `QUESTIONNAIRE_FORWARDED_NETWORKS` | `X-Forwarded-*` を信頼するリバースプロキシの CIDR。複数はカンマ区切り。Ingress の送信元範囲だけを指定する |
| `QUESTIONNAIRE_MONITORING_TOKEN` | 監視 API の Bearer token。**未設定なら監視 API の経路自体を作らない。** 環境変数か Key Vault から与える |
| `QUESTIONNAIRE_HEALTH_NETWORKS` | `/healthz` と `/ready` を許す送信元 CIDR。複数はカンマ区切り。**未設定なら制限しない** |
| `QUESTIONNAIRE_MONITORING_NETWORKS` | 監視 API を許す送信元 CIDR。複数はカンマ区切り。`inherit` なら `QUESTIONNAIRE_HEALTH_NETWORKS` を引き継ぐ。**未設定なら制限しない** |
| `QUESTIONNAIRE_OPENAPI_ENABLED` | `true` で `/openapi/v1.json` を公開する。**既定は `false`** |
| `QUESTIONNAIRE_OPENAPI_NETWORKS` | OpenAPI 文書を許す送信元 CIDR。複数はカンマ区切り。`inherit` なら `QUESTIONNAIRE_HEALTH_NETWORKS` を引き継ぐ。**未設定なら制限しない** |
| `QUESTIONNAIRE_ADMIN_TWOFACTOR` | 管理者の 2 要素認証。`required` / `optional`（既定） / `disabled`。**知らない値は起動時に落ちる。** ⚠️ `disabled` にしても、登録済みの管理者からは 2 要素を外さない |
| `PasswordMinimumLength` | パスワードの最低の長さ（既定 12）。`Security.json` にも書ける（Issue #157） |
| `PasswordAllowSameAsLoginId` | ログイン ID と同じパスワードを許すか（既定 `false`）。同上 |
| `AnalyticsProvider` | アクセス解析のサービス。`None`（既定）/ `Ga4` / `Gtm` / `Matomo` / `Plausible`。`Analytics.json` にも書ける |
| `AnalyticsSiteId` | 測定 ID・コンテナ ID・サイト ID・ドメイン（サービスで意味が変わる） |
| `AnalyticsScriptOrigin` | 自前設置の配信元。**Matomo は必須** |
| `AnalyticsShowNotice` | 回答者へ告知を出すか（既定 `true`） |
| `CaptchaProvider` | 課す課題。`Altcha`（既定・自前設置）/ `Recaptcha` / `Turnstile` / `Hcaptcha`。**知らない値は既定へ落とす** |
| `CaptchaSiteKey` | 外部の CAPTCHA のサイトキー（画面へ渡る） |
| `CaptchaSecretKey` | 外部の CAPTCHA の秘密鍵。**このフォルダのファイルへ書かないこと。** 環境変数か Key Vault から |
| `QUESTIONNAIRE_SAML_ENABLED` | `true` で SAML のログインを使う。**既定は無効** |
| `QUESTIONNAIRE_SAML_ENTITYID` | 本アプリ（SP）の EntityID。IdP へ登録する値と同じにする |
| `QUESTIONNAIRE_SAML_IDPENTITYID` | IdP の EntityID。**これ以外が発行した応答は受け取らない** |
| `QUESTIONNAIRE_SAML_SINGLESIGNONURL` | IdP のログインの窓口（`AuthnRequest` の宛先） |
| `QUESTIONNAIRE_SAML_IDPCERTIFICATE` | IdP の署名証明書（PEM か、DER の base64）。**入れ替えの最中は改行かカンマで 2 枚並べられる** |
| `QUESTIONNAIRE_SAML_UNKNOWNUSER` | 本アプリに居ない利用者の扱い。`Reject`（既定・通さない）/ `Register`（その場で作る） |
| `QUESTIONNAIRE_SAML_REGISTERROLE` | `Register` で作る利用者の役割。`Editor`（既定）/ `Administrator`。**⚠️ Administrator にすると IdP に居る全員が全権を持つ** |
| `QUESTIONNAIRE_SAML_LOGINIDSOURCE` | ログイン ID の取り出し先。`NameId`（既定）/ `Claim` |
| `QUESTIONNAIRE_SAML_LOGINIDCLAIM` | `LOGINIDSOURCE=Claim` のときに読む属性名 |
| `QUESTIONNAIRE_SAML_BUTTONLABEL` | ログイン画面の釦に出す文字（省略時は「シングルサインオンでログイン」） |
| `QUESTIONNAIRE_BOT_MITIGATION` | `off` で bot 対策を切る。**検証環境のためだけ。本番で切らないこと** |
| `QUESTIONNAIRE_SUBMIT_MIN_SECONDS` | 送信チケットの発行から送信までの最短時間（秒・既定 3） |
| `QUESTIONNAIRE_SUBMIT_TICKET_HOURS` | 送信チケットの有効期間（時間・既定 24） |
| `QUESTIONNAIRE_REQUESTS_PER_MIN` | 送信元 IP ごとの要求上限（1 分あたり・既定 60） |
| `QUESTIONNAIRE_SUBMITS_PER_MIN` | 送信元 IP ごとの回答送信の上限（1 分あたり・既定 20） |
| `QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN` | 送信元 IP ごとのログイン試行上限（5 分あたり・既定 10）。**検証環境向けの変更口であり、本番で緩めない** |
| `QUESTIONNAIRE_ADMIN_PASSWORD_SIGNIN` | SAML 有効時に合言葉ログインを許可するか（既定 `true`）。`false` は画面と API の両方を塞ぐ。SAML 無効時は締め出し防止のため警告付きで許可する |
| `QUESTIONNAIRE_ADMIN_RESCUE_TOKEN` | 合言葉を塞いだときの救済トークン（未設定なら逃げ道なし、32 文字以上）。**秘密管理基盤から外部設定として与える** |
| `QUESTIONNAIRE_ALTCHA_ENABLED` | `false` で proof-of-work を切る。**アプリ全体。検証環境のためだけ** |
| `QUESTIONNAIRE_ALTCHA_MIN_NUMBER` | 探させる数の下限（既定 50000）。**大きいほど回答者の待ち時間も伸びる** |
| `QUESTIONNAIRE_ALTCHA_MAX_NUMBER` | 探させる数の上限（既定 150000） |
| `QUESTIONNAIRE_LOGIN_PROOF_OF_WORK` | `true` で管理画面のパスワードログインと招待受取へ proof-of-work を課す（既定 `false`） |
| `QUESTIONNAIRE_ATTACHMENT_*` | 添付の許可拡張子・サイズ・個数の上限 |
| `QUESTIONNAIRE_VIRUSSCAN_*` | ウイルススキャン（**既定は無効**） |

保持日数・流量・添付上限・スクリプト上限・各種タイムアウトは管理画面からも変更できる。
外部設定で指定した項目は管理画面では固定され、外部設定が優先される。
数値の上限・下限と現在の既定値は管理画面に表示され、明示的な 0 や空値は受け付けない。
`QUESTIONNAIRE_DEADLETTER_RETENTION_DAYS` だけは、未設定時の既定値 0 を
「無期限」として維持するが、画面・外部設定から 0 を再設定することはできない。

**proof-of-work の要否はアンケートごとにも切り替えられる**（管理画面の公開設定。Issue #66）。
**どちらも有効なときだけ課す**ので、ここで切るとアンケート側の設定に関わらず課さない。

### 複数プロセスのレート制限

⚠️ **`QUESTIONNAIRE_SHARED_STATE_STORE=Redis` を設定せずに複数プロセスで動かす場合は、
`QUESTIONNAIRE_REQUESTS_PER_MIN`、`QUESTIONNAIRE_SUBMITS_PER_MIN`、
`QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN` を、それぞれプロセス数で割った値にすること。**
プロセス内のレート制限は互いの回数を見ないため、設定値が同じなら実効上限はプロセス数倍になる。

Redis で共有する場合は `QUESTIONNAIRE_SHARED_STATE_STORE=Redis` と
`QUESTIONNAIRE_ADMIN_SESSION_REDIS_CONNECTIONSTRING` を設定する。
管理者セッションでも Redis を使う場合は同じ接続と接続オブジェクトへ相乗りし、
接続設定を二重には持たない。Redis に接続できない間は要求を全部通すのではなく、
各プロセス内のレート制限と滞留の見張りへ切り替え、警告をログへ出す。

⚠️ **`QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN` は結合試験が枠を使い切らないための逃げ道でもある。**
本番へ反映する前に、検証環境の大きな値を持ち込んでいないことを確認する。

添付とウイルススキャンの項目は
[`_documents/添付ファイル検査-運用手順書.md`](../../_documents/添付ファイル検査-運用手順書.md)
5 章に一覧がある。**ここへ書き写さないこと。**

### `QUESTIONNAIRE_SECRET_KEY` について

**失うと、登録済みの 2 要素が全て使えなくなる。**
管理者は復旧コードで入り、2 要素を登録し直すことになる。

- **Key Vault に置き、控えを取っておくこと**
- 値は次で作れる

```
dotnet run --project src/VehicleVision.PleasanterTools.Questionnaire.Web -- --generate-secret-key
```

または任意の手段で 32 バイトの乱数を Base64 にする。

**この鍵と ASP.NET Core Data Protection の鍵束は役割が異なる。**
`QUESTIONNAIRE_SECRET_KEY` は 2 要素の共有鍵と送信チケットに使い、運用者が保管する。
Data Protection は管理画面の Cookie に使う。AKS の複数 Pod では
`QUESTIONNAIRE_DATA_PROTECTION_KEYS_PATH` を ReadWriteMany の永続ボリュームへ向け、
Pod 間で鍵束を共有する。

管理者の cookie はセッション ID だけを持つが、その ID の改ざん防止にも Data Protection を使う。
**セッションストアを Redis にしても、鍵束の共有はやめられない。**

### 管理者セッションストア

既定の `Database` は本アプリの DB に `AdminSessions` を作る。追加のサービスは要らない。
大規模構成では `Redis` を選べる。

Redis はセッション専用の領域を使い、`maxmemory-policy` は `noeviction` にする。
**メモリ不足時にキーを個別に追い出す設定では、端末一覧の索引とセッション本体の片方だけが
消え得るため使用しない。** 書き込みは失敗してログインを成立させない方が安全である。

⚠️ **Redis を選ぶことは、Redis 停止時に管理画面の全員がログアウト扱いになることを
受け入れる選択である。** cookie だけで通す代替動作はしない。失効が効かない状態で
管理操作を許す方が危険なため。

## タイムゾーンに注意

**3 つのタイムゾーンが別々に存在し得る。** 詳細は
[`_documents/アーキテクチャ方針.md`](../../_documents/アーキテクチャ方針.md) の「タイムゾーン」を参照。

1. 本アプリの実行環境（Azure App Service は**既定 UTC**）
2. API キーに紐づく Pleasanter ユーザの `TimeZone`
3. 回答者のタイムゾーン
