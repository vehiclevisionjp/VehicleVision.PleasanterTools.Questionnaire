#!/usr/bin/env bash
# 起動しているアプリへ HTTP で当てて、配信と応答の形を確かめる。
#
# **単体テストでは見えない部分**（静的ファイルの配信・SPA のフォールバック・
# セキュリティヘッダ・存在しない公開 ID の扱い）を見る。
#
#   docker compose --profile sqlserver up -d --wait
#   ./scripts/smoke.sh
set -uo pipefail

BASE="${QUESTIONNAIRE_BASE_URL:-http://localhost:8081}"
failed=0

check_status() {
    local path="$1" expected="$2" label="$3"
    local actual
    actual=$(curl -s -o /dev/null -w "%{http_code}" "${BASE}${path}")
    if [ "$actual" = "$expected" ]; then
        printf 'OK   %-46s %s\n' "$label" "$actual"
    else
        printf 'NG   %-46s 期待 %s / 実際 %s\n' "$label" "$expected" "$actual" >&2
        failed=1
    fi
}

check_post_status() {
    local path="$1" expected="$2" label="$3"
    local actual
    actual=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE}${path}")
    if [ "$actual" = "$expected" ]; then
        printf 'OK   %-46s %s
' "$label" "$actual"
    else
        printf 'NG   %-46s 期待 %s / 実際 %s
' "$label" "$expected" "$actual" >&2
        failed=1
    fi
}

check_header() {
    local path="$1" header="$2" label="$3"
    if curl -s -D - -o /dev/null "${BASE}${path}" | grep -qi "^${header}:"; then
        printf 'OK   %-46s\n' "$label"
    else
        printf 'NG   %-46s ヘッダが無い: %s\n' "$label" "$header" >&2
        failed=1
    fi
}

echo "== 配信 =="
check_status "/healthz" 200 "生存確認"
check_status "/ready" 200 "DBを含む受付準備"
check_status "/" 200 "回答画面の入口"
# **画面側で解釈するので、サーバは同じ入口を返す**
check_status "/f/pub-any" 200 "SPA のフォールバック"

echo
echo "== 存在しない公開 ID の扱い =="
# **未公開と存在しないを区別しない。** 総当たりで実在が分からないようにする
check_status "/api/forms/pub-does-not-exist" 404 "存在しない公開 ID"
check_status "/api/forms/pub-also-not-there" 404 "別の存在しない公開 ID"

echo
echo "== セキュリティヘッダ =="
check_header "/" "content-security-policy" "Content-Security-Policy"
check_header "/" "x-content-type-options" "X-Content-Type-Options"
check_header "/" "referrer-policy" "Referrer-Policy"
check_header "/" "permissions-policy" "Permissions-Policy"

echo
echo "== 管理画面の配信 =="
check_status "/admin" 200 "管理画面の入口"
# **回答画面とは別の束。** 回答者へ管理画面のコードを配らない
check_status "/admin/surveys/00000000-0000-0000-0000-000000000000" 200 "管理画面の画面内遷移"

echo
echo "== 管理画面の認証 =="
check_status "/api/admin/session" 200 "状態は誰でも見られる"
# **合言葉を通していない相手に、登録の入口を開けない**
check_post_status "/api/admin/enroll/begin" 401 "途中状態でなければ登録できない"

# **管理画面の応答を途中の経路に残さない**
check_header "/api/admin/session" "cache-control" "Cache-Control（保存させない）"

echo
if [ "$failed" -eq 0 ]; then
    echo "すべて通った"
else
    echo "失敗あり" >&2
fi
exit "$failed"
