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

## タイムゾーンに注意

**3 つのタイムゾーンが別々に存在し得る。** 詳細は
[`_documents/アーキテクチャ方針.md`](../../_documents/アーキテクチャ方針.md) の「タイムゾーン」を参照。

1. 本アプリの実行環境（Azure App Service は**既定 UTC**）
2. API キーに紐づく Pleasanter ユーザの `TimeZone`
3. 回答者のタイムゾーン
