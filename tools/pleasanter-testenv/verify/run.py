"""実機でしか確認できない事項を潰す検証。

**判定を推測で埋めない。** 確認できなかったものは「不明」と書く。
結果は /results/検証結果.md へ出す。
"""
import json
import os
import sys
import time
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
BASE = os.environ.get("PLEASANTER_BASE_URL", "http://pleasanter:8080").rstrip("/")

RESULTS = []


def rec(title, ok, detail):
    RESULTS.append((title, ok, detail))
    mark = {True: "OK", False: "NG", None: "不明"}[ok]
    print(f"\n===== [{mark}] {title} =====\n{detail}", flush=True)


def wait_for_pleasanter(timeout=180):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"{BASE}/", timeout=5) as res:
                if res.status < 500:
                    return True
        except Exception:
            pass
        time.sleep(3)
    return False


def main():
    if not wait_for_pleasanter():
        print("Pleasanter へ到達できない", file=sys.stderr)
        return 1
    import api

    # ------------------------------------------------------------------
    # 準備：検証用サイト（Results）を作る
    # ------------------------------------------------------------------
    ss = {
        "Columns": [
            {"ColumnName": "ClassA", "LabelText": "回答GUID"},
            {"ColumnName": "ClassB", "LabelText": "満足度",
             "ChoicesText": "1,とても不満\n2,不満\n3,普通\n4,満足\n5,とても満足",
             "ChoicesControlType": "Radio"},
            {"ColumnName": "DateA", "LabelText": "来店日"},
            {"ColumnName": "NumA", "LabelText": "点数"},
        ],
        "EditorColumnHash": {"General": ["ClassA", "ClassB", "DateA", "NumA"]},
    }
    status, body = api.post("api/items/0/CreateSite", {
        "Title": "検証用アンケート",
        "ReferenceType": "Results",
        "SiteSettings": ss,
    })
    site_id = body.get("Id") if isinstance(body, dict) else None
    rec("サイト作成", status == 200 and bool(site_id),
        f"status={status} siteId={site_id}\n{json.dumps(body, ensure_ascii=False)[:400]}")
    if not site_id:
        write_report()
        return 1

    # ------------------------------------------------------------------
    # 検証 2：GetSite の応答に解決済みの選択肢（ChoiceHash）が載るか
    # ------------------------------------------------------------------
    status, body = api.post(f"api/items/{site_id}/GetSite", {})
    raw = json.dumps(body, ensure_ascii=False)
    site = (body.get("Response", {}) or {}).get("Data", body) if isinstance(body, dict) else {}
    has_choicehash = "ChoiceHash" in raw
    has_choicestext = "ChoicesText" in raw
    rec("GetSite に ChoiceHash が載るか",
        None if status != 200 else has_choicehash,
        f"status={status}\nChoiceHash を含む: {has_choicehash}\n"
        f"ChoicesText を含む: {has_choicestext}\n応答の先頭:\n{raw[:700]}")
    with open("/results/getsite.json", "w", encoding="utf-8") as f:
        f.write(raw)

    # ------------------------------------------------------------------
    # 検証 4：Upsert が Keys で突き合わせるか（多重投稿抑止・編集の中核）
    # ------------------------------------------------------------------
    guid = "11111111-2222-3333-4444-555555555555"
    up1 = api.post(f"api/items/{site_id}/Upsert",
                   {"Keys": ["ClassA"], "ClassHash": {"ClassA": guid, "ClassB": "4"},
                    "NumHash": {"NumA": 10}})
    up2 = api.post(f"api/items/{site_id}/Upsert",
                   {"Keys": ["ClassA"], "ClassHash": {"ClassA": guid, "ClassB": "5"},
                    "NumHash": {"NumA": 20}})
    st, got = api.post(f"api/items/{site_id}/Get", {})
    data = (got.get("Response", {}) or {}).get("Data", []) if isinstance(got, dict) else []
    count = len(data) if isinstance(data, list) else None
    rec("Upsert が Keys で 1 件に集約されるか",
        None if st != 200 else (count == 1),
        f"1回目 status={up1[0]} {json.dumps(up1[1], ensure_ascii=False)[:200]}\n"
        f"2回目 status={up2[0]} {json.dumps(up2[1], ensure_ascii=False)[:200]}\n"
        f"Get status={st} 件数={count}\n{json.dumps(data, ensure_ascii=False)[:600]}")

    # ------------------------------------------------------------------
    # 検証 3：日付がどのタイムゾーンで解釈・返却されるか
    # ------------------------------------------------------------------
    sent = "2026-03-01T09:00:00"
    api.post(f"api/items/{site_id}/Upsert",
             {"Keys": ["ClassA"], "ClassHash": {"ClassA": guid}, "DateHash": {"DateA": sent}})
    st, got = api.post(f"api/items/{site_id}/Get", {})
    data = (got.get("Response", {}) or {}).get("Data", []) if isinstance(got, dict) else []
    returned = None
    if isinstance(data, list) and data:
        returned = (data[0].get("DateHash") or {}).get("DateA") or data[0].get("DateA")
    rec("日付の往復（送った値と返る値）",
        None,
        f"送った値: {sent}\n返った値: {returned}\n"
        f"（APIキー保有ユーザの TimeZone 設定と突き合わせて判断する）\n"
        f"{json.dumps(data[:1], ensure_ascii=False)[:600]}")

    write_report()
    return 0


def write_report():
    os.makedirs("/results", exist_ok=True)
    with open("/results/検証結果.md", "w", encoding="utf-8") as f:
        f.write("# 検証結果（実機）\n\n")
        f.write("Pleasanter 1.5.7.0 / SQL Server 2025 / `tools/pleasanter-testenv`\n\n")
        for t, ok, d in RESULTS:
            mark = {True: "OK", False: "NG", None: "不明"}[ok]
            f.write(f"## [{mark}] {t}\n\n```text\n{d}\n```\n\n")


if __name__ == "__main__":
    sys.exit(main())
