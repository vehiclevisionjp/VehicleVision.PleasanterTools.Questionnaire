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
| `pleasanter` | `implem/pleasanter:1.5.8.1` | 本体。サブモジュールの固定版に合わせる |
| `verify` | `python:3.13-slim` | 検証の実行役。**ホストに python も curl も要求しない** |

## 使い方

```bash
cd tools/pleasanter-testenv
docker compose up -d --wait          # 起動（初回はイメージ取得で数分）
docker compose cp seed/01_apikey.sql db:/tmp/ \
  && docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd \
       -S localhost -U sa -P 'Questionnaire#Test1' -C -b -i /tmp/01_apikey.sql
# シングルサインオンの試験の利用者（Issue #470）。何度流してもよい
docker compose cp seed/03_sso_users.sql db:/tmp/ \
  && docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd \
       -S localhost -U sa -P 'Questionnaire#Test1' -C -b -i /tmp/03_sso_users.sql
docker compose restart pleasanter    # 利用者キャッシュを捨てる
docker compose run --rm verify       # 検証を実行（結果は ./results/）
docker compose down -v               # 後片付け（DB ごと破棄）
```

画面を見たいときは <http://localhost:8080>（`Administrator` / `pleasanter`）。

### シングルサインオンの試験の利用者

`seed/03_sso_users.sql` が作る（Issue #470）。**検証環境専用。**

| ログイン ID | パスワード | 2 要素 | 使う試験 |
|---|---|---|---|
| `sso-e2e-plain` | `SsoE2e#Plain1` | なし | 一般の利用者で入れる・ログアウトで締め出される |
| `sso-e2e-mail` | `SsoE2e#Mail1` | メールのワンタイムパスワード | パスワードだけでは入れず、コードの後に入れる |
| `sso-e2e-stranger` | `SsoE2e#Stranger1` | なし | 本アプリに居ない人は断られる（`Administrator` は作りたてだと初回にパスワードの変更を求められるため使わない） |

- **パスワードは SHA-512 の 16 進で DB へ直接入れている。** Pleasanter 1.5.8.1 は
  `Users_Password` を塩なしの SHA-512（`Sha512Cng()`）にして `Users.Password` と比べる
  （`Implem.Pleasanter/Models/Users/UserModel.cs` の `SetByForm`・`GetByCredentials`、
  `Implem.Libraries/Utilities/Encryptions.cs`）
- **`PasswordExpirationTime` を NULL にしている。** 値があると初回のログインで
  パスワードの変更を求められ、試験が先へ進めない（実測）
- **入れた直後からログインできる。** 利用者は DB から引かれるため、再起動は要らない
  （2026-09-25 実測）。API キーと違い、キャッシュに頼る所が無い
- **メールアドレスは登録していない。** 登録が無ければ Pleasanter はメールを送らず、
  コードを `Users.SecondaryAuthenticationCode` へ平文で残すだけになる
  （`UserModel.cs` の `UpdateSecondaryAuthenticationCode`・`NotificationSecondaryAuthenticationCode`）。
  **試験はここからコードを読む。** SMTP（`Mail.json`）を設定していないので、
  登録してもメールは届かない

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

### 2 要素は `Security.json` を丸ごと差し替えて有効にしている

**メールのワンタイムパスワードを、試験用の利用者 1 人にだけ掛けたい**（Issue #470）。
[`Parameters/Security.json`](Parameters/Security.json) をマウントしている。

| 項目 | 公式イメージ | ここ |
|---|---|---|
| `SecondaryAuthentication.Mode` | `None` | **`DefaultDisable`** |
| `SecondaryAuthentication.NotificationType` | `Mail` | `Mail`（既定のまま） |

- **`DefaultDisable` は、`Users.EnableSecondaryAuthentication` を立てた利用者にだけ効く**
  （`Implem.Pleasanter/Models/Users/UserModel.cs:5704-5723` の `EnabledSecondaryAuthentication`）。
  `Administrator` と API キーで動く試験は素通りする（端から端まで通す試験の一式で確認）
- **ファイルの中身は `implem/pleasanter:1.5.8.1` の `/app/App_Data/Parameters/Security.json`
  の写しで、`Mode` の 1 か所だけを変えてある。** Pleasanter は Parameters のファイルを
  丸ごと読むため、変えたい項目だけを置くことはできない
- **JSON には注釈が書けないので、由来は先頭の `"//"` の項目とこの節に書いてある**
  （`Rds.json` と同じやり方。知らない項目は Pleasanter が読み飛ばす。実測）
- **CodeDefiner には置かない。** スキーマを作るだけで 2 要素を見ないため
- **本体（AGPL）のコードではなく、相手方の容器へ渡す設定ファイル。** 本製品には含まれない
  （`Rds.json` と同じ扱い）

⚠️ **Pleasanter の版を上げるときは、新しいイメージから取り直すこと。**
古い写しのままだと、新しい版で増えた項目が既定値にならない。

```bash
docker run --rm --entrypoint cat implem/pleasanter:<版> /app/App_Data/Parameters/Security.json
```

**メールとの排他。** `NotificationType` は Pleasanter 全体で 1 つなので、この検証環境では
TOTP の 2 要素は試せない（TOTP は #464 で手で確認済み）。

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
