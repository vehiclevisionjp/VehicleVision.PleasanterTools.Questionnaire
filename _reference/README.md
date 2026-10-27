# _reference（参考専用・AGPL）

Pleasanter 本体（**GNU AGPL v3**）は、AI（Claude・Copilot・Codex）の事実確認専用のソース。
本リポジトリでは追跡せず、必要なときに上流 `main` の先頭を取得する。
CI は参照ソースを使わず、checkout の `submodules: false` も維持する。

- リポジトリ: <https://github.com/Implem/Implem.Pleasanter>
- 既定の取得先: `_reference/Implem.Pleasanter/`（Git 管理外）
- 共有取得先: 環境変数 `PLEASANTER_REFERENCE_DIR`（その場所を直接読む）
- この `README.md` は引き続き Git で追跡する

## 厳守事項

- **コードをコピー・流用・リンク・プロジェクト参照しない。** 本アプリは Pleasanter 本体を
  改造せず、**標準 API を叩く別アプリ**として実装する
- ここは **API 仕様・サイト設定のスキーマ・列定義・挙動の事実確認** にのみ使う
- ビルド対象・配布物に含めない（ソリューションファイルにも入れない）
- 取得した本体の作業ツリーを編集しないこと。本リポジトリへのコミット対象でもない

> 本アプリは Pleasanter を**ネットワーク越しに API 利用するだけ**なので、本体の派生著作物には
> ならない。**本体のコードを取り込んだ瞬間にこの前提が崩れる。**
>
> **本製品はデュアルライセンス（AGPL-3.0-or-later OR 商用）で提供する**（[`../LICENSING.md`](../LICENSING.md)）。
> Pleasanter の著作権は当社に無いため、**取り込むと商用ライセンスでの提供ができなくなる。**
> 「どうせ AGPL 側があるのだから取り込んでよい」ではない。**デュアルの片方が壊れる。**

## 取得と更新

読む前に毎回実行する。Git と bash が必要で、Windows では Git Bash を使う。

```bash
bash scripts/sync-reference.sh

# worktree 間で共有するときは絶対パスを推奨する
export PLEASANTER_REFERENCE_DIR='D:\references\Implem.Pleasanter'
bash scripts/sync-reference.sh
```

PowerShell からも指定できる。

```powershell
$env:PLEASANTER_REFERENCE_DIR = 'D:\references\Implem.Pleasanter'
bash scripts/sync-reference.sh
```

未取得なら `git clone --depth 1`、既存なら `git fetch --depth 1 origin main` と
`git checkout --detach FETCH_HEAD` で更新する。
出力の Path・SHA・FullSHA・Date・Status・取得日時（UTC）・Version を確認する。
Version を読めない場合は警告と `Version=unknown` を出し、版を推測しない。

変更済み・ステージ済み・未追跡ファイルがある作業ツリー、origin 不一致、
別リポジトリや非空の取得先では停止する。通信失敗は失敗コードで終了し、既存の HEAD とファイルを維持する。
新規 clone は一時取得先で成功してから配置する。並行取得はロックで止める。
異常終了で `.sync-lock` が残った場合は、実行中の同期が無いことを確認してから空ディレクトリを取り除く。

共有先指定時は `_reference/Implem.Pleasanter` にジャンクションやシンボリックリンクを作らない。
相対パスはリポジトリ直下を基準にする。共有先を他の AI が読んでいる間は更新しない。
このアプリには `references.json` を置かず、本体 `main` の 1 件だけを取得する。

## 参照ソースで確認できること（過去の確認記録）

以下は従来の `Pleasanter_1.5.8.1` の確認記録で、今回最新ソースで再確認した結果ではない。
新たな調査時は同期後の完全な SHA・固定リンク・参照日を別途記録する。

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
> **`1.5.8.1` では含まれている。** 同リポジトリの記述は古い可能性がある。

## 確認できないこと

- **実際に API が返す JSON の中身。** 例えば `Column.ChoiceHash`（解決済みの選択肢）は
  `[NonSerialized]` が付いており、API 応答に載るかはソースだけでは確定できない。
  **実機の応答で確認すること**
- 実運用のテナント設定・権限・拡張項目の有効化状態

## 事実の記録と再現性

日次更新 workflow と gitlink を廃止し、再現性の根拠を文書に残す確認済み SHA へ移す。
既存の日次更新ブランチ・Draft PR・Issue の整理は別作業とし、この移行では触らない。

- 文書には**確認した完全な SHA・該当ソースへの固定リンク・参照日**を併記する。
  再確認していない過去の根拠を最新値へ置き換えない。実機未検証ならその旨を残す
- Pleasanter 更新時は API 仕様・サイト設定スキーマ・列定義・ランタイム版数を確認し、
  必要な対応は**自前実装側に行う**（本体コードは持ち込まない）
- **`main` はリリース前の変更を含む。** 「どの版の事実か」は、**確認したコミットと `<Version>`**
  （`Implem.Pleasanter/Implem.Pleasanter.csproj`）で言う。リリース版との対応は `Pleasanter_*` タグで確かめる

```bash
# 同期結果の FullSHA を根拠として記録する
bash scripts/sync-reference.sh
# どのリリースタグと同じコミットか
git ls-remote --tags https://github.com/Implem/Implem.Pleasanter.git 'refs/tags/Pleasanter_*^{}'
```

検証環境の公式イメージ `implem/pleasanter:1.5.8.1` は取得先と独立して固定し、自動更新しない。
ZIP は Web プロジェクトの発行結果、コンテナは Dockerfile の明示した対象だけを含める。
既定先・共有先の本体と Git メタデータをビルド・配布物へ含めない。
既存の `license-check.sh` と、取得先を検査する `reference-isolation-check.sh` を維持する。
隔離検査は既存 CI と同じ Python を使い、XML のコメントや除外設定と取り込みを区別する。

配布元（2026-10-27 参照）:

- Template 承認済み SHA: `3312f003fbe9827090e20bc643800824bdf30e22`
- [配布手順][distribution]
- 決定: vehiclevisionjp/VehicleVision.PleasanterTools.Template#61・
  vehiclevisionjp/VehicleVision.PleasanterTools.Template#63
- [git clone](https://git-scm.com/docs/git-clone) /
  [git fetch](https://git-scm.com/docs/git-fetch)

[distribution]: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/blob/3312f003fbe9827090e20bc643800824bdf30e22/_documents/参照ソースのオンデマンド取得への配布手順.md
