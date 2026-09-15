# 検証用 IdP（SAML）

SAML のログイン（Issue #166）を**手元で通すための IdP**。
[Keycloak](https://www.keycloak.org/)（Apache-2.0）を `compose.yaml` の
`saml` プロファイルで立てる。

> ⚠️ **検証環境専用。** ここにある鍵・証明書・パスワードは**全て公開された固定値**で、
> 誰でも同じものを持っている。**本番へ持ち込まないこと。**

## 起こし方

```bash
DEV_SAML_ENABLED=true docker compose --profile sqlserver --profile saml up -d --wait
```

**SAML は HTTPS でしか動かない**（途中を預ける cookie が `SameSite=None; Secure`）。
本アプリは `https://localhost:8443`、IdP は `https://localhost:8543` を使う。
どちらも自己署名なので、ブラウザは警告を出す（「詳細設定」から進む）。

| 役割 | URL |
| --- | --- |
| 管理画面 | `https://localhost:8443/admin` |
| IdP のログイン | `https://localhost:8543/realms/questionnaire/` |
| IdP の管理画面 | `https://localhost:8543/admin/`（`admin` / `idp-test-admin`） |

## 用意してある利用者

| 利用者 | パスワード | 用途 |
| --- | --- | --- |
| `admin@example.jp` | `idp-test-password` | **本アプリ側にも同じログイン ID を作って**試す |
| `stranger@example.jp` | `idp-test-password` | **本アプリに居ない**利用者。拒絶と JIT 登録を試す |

本アプリ側の管理者はこう作る（2 要素を任意にしておくと素直）。

```bash
DEV_SAML_ENABLED=true DEV_ADMIN_TWOFACTOR=optional \
    docker compose --profile sqlserver --profile saml up -d --wait

curl -sk -X POST https://localhost:8443/api/admin/setup \
    -H 'Content-Type: application/json' \
    -d '{"loginId":"admin@example.jp","password":"long-enough-password"}'
```

## 往復を確かめる

ブラウザを使わずに、SP → IdP → SP の往復を 1 本で通せる。

```bash
python tools/saml-idp/roundtrip.py admin@example.jp idp-test-password
```

```text
1. SP -> IdP  302  https://localhost:8543/realms/questionnaire/protocol/saml?SAMLRequest=...
2. IdP login  200
3. IdP auth   200
4. IdP -> SP  302  Location=/admin
5. session    200  {"authenticated":true,"loginId":"admin@example.jp",...}
```

**未登録の扱いを切り替えて試す。**

```bash
# 拒絶（既定）→ /admin?samlError=unknown-user
python tools/saml-idp/roundtrip.py stranger@example.jp idp-test-password

# その場で登録 → Editor として入れる
DEV_SAML_UNKNOWNUSER=Register docker compose --profile sqlserver --profile saml up -d app
python tools/saml-idp/roundtrip.py stranger@example.jp idp-test-password
```

## 中身

| ファイル | 内容 |
| --- | --- |
| `realm/realm-questionnaire.json` | realm・SP の登録・利用者・**署名鍵**（起動時に取り込まれる） |
| `tls/cert.pem`・`tls/key.pem` | IdP を HTTPS で立てるための自己署名証明書 |
| `roundtrip.py` | 往復を通す確認用のスクリプト |

### なぜ署名鍵を焼き込んでいるか

Keycloak は**既定だと起動のたびに署名鍵を作る。**
そのままだと SP 側の設定（`QUESTIONNAIRE_SAML_IDPCERTIFICATE`）を毎回書き換えることになる。
realm へ固定の鍵を入れておくことで、`compose.yaml` の既定値だけで通る。

### なぜ IdP も HTTPS なのか

**Keycloak がログインの途中を保つ cookie（`KC_AUTH_SESSION_HASH`）は `Secure` 付き。**
http で立てると cookie が戻らず、`cookie_not_found` でログインできない（実際に踏んだ）。

## 実機の IdP を試すとき

Google Workspace の設定例は
[`_documents/SAML認証-運用手順書.md`](../../_documents/SAML認証-運用手順書.md)。
**ここの IdP はプロトコルの往復を確かめるためのもの**で、
実際の IdP の属性の出し方までは代わりにならない。
