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
| `QUESTIONNAIRE_BOT_MITIGATION` | `off` で bot 対策を切る。**検証環境のためだけ。本番で切らないこと** |
| `QUESTIONNAIRE_SUBMIT_MIN_SECONDS` | 送信チケットの発行から送信までの最短時間（秒・既定 3） |
| `QUESTIONNAIRE_SUBMIT_TICKET_HOURS` | 送信チケットの有効期間（時間・既定 24） |
| `QUESTIONNAIRE_SUBMITS_PER_MIN` | 送信元 IP ごとの回答送信の上限（1 分あたり・既定 20） |
| `QUESTIONNAIRE_ATTACHMENT_*` | 添付の許可拡張子・サイズ・個数の上限 |
| `QUESTIONNAIRE_VIRUSSCAN_*` | ウイルススキャン（**既定は無効**） |

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

**Data Protection の鍵束は使っていない。**
App Service では鍵の保存先が既定で一時領域になり、
**再起動で鍵を失うと 2 要素が全部使えなくなる**ため、
運用者が持つ 1 本の鍵を設定から受け取る形にしている。

## タイムゾーンに注意

**3 つのタイムゾーンが別々に存在し得る。** 詳細は
[`_documents/アーキテクチャ方針.md`](../../_documents/アーキテクチャ方針.md) の「タイムゾーン」を参照。

1. 本アプリの実行環境（Azure App Service は**既定 UTC**）
2. API キーに紐づく Pleasanter ユーザの `TimeZone`
3. 回答者のタイムゾーン
