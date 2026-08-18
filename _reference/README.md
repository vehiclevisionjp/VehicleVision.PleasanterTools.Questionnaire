# _reference（参考専用・AGPL）

`Implem.Pleasanter` は Pleasanter 本体（**GNU AGPL v3**）を **参考目的でのみ** サブモジュールとして
配置したもの。固定コミットで参照する。

- リポジトリ: <https://github.com/Implem/Implem.Pleasanter>
- 固定位置: `Pleasanter_1.5.7.0`（`870a56ae` / 2026-08-12 時点の upstream 最新タグ）

## 厳守事項

- **コードをコピー・流用・リンク・プロジェクト参照しない。** 本アプリは Pleasanter 本体を
  改造せず、**標準 API を叩く別アプリ**として実装する
- ここは **API 仕様・サイト設定のスキーマ・列定義・挙動の事実確認** にのみ使う
- ビルド対象・配布物に含めない（ソリューションファイルにも入れない）
- `_reference/` 配下を編集しないこと。サブモジュールの作業ツリーであり、本リポジトリの
  コミットには乗らない

> 本アプリは Pleasanter を**ネットワーク越しに API 利用するだけ**なので、本体の派生著作物には
> ならない。**本体のコードを取り込んだ瞬間にこの前提が崩れる。**

## 取得

```bash
git submodule update --init --recursive
```

## この submodule で確認できること

`Pleasanter_1.5.7.0` の公開リポジトリには **Web アプリ本体のソースが含まれている**（実際に確認済み）。

| 対象 | 確認できること |
|---|---|
| `Implem.Pleasanter/Controllers/Api/ItemsController.cs` | 標準 API のエンドポイント一覧とルーティング |
| `Implem.Pleasanter/Libraries/Requests/Context.cs` | **API キーの受け渡し方式**（リクエストボディの `ApiKey`） |
| `Implem.Pleasanter/Libraries/Settings/Column.cs` | 列定義のプロパティ（`LabelText` / `ChoicesText` / `ValidateRequired` ほか） |
| `Implem.Pleasanter/Models/Sites/SiteApiModel.cs` | `GetSite` の応答に `SiteSettings` が含まれること |
| `Implem.Pleasanter/App_Data/Definitions/Definition_Column/*.json` | 列の型・桁 |
| `global.json` / 各 `*.csproj` | **本体のランタイム版数**（SDK 10.0.100 / net10.0） |
| `Implem.PleasanterFrontend/` | **本体のフロントエンド構成**（TypeScript + Vite + Svelte + SCSS） |

> 参考: `EizoGiken.PleasanterTools.McpServer` の `_reference/README.md` には
> 「公開リポジトリに Web アプリ本体のソースが含まれていない」とあるが、
> **`1.5.7.0` では含まれている。** 同リポジトリの記述は古い可能性がある。

## 確認できないこと

- **実際に API が返す JSON の中身。** 例えば `Column.ChoiceHash`（解決済みの選択肢）は
  `[NonSerialized]` が付いており、API 応答に載るかはソースだけでは確定できない。
  **実機の応答で確認すること**
- 実運用のテナント設定・権限・拡張項目の有効化状態

## バージョン追随

- 固定コミットで参照する。Pleasanter 更新時は差分（API 仕様・サイト設定スキーマ・列定義・
  ランタイム版数）を確認し、必要な対応は**自前実装側に取り込む**（本体コードは持ち込まない）

```bash
git -C _reference/Implem.Pleasanter fetch --tags origin
git -C _reference/Implem.Pleasanter tag --sort=-creatordate | head -5
```
