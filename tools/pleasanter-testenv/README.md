# Pleasanter 検証環境

**ソースだけでは確定できない事項を実機で潰すための使い捨て環境。**
Pleasanter 本体は**公式イメージを起動するだけ**で、ソースの参照・リンクはしない
（AGPL 非汚染の前提。[`_reference/README.md`](../../_reference/README.md)）。

得られた結果は [`_documents/実機検証結果.md`](../../_documents/実機検証結果.md)。

> **開発環境ひとそろい（Pleasanter ＋ 3 RDBMS ＋ 本アプリ）はリポジトリ直下の
> [`compose.yaml`](../../compose.yaml) にある。** こちらは **Pleasanter 単体の検証専用**。
> **同時に起動しないこと。ポートが衝突する。**

## 構成

| サービス | イメージ | 役割 |
|---|---|---|
| `db` | `mcr.microsoft.com/mssql/server:2025-CU8-ubuntu-24.04` | SQL Server 2025 |
| `codedefiner` | `implem/pleasanter:codedefiner` | スキーマ作成 ＋ 90 日トライアル登録 |
| `pleasanter` | `implem/pleasanter:1.5.7.0` | 本体。サブモジュールの固定版に合わせる |
| `verify` | `python:3.13-slim` | 検証の実行役。**ホストに python も curl も要求しない** |

## 使い方

```bash
cd tools/pleasanter-testenv
docker compose up -d --wait          # 起動（初回はイメージ取得で数分）
docker compose cp seed/01_apikey.sql db:/tmp/ \
  && docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd \
       -S localhost -U sa -P 'Questionnaire#Test1' -C -b -i /tmp/01_apikey.sql
docker compose restart pleasanter    # 利用者キャッシュを捨てる
docker compose run --rm verify       # 検証を実行（結果は ./results/）
docker compose down -v               # 後片付け（DB ごと破棄）
```

画面を見たいときは <http://localhost:8080>（`Administrator` / `pleasanter`）。

**Windows の Git Bash から実行する場合は `MSYS_NO_PATHCONV=1` を付ける。**
付けないと `/opt/...` などのパスが Windows パスへ変換されて失敗する。

## 変数

**変数名には必ず `TESTENV_` 接頭辞を付けてある。** 素の `PLEASANTER_API_KEY` のような
名前だと、**ホスト環境に同名の変数があったときに compose の既定値が黙って上書きされる**
（実際に踏んで原因究明に時間を使った）。

| 変数 | 既定 |
|---|---|
| `TESTENV_SA_PASSWORD` | `Questionnaire#Test1` |
| `TESTENV_DB_PORT` | `11433` |
| `TESTENV_PLEASANTER_PORT` | `8080` |
| `TESTENV_API_KEY` | `seed/01_apikey.sql` が設定する固定値 |

## 踏んだ落とし穴

### `Dbms` は環境変数で設定できない

公式イメージの `App_Data/Parameters/Rds.json` は **`Dbms: "PostgreSQL"` を焼き込んでいる。**
`Implem.Pleasanter_Rds_Dbms` を渡しても無視される
（`Implem.DefinitionAccessor/Initializer.cs` は**接続文字列しか**環境変数から読まない）。

→ [`Parameters/Rds.json`](Parameters/Rds.json) をマウントして差し替える。
**マウント先の階層が本体と CodeDefiner で違う**ので注意。

| コンテナ | マウント先 |
|---|---|
| `pleasanter` | `/app/App_Data/Parameters/Rds.json` |
| `codedefiner` | `/app/Implem.Pleasanter/App_Data/Parameters/Rds.json` |

### CodeDefiner は失敗しても終了コード 0

接続失敗で全処理が落ちても `exited 0` を返す。
`depends_on: condition: service_completed_successfully` は**素通りする。**

→ **必ずログを確認すること。**

```bash
docker compose logs codedefiner | grep -iE "error|success"
```

`Full-Text Search is not installed` のエラーは、SQL Server コンテナに全文検索が
入っていないため。**今回の検証には影響しない。**

### API キーは DB へ直接入れている

Pleasanter は `Users.ApiKey` を**そのまま比較する**ため、任意の値を入れれば通る
（`seed/01_apikey.sql`）。UI からキーを発行する手間を省くための検証環境専用の手段。
**本番の Pleasanter に対して実行しないこと。**

**キーを変えたら `pleasanter` を再起動すること。** 利用者情報がキャッシュされている。

## タイムゾーンを確かめ直すとき

**Pleasanter は「サーバの OS ローカル時刻」で DB に書き、「API キー保有ユーザの
`TimeZone`」で返す。** 同じレコードが、読む側の設定で違う時刻に見える。

```bash
# 利用者のタイムゾーンを変えて、同じレコードの見え方が変わることを確かめる
docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'Questionnaire#Test1' -C -b \
  -Q "USE [Implem.Pleasanter]; UPDATE Users SET TimeZone=N'Tokyo Standard Time' WHERE UserId=1;"
docker compose restart pleasanter
docker compose run --rm verify
```

DB の実値は次で見る。

```bash
docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'Questionnaire#Test1' -C -b \
  -Q "USE [Implem.Pleasanter]; SELECT ResultId, DateA, CreatedTime FROM Results;"
```
