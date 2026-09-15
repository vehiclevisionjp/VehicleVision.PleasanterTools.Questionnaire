# -*- coding: utf-8 -*-
"""検証用 IdP（Keycloak）へ SAML の往復を通す（Issue #166）。

**ブラウザ無しで確かめる。** 標準ライブラリだけで組んでいるので、
Playwright を入れずに済む。

    python tools/saml-idp/roundtrip.py admin@example.jp idp-test-password

起こし方は tools/saml-idp/README.md。

⚠️ **検証環境専用。** 自己署名の証明書を確かめずに繋ぐので、本番へ向けて使わないこと。
"""
import http.cookiejar
import re
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request

APP = "https://localhost:8443"
CTX = ssl._create_unverified_context()  # 開発用の自己署名証明書


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def build(follow=True):
    jar = http.cookiejar.CookieJar()
    handlers = [urllib.request.HTTPCookieProcessor(jar),
                urllib.request.HTTPSHandler(context=CTX)]
    if not follow:
        handlers.append(NoRedirect())
    return urllib.request.build_opener(*handlers), jar


def get(opener, url, follow=True):
    request = urllib.request.Request(url, method="GET")
    try:
        with opener.open(request) as response:
            return response.status, response.headers, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read().decode("utf-8", "replace")


def post(opener, url, fields):
    data = urllib.parse.urlencode(fields).encode()
    request = urllib.request.Request(
        url, data=data, method="POST",
        headers={"Content-Type": "application/x-www-form-urlencoded"})
    try:
        with opener.open(request) as response:
            return response.status, response.headers, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read().decode("utf-8", "replace")


def main(user, password):
    # 1 つの cookie 入れを使い回す（ブラウザ 1 つ分）
    jar = http.cookiejar.CookieJar()
    processor = urllib.request.HTTPCookieProcessor(jar)
    https = urllib.request.HTTPSHandler(context=CTX)
    follower = urllib.request.build_opener(processor, https)
    stopper = urllib.request.build_opener(processor, https, NoRedirect())

    # ---- 1. SP へログインを頼む（IdP へ飛ばされる）
    status, headers, _ = get(stopper, f"{APP}/api/admin/saml/login?returnUrl=/admin")
    assert status in (301, 302, 303, 307), status
    idp_url = headers["Location"]
    print(f"1. SP -> IdP  {status}  {idp_url[:80]}...")
    assert "SAMLRequest=" in idp_url
    assert any(cookie.name == "q.admin.saml" for cookie in jar), "途中を預ける cookie が無い"

    # ---- 2. IdP のログイン画面
    status, _, html = get(follower, idp_url)
    print(f"2. IdP login  {status}")
    action = re.search(r'<form id="kc-form-login"[^>]*action="([^"]+)"', html)
    assert action, html[:400]
    login_url = action.group(1).replace("&amp;", "&")

    # ---- 3. 資格情報を出す（SAMLResponse を積んだ form が返る）
    status, _, html = post(follower, login_url, {"username": user, "password": password, "credentialId": ""})
    print(f"3. IdP auth   {status}")
    response = re.search(r'name="SAMLResponse" value="([^"]+)"', html)
    assert response, html[:600]
    acs = re.search(r'action="([^"]+)"', html).group(1).replace("&amp;", "&")
    relay = re.search(r'name="RelayState" value="([^"]*)"', html)
    print(f"   ACS = {acs}")

    # ---- 4. 応答を SP の受け口へ渡す
    fields = {"SAMLResponse": response.group(1)}
    if relay:
        fields["RelayState"] = relay.group(1)
    status, headers, _ = post(stopper, acs, fields)
    location = headers.get("Location", "")
    print(f"4. IdP -> SP  {status}  Location={location}")

    # ---- 5. 入れたか
    status, _, body = get(follower, f"{APP}/api/admin/session")
    print(f"5. session    {status}  {body}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
