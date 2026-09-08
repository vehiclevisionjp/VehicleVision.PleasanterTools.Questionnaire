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

| ファイル | 内容 |
|---|---|
| `Service.json` | アプリ名・既定タイムゾーン |
| `Pleasanter.json` | 接続先 Pleasanter の URL・API キー・タイムアウト |

## 環境変数

| 変数 | 内容 |
|---|---|
| `QUESTIONNAIRE_DB_PROVIDER` | `SqlServer` / `PostgreSql` / `MySql` |
| `QUESTIONNAIRE_DB_CONNECTIONSTRING` | 本アプリの DB への接続文字列 |
| `QUESTIONNAIRE_PLEASANTER_BASEURL` | 接続先 Pleasanter の URL |
| `QUESTIONNAIRE_PLEASANTER_APIKEY` | Pleasanter の API キー |
| `QUESTIONNAIRE_PLEASANTER_TIMEZONE` | API キーに紐づくユーザのタイムゾーン |
| `QUESTIONNAIRE_SECRET_KEY` | 管理者の 2 要素の共有鍵を守る鍵（Base64・32 バイト）。**送信チケットの署名鍵もここから派生させる** |
| `QUESTIONNAIRE_DATA_PROTECTION_KEYS_PATH` | 複数インスタンスで管理画面の Cookie を共有する鍵束ディレクトリ。AKS では ReadWriteMany の永続ボリュームを指定する |
| `QUESTIONNAIRE_FORWARDED_NETWORKS` | `X-Forwarded-*` を信頼するリバースプロキシの CIDR。複数はカンマ区切り。Ingress の送信元範囲だけを指定する |
| `QUESTIONNAIRE_ADMIN_TWOFACTOR` | 管理者の 2 要素認証。`required` / `optional`（既定） / `disabled`。**知らない値は起動時に落ちる。** ⚠️ `disabled` にしても、登録済みの管理者からは 2 要素を外さない |
| `QUESTIONNAIRE_BOT_MITIGATION` | `off` で bot 対策を切る。**検証環境のためだけ。本番で切らないこと** |
| `QUESTIONNAIRE_SUBMIT_MIN_SECONDS` | 送信チケットの発行から送信までの最短時間（秒・既定 3） |
| `QUESTIONNAIRE_SUBMIT_TICKET_HOURS` | 送信チケットの有効期間（時間・既定 24） |
| `QUESTIONNAIRE_SUBMITS_PER_MIN` | 送信元 IP ごとの回答送信の上限（1 分あたり・既定 20） |
| `QUESTIONNAIRE_ALTCHA_ENABLED` | `false` で proof-of-work を切る。**アプリ全体。検証環境のためだけ** |
| `QUESTIONNAIRE_ALTCHA_MIN_NUMBER` | 探させる数の下限（既定 50000）。**大きいほど回答者の待ち時間も伸びる** |
| `QUESTIONNAIRE_ALTCHA_MAX_NUMBER` | 探させる数の上限（既定 150000） |
| `QUESTIONNAIRE_ATTACHMENT_*` | 添付の許可拡張子・サイズ・個数の上限 |
| `QUESTIONNAIRE_VIRUSSCAN_*` | ウイルススキャン（**既定は無効**） |

**proof-of-work の要否はアンケートごとにも切り替えられる**（管理画面の公開設定。Issue #66）。
**どちらも有効なときだけ課す**ので、ここで切るとアンケート側の設定に関わらず課さない。

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

## タイムゾーンに注意

**3 つのタイムゾーンが別々に存在し得る。** 詳細は
[`_documents/アーキテクチャ方針.md`](../../_documents/アーキテクチャ方針.md) の「タイムゾーン」を参照。

1. 本アプリの実行環境（Azure App Service は**既定 UTC**）
2. API キーに紐づく Pleasanter ユーザの `TimeZone`
3. 回答者のタイムゾーン
