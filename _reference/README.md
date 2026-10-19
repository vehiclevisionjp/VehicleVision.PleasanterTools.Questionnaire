# _reference（参考専用・AGPL）

`Implem.Pleasanter` は Pleasanter 本体（**GNU AGPL v3**）を **参考目的でのみ** サブモジュールとして
配置したもの。**上流の `main` の先頭を追う**（2026-10-19 に、リリースタグへの固定から変えた）。コミットは固定して参照し、更新は自動 PR で進める。

- リポジトリ: <https://github.com/Implem/Implem.Pleasanter>
- 追従先: `main`（`.gitmodules` の `branch`）。**固定位置の正は、リポジトリが記録しているコミット**（`git ls-tree HEAD _reference/Implem.Pleasanter`）
- 固定位置（2026-10-19 時点）: `1b7b6e6c`（`Pleasanter_1.5.9.0` と同じコミット）

## 厳守事項

- **コードをコピー・流用・リンク・プロジェクト参照しない。** 本アプリは Pleasanter 本体を
  改造せず、**標準 API を叩く別アプリ**として実装する
- ここは **API 仕様・サイト設定のスキーマ・列定義・挙動の事実確認** にのみ使う
- ビルド対象・配布物に含めない（ソリューションファイルにも入れない）
- `_reference/` 配下を編集しないこと。サブモジュールの作業ツリーであり、本リポジトリの
  コミットには乗らない

> 本アプリは Pleasanter を**ネットワーク越しに API 利用するだけ**なので、本体の派生著作物には
> ならない。**本体のコードを取り込んだ瞬間にこの前提が崩れる。**
>
> **本製品はデュアルライセンス（AGPL-3.0-or-later OR 商用）で提供する**（[`../LICENSING.md`](../LICENSING.md)）。
> Pleasanter の著作権は当社に無いため、**取り込むと商用ライセンスでの提供ができなくなる。**
> 「どうせ AGPL 側があるのだから取り込んでよい」ではない。**デュアルの片方が壊れる。**

## 取得

```bash
git submodule update --init --recursive
git config submodule.recurse true
```

**`submodule.recurse` を設定しておくこと**（クローンごとに 1 回）。追従 PR がマージされるたびに親のポインタが進むが、
手元のサブモジュールの中身は、これが無いと古いまま残り、`pull` のあとに
`modified: _reference/Implem.Pleasanter (new commits)` と差分が出る。設定しておけば、`pull`・`switch` のたびに
サブモジュールの中身が、親が記録したコミットへ自動で合う。

- **`.gitmodules` の `ignore = all` で隠さないこと。** 差分は消えるが、**中身が古いまま**で、
  事実確認の根拠を取り違える（`git submodule status` の行頭に `+` が出る）
- 全リポジトリへ効かせたいときは `git config --global submodule.recurse true`。ただし、Manual のように
  サブモジュールが多いリポジトリでは、`pull` のたびにサブモジュール側の処理が走り、時間がかかることがある
  （この点は未検証）

## この submodule で確認できること

`Pleasanter_1.5.8.1` の公開リポジトリには **Web アプリ本体のソースが含まれている**（実際に確認済み）。

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

## バージョン追随

- **上流の `main` の先頭を追う。** コミットは固定して参照し、更新は日次の検査が出す追従 PR
  （`.github/workflows/reference-submodule-update.yml`）で進める。Pleasanter 更新時は差分
  （API 仕様・サイト設定スキーマ・列定義・ランタイム版数）を確認し、必要な対応は
  **自前実装側に取り込む**（本体コードは持ち込まない）
- **`main` はリリース前の変更を含む。** 「どの版の事実か」は、**確認したコミットと `<Version>`**
  （`Implem.Pleasanter/Implem.Pleasanter.csproj`）で言う。リリース版との対応は `Pleasanter_*` タグで確かめる

```bash
# main の先頭へ進める（作業ツリーのポインタが変わる。コミットは追従 PR に任せる）
git submodule update --init --remote --depth 1 _reference/Implem.Pleasanter
git -C _reference/Implem.Pleasanter log -1 --format='%h %ad' --date=short
# どのリリースタグと同じコミットか
git ls-remote --tags https://github.com/Implem/Implem.Pleasanter.git 'refs/tags/Pleasanter_*^{}'
```
