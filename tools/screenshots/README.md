# 取説用の画面の写しを撮る

**手で撮ると、画面が変わるたびに古くなる。** Playwright で撮り直せるようにしてある。

## 撮り方

**まっさらな検証環境が要る**（管理者がまだ 1 人も居ない状態）。
初期設定と 2 要素の登録も取説に載せる画面なので、実際の初回の流れをそのまま辿る。

```bash
# 1. 検証環境を作り直す（DB ごと消す）
docker compose --profile sqlserver down -v
docker compose --profile sqlserver up -d --wait

# 2. 撮る
docker compose --profile sqlserver --profile screenshots run --rm screenshots
```

写しは `tools/screenshots/shots/` に出る。

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
| `admin-07-login` | ログイン（合言葉） |
| `admin-08-totp` | ログイン（使い捨てパスワード） |
| `answer-01-form` | 回答画面 |
| `answer-02-filled` | 入力した状態 |
| `answer-03-completed` | 送信後 |
| `answer-04-not-found` | 見つからないとき |
| `answer-05-mobile` | 回答画面（携帯の幅） |

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
