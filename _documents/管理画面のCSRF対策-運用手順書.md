# 管理画面の CSRF 対策 運用手順書

本書は、管理 API（`/api/admin/**`）が**別オリジンからの状態変更を拒む仕組み**と、
併設の構成で運用者が守る前提をまとめる（Issue #907）。

## 1. なぜ要るのか

管理画面の認証 cookie（`q.admin` など）は `HttpOnly`・`SameSite=Strict` である。
しかし **`SameSite` の「サイト」は登録可能なドメイン単位**で、オリジン（スキーム・ホスト・ポート）より粗い。
次の構成では、本アプリと別のページが**同一サイト**になり、`SameSite=Strict` の cookie が付く。

| 構成の例 | サイトの関係 |
| --- | --- |
| Pleasanter `https://pleasanter.example.com`、本アプリ `https://questionnaire.example.com` | **同一サイト**（`example.com`）。別オリジン |
| `http://localhost:29102` と `http://localhost:29103`（ポートだけ違う） | **同一サイト**。別オリジン |
| Azure の既定のホスト名（`a.azurewebsites.net` と `b.azurewebsites.net`） | 別サイト（`azurewebsites.net` は公開サフィックス） |
| Pleasanter `/`、本アプリ `/questionnaire/`（同じホスト名のサブパス） | **同一オリジン**（下記 4 章） |

同一サイトの別オリジンのページ（Pleasanter のコンテンツに XSS があるとき、サブドメインに
信頼できないページがあるときなど）から、ログイン中の管理者の権限で、本文なしの `POST`
（アンケートの停止・再開・アーカイブ、セッションの失効、ユーザーの無効化など）を送れた。
`SameSite=Strict` だけでは防げなかったことを、#858 の確認で実測した。

## 2. 何を拒むか

`/api/admin/**` の **`GET`・`HEAD`・`OPTIONS`・`TRACE` 以外**の要求を、次の順で検証する
（`AdminCsrfMiddleware`・`AdminCsrfGuard`）。

| 順 | 条件 | 結果 |
| --- | --- | --- |
| 1 | `Sec-Fetch-Site` がある | `same-origin`・`none` だけ通す。`same-site`・`cross-site`・未知の値・複数の値は **403** |
| 2 | 1 が無く、`Origin` がある | 本アプリの公開オリジンと一致すれば通す。不一致・`null` は **403** |
| 3 | どちらも無い | **通す** |

- **公開オリジン**は、`UseForwardedHeaders` が決めた `Request.Scheme` と、受け取った `Host`（ポート）で決める。
  設定は増やしていない（SAML の ACS URL の作り方と同じ流儀）。`Origin` にパスは含まれないので、
  サブパス（`QUESTIONNAIRE_PATH_BASE`）は比較に影響しない
- **3 を通す理由。** ブラウザは、クロスオリジンの `POST`・`PUT`・`DELETE`・`PATCH` に必ず `Origin` を付ける。
  `Sec-Fetch-Site` を知らない古いブラウザも同じ。つまり**両方無い要求はブラウザの CSRF の経路にならず**、
  curl などの非ブラウザ（運用スクリプト）を壊さずに済む。`Referer` は使わない
- 拒否の応答は **403** と `{"code":"cross-site-request-rejected"}` だけ。理由の詳細は返さない
- ログ（Warning）には、拒否した事実・メソッド・パス・ヘッダーの種別だけを出す
  （`sec-fetch-site=cross-site`、`origin=mismatch`）。`Origin` の値・cookie・本文は出さない
- 認証より前に判定する。未認証の `POST`（ログインの試行など）も、別オリジンからは受け付けない
- 回答画面の API（`/api/forms/**`。cookie 認証を使わない）は対象外

### 2.1 例外（別サイトからの POST を意図する経路）

パスを明示した許可リスト（`AdminCsrfGuard.CrossSitePostPaths`）だけ。既定は拒否。

| 経路 | 理由 | ほかの守り |
| --- | --- | --- |
| `POST /api/admin/saml/acs` | IdP の画面から、利用者のブラウザが HTTP-POST バインディングで送る | 署名・宛先・期限・こちらが出した要求との対応（SP 起動だけ） |
| `POST /api/admin/saml/slo` | IdP からの単一ログアウト（POST バインディング） | 署名の検証。落とすのはこちらの cookie だけ |

Pleasanter のログインの戻り（`POST /api/admin/pleasanter-sso/check`）は**例外にしていない**。
本アプリの画面が同一オリジンから `fetch` し、Pleasanter の cookie はブラウザがそのまま送るだけで、
別サイトからの `POST` を受ける経路ではないため。

経路を足すときは、`EndpointGraphTests` が「許可リストの経路が実在し、`POST` である」ことを見る。
**例外を足す変更は、この表の根拠も一緒に直すこと。**

## 3. 運用者が守る前提

- **リバースプロキシで `Origin`・`Sec-Fetch-Site` を落とさない**（書き換えもしない）。
  落とすと、ヘッダーが無い要求として通ってしまう（防御が効かなくなるだけで、画面は動く）
- **`Host` を元のまま渡す。** `Origin` の照合は、受け取った `Host` を使う。
  プロキシが `Host` を内部の名前へ書き換えると、`Sec-Fetch-Site` を送らない場合
  （古いブラウザ・**HTTPS でも localhost でもない平文 HTTP**。ブラウザは `Sec-Fetch-*` を送らない）に
  `Origin` が一致せず、管理画面の操作が 403 になる。
  サブパス配置の例（Apache の `ProxyPreserveHost On`、Nginx の `proxy_set_header Host`）は、
  [サブパス配置-運用手順書](サブパス配置-運用手順書.md) 3 章
- **HTTPS を終端するプロキシは、信頼するネットワークを指定して `X-Forwarded-Proto` を渡す**
  （`QUESTIONNAIRE_FORWARDED_NETWORKS`）。`Request.Scheme` が `http` のままだと、
  `Origin: https://…` と一致しない
- 運用スクリプトから管理 API を呼ぶときは、`Origin` を付けないか、公開オリジンと同じ値を付ける

## 4. この対策の範囲

- 守るのは、**別オリジンのページから、管理者のブラウザを使って状態を変える要求**。
  同一オリジン（Pleasanter `/`、本アプリ `/questionnaire/` のサブパス配置）のページで動く
  悪意のスクリプト（Pleasanter への XSS）には**効かない**。同一オリジンなので、
  `Sec-Fetch-Site: same-origin` で通る。Pleasanter 側の XSS を防ぐのは Pleasanter 側の責任
- `GET` は検証しない。状態を変える `GET`（`/api/admin/saml/logout`・`/slo` の `GET`。ログアウトだけ）が
  あり、別オリジンから呼ばれると、ログアウトさせられる。ログアウトは入られるより軽いと判断して
  対象にしていない
- CSRF トークンは使わない。ブラウザの `Sec-Fetch-Site`・`Origin` に頼る
  （ヘッダーを送らない・落とす構成は 3 章の前提で潰す）

## 5. 確認

```bash
base="https://questionnaire.example.com"
# 別サイトを装う（curl は何でも書ける。ブラウザでは偽れない）
curl -s -o /dev/null -w "%{http_code}\n" -X POST -H "Sec-Fetch-Site: same-site" \
  "$base/api/admin/logout"    # 403
curl -s -o /dev/null -w "%{http_code}\n" -X POST -H "Origin: https://pleasanter.example.com" \
  "$base/api/admin/logout"    # 403
```

ブラウザの開発者ツールで、管理画面の `POST` に `Sec-Fetch-Site: same-origin` が付いていること、
プロキシ越しでも付いていること（落とされていないこと）を確かめる。

## 6. 根拠

- `src/VehicleVision.PleasanterTools.Questionnaire.Web/Services/AdminCsrfGuard.cs`・`AdminCsrfMiddleware.cs`
- `tests/VehicleVision.PleasanterTools.Questionnaire.Integration.Tests/AdminCsrfEndToEndTests.cs`
  （実アプリの host・SQLite）
- `Sec-Fetch-Site`・`Origin` の仕様: [Fetch Metadata Request Headers](https://www.w3.org/TR/fetch-metadata/)・
  [Fetch Standard の Origin ヘッダー](https://fetch.spec.whatwg.org/#origin-header)
  。**`Sec-Fetch-*` は、信頼できる URL（HTTPS・localhost）の要求にだけ付く**という点は、
  仕様の記憶に基づく。本 Issue では一次資料を取り直していない（未確認）。
  実機では、localhost（Chromium）で `Sec-Fetch-Site: same-origin` が付くことだけを確かめた
