# 取説用の画面の写しを撮る

**手で撮ると、画面が変わるたびに古くなる。** Playwright で撮り直せるようにしてある。

## 撮り方

**まっさらな検証環境が要る**（管理者がまだ 1 人も居ない状態）。
初期設定と 2 要素の登録も取説に載せる画面なので、実際の初回の流れをそのまま辿る。

```bash
# 1. 検証環境を作り直す（DB ごと消す）
docker compose --profile sqlserver down -v
docker compose --profile sqlserver up -d --wait

# 2. spec を焼き直す
docker compose --profile screenshots build screenshots

# 3. 撮る
docker compose --profile sqlserver --profile screenshots run --rm screenshots
```

写しは `tools/screenshots/shots/` に出る。

### ⚠️ 変更したものに応じてイメージを作り直す

**spec を直したら、撮る前に `docker compose --profile screenshots build screenshots` が要る。**
spec は screenshots イメージへ焼き込まれ、容器へマウントしているのは
`shots` と `report` だけなので、ビルドしない限り古い spec が走る。

**アプリを直したら、検証環境を `up --build` で起動する。**

```bash
docker compose --profile sqlserver up -d --build --wait
```

`up` だけでは古いアプリのイメージが残り、直した画面を写しへ反映できない。

**2 回目も必ず `down -v` から始める。** 1 回目に管理者を作るため、
同じボリュームでもう一度走らせると初期設定の画面へ進めず、必ず落ちる。

### ⚠️ 「まっさら」を省くと、分からない形で落ちる（Issue #65）

**`down -v` を省いて撮ると、途中で次のように落ちる。**

```text
Error: page.screenshot: Protocol error (Page.captureScreenshot):
  Unable to capture screenshot
```

**落ちる場所は走らせるたびに変わる**ので、特定の画面の問題にも、
倍率の問題にも見える。**どちらでもない。**

原因は**溜まった行**。結合テストを何度も走らせた検証環境には
**アンケートが 1367 件**あり、管理画面の一覧はそれを全部描く。
ページ全体の写しが巨大になり、Chromium が撮れなくなる。
**DB を空にすれば、同じ倍率のまま 9 件すべて通る**（実測、2026-08-20）。

**効かなかったもの。** 原因を取り違えないよう記しておく。

| 試したこと | 結果 |
|---|---|
| `/dev/shm` を 1 GB へ広げる（既定 64 MB） | **変わらない** |
| `--disable-dev-shm-usage` | **変わらない** |
| `--disable-gpu` | **変わらない** |
| `deviceScaleFactor` を 2 → 1 | 通るが、**原因ではない**（症状を薄めているだけ） |

**今は撮り始める前に前提を確かめて落とす**（`lib/fresh.ts`）。
「撮れない」より「撮れない理由が分からない」方が高く付く。

### 初期設定を撮る spec は先に分けて走らせる

**`manual.spec.ts` は最初の管理者を自分で作る。**
`branching.spec.ts` などを先に走らせると管理者が既に居るので、
**初期設定の画面がもう出ない**（取説に要る画面）。

既定の `npm run shot` は、`manual.spec.ts` を先に単独で通してから、
そこで作った管理者の認証状態を引き継いで残りを走らせる。
**単発確認用の `review.spec.ts` は既定の一式へ混ぜない。**

```bash
# 写しを撮る（まっさらな環境で、これだけ）
docker compose --profile sqlserver --profile screenshots run --rm     screenshots npx playwright test specs/manual.spec.ts

# 製品の検証（写しは撮らない）
docker compose --profile sqlserver --profile screenshots run --rm     screenshots npx playwright test --grep-invert @standalone

# 直した画面だけを確認する
docker compose --profile sqlserver --profile screenshots run --rm     screenshots npx playwright test specs/review.spec.ts
```

## 豆腐（□）にしないために

**Linux のコンテナには日本語フォントが無いのが普通で、入れ忘れれば確実に豆腐になる。**
しかも**撮った画像を目で見るまで気付かない。**

そこで 2 つ置いてある。

1. **フォントを入れる**（`Dockerfile` の `fonts-noto-cjk`）
2. **撮る直前に機械で確かめる**（`lib/tofu.ts`）。豆腐があれば**落とす**

### 見分け方

**字形を持たない文字は、どれも同じ「豆腐」の字形で描かれる。**
確かめたい文字と「どのフォントにも無い文字」を同じ書体で canvas へ描き、
**画素が完全に一致したら描けていない**と判断する。

幅の比較では足りない。等幅の書体では、描けている文字も同じ幅になる。

### 検出器そのものも試験する

**「豆腐が出ていない」という結果は、検出器が壊れていても同じように出る。**
字形の無い文字（U+E000）を渡して、ちゃんと拾えることを毎回見ている。

**実測（2026-08-19）。** フォントを全部消した入れ物で走らせると、
日本語の 2 件が落ち、検出器そのものの試験だけが通った。**検出器は働いている。**

なお **Playwright の公式イメージには IPAGothic が既に入っている。**
`fonts-noto-cjk` は絵文字と字種の広さのために足している。

## 撮れるもの

| ファイル | 画面 |
|---|---|
| `admin-01-setup` | 最初の管理者を登録する |
| `admin-02-enroll` | 2 要素認証の登録（QR） |
| `admin-03-recovery-codes` | 復旧コードの表示 |
| `admin-04-survey-list-empty` | アンケート一覧（空） |
| `admin-05-survey-list` | アンケート一覧 |
| `admin-06-survey-editor` | 設問エディタ |
| `admin-07-login` | ログイン（パスワード） |
| `admin-08-totp` | ログイン（使い捨てパスワード） |
| `answer-01-form` | 回答画面 |
| `answer-02-filled` | 入力した状態 |
| `answer-03-completed` | 送信後 |
| `answer-04-not-found` | 見つからないとき |
| `answer-05-mobile` | 回答画面（携帯の幅） |
| `admin-09-audit-log` | 操作の記録 |
| `admin-10-outbox` | 送信状況 |
| `admin-11-preview` | プレビュー |
| `admin-12-users` | 管理者の一覧 |
| `admin-13-invitation` | 招待の URL |
| `admin-14-my-account` | 自分のアカウント |
| `admin-15-flowchart` | 分岐のフローチャート（`specs/flowchart.spec.ts`） |
| `admin-16-flowchart-problems` | フローチャートの問題の表示（同上） |
| `admin-17-readability-dark` | 管理画面の暗い配色（`specs/readability.spec.ts`） |
| `admin-18-readability-large` | 管理画面の特大文字（同上） |
| `answer-06-readability-default` | 回答画面（作成者のテーマと標準文字。同上） |
| `answer-07-readability-large` | 回答画面の特大文字（同上） |
| `answer-08-readability-contrast` | 回答画面の高コントラスト（同上） |
| `answer-09-readability-dark` | 回答画面の暗い配色（同上） |
| `answer-10-readability-required-error` | 高コントラストでの必須の入力漏れの表示（同上） |
| `admin-19-pleasanter-sso-settings` | Pleasanter ログイン設定（`specs/pleasanter-sso.spec.ts`） |
| `admin-20-pleasanter-login` | ログイン（「Pleasanter でログイン」の釦） |

`admin-12`〜`admin-14` は `specs/manual.spec.ts` が撮る。
`admin-19`・`admin-20` は `shots/` にコミットしていない（2026-09-25 時点。e2e.yml の写しの一式で撮られる）。

## 秘密の値は伏せる

**取説に本物の値を載せない。** 撮る直前に、共有鍵と復旧コードを見本の文字へ置き換えている。

使い捨ての検証環境で撮っているとはいえ、**本物が載った図は
「載せてよいもの」という誤解を招く。**

## 気をつけていること

- **写しはデスクトップだけで撮る。** 初期設定は「管理者がまだ 1 人も居ない」状態でしか
  通らないので、複数の見え方で走らせると 2 回目が必ず落ちる。
  携帯の見え方は、回答画面の中で画面の幅を変えて撮っている
- **`app` という名前でブラウザから開かない。** Chrome は `.app` を HSTS の
  プリロード一覧に持っており、単一ラベルの `app` でも https へ強制されて
  `ERR_SSL_PROTOCOL_ERROR` になる（実測。curl では http のまま 200 が返る）。
  compose で `questionnaire-app` の別名を付けてある
- **ログインは試験をまたいで持ち越す。** 試験ごとに入れ物は作り直されるので、
  cookie を書き出して次へ渡している
- **送信は最短時間を待つ。** 速すぎる送信は bot 対策で断られる

## Pleasanter のログインの往復（Issue #470）

`specs/pleasanter-sso.spec.ts` が、設定画面から Pleasanter のログインを有効にし、
ログイン画面の「Pleasanter でログイン」→ 別窓で Pleasanter にログイン → 管理画面へ入る往復を確かめる。

- **Pleasanter の利用者を先に入れておくこと**（`tools/pleasanter-testenv/seed/03_sso_users.sql`。
  e2e.yml の写しの一式の job が流している）
- **Pleasanter と本アプリをブラウザから同じホスト名で開く。** cookie はホスト名ごとにしか届かないため、
  容器の中に `localhost:8080`（→ `pleasanter:8080`）と `localhost:8081`（→ `questionnaire-app:8080`）の
  素通しを立てる（`lib/tcp-forward.ts`。TCP を流すだけ）
- **設定は画面から有効にし、終わったら無効へ戻す。** 写しの一式の本アプリは外部設定を持たないので、
  ロック表示はサーバの応答の `fixedFields` だけを書き換えて確かめる。サーバが外部設定を優先することは
  結合テストの `PleasanterSsoEndToEndTests` が見ている
- 手元（容器の外）で走らせるときは、`localhost:8080` と `localhost:8081` に Pleasanter と本アプリが
  既に居る前提で `SSO_SKIP_FORWARD=1 SSO_PLEASANTER_HOST=localhost` を付ける

## 安全なコンテキスト（https）でも試す

**`crypto.subtle` は https か localhost でしか使えない。** 平文だけで試していると、
proof-of-work は自前の SHA-256（控え）しか通らず、**本番で実際に使われる経路が
一度も試されない**（Issue #67）。

検証環境のアプリは 8443 で https も待ち受けており（`compose.yaml` の `devcert` と `app`）、
`specs/secure-context.spec.ts` が両方の経路を、それぞれの向きから見ている。

- https … `crypto.subtle.digest` が**実際に呼ばれ**、その解答がサーバに通ること
- 平文 … `crypto.subtle` が**無く**、それでも控えの解答がサーバに通ること

**「呼ばれた」は数えて確かめる。** `window.isSecureContext` を見るだけでは、
画面の側が本当にその道を通ったかは分からない。数える仕掛けは `page.addInitScript` で
外から包んでおり、**製品のコードには手を入れていない。**

証明書は自己署名で、名前も `questionnaire-app` とは一致しない。
そのため `ignoreHTTPSErrors: true` で通している（`playwright.config.ts`）。
**検証環境だけの割り切り。** 手順は
[`_documents/開発環境.md`](../../_documents/開発環境.md)「HTTPS（自己署名）で動かす」。

## ライセンス

`@playwright/test` は Apache-2.0。フォントは SIL Open Font License 1.1。
いずれも寛容ライセンス（`NOTICE` に記載）。
