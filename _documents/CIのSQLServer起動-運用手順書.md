# CI の SQL Server 起動運用手順書

## 起動時に行うこと

結合テスト、E2E、画面試験は `scripts/wait_sqlserver.py` で SQL Server の起動を確認する。
E2E と画面試験では、DB を先に起動し、接続できることを確認してから依存サービスを起動する。

起動確認は `sqlcmd` の `SELECT 1` で行う。起動待ちの期限は 300 秒とし、
接続・クエリと Docker コマンドにも個別のタイムアウトを設ける。
再試行しても起動待ちの期限は延ばさない。診断の取得時間はこの期限の外にある。

コンテナが終了していたら、待ち続けず状態とログを保存する。
以下の条件がすべて揃った場合だけ、同じコンテナを **1 回だけ** 起動し直す。

- 終了コードが `1`。
- `OOMKilled` が `false`。
- 取得に成功したコンテナログに `Reason: 0x00000002` と
  `Resource temporarily unavailable` の両方がある。

この組合せは Issue #461 の失敗ログで確認したもの。
2 回目の終了、別のエラー、OOM、ログを取得できない場合は再起動せず失敗する。
実行中だが接続できない場合は期限まで待ち、失敗時の診断を残す。
テストの再実行やデータボリュームの削除は行わない。

## 診断の確認

Actions の `SQLServer起動診断-*` アーティファクトを見る。
再試行前の記録は、再試行後の成功でも残す。

- `*-state.json`：終了コード、OOM の有無、起動・終了時刻など。
- `*-logs.txt`：SQL Server コンテナの直近 500 行。
- `*-limits.txt`：イメージ ID、再起動回数、メモリ・CPU・プロセス数の上限。
- `*-host.json`：ホストの CPU 数、メモリ情報、空きディスク容量。
- `last-probe.txt`：最後に失敗した接続確認の出力。

環境変数を含む `docker inspect` 全体は記録しない。
パスワードは環境変数で渡し、保存する診断からも同じ文字列を除く。

## 確認できたことと未確定のこと

2026-09-24 の結合テストでは、起動待ちが失敗する約 5 分前に SQL Server が異常終了していた。
待ち時間の延長だけでは回復しないため、終了を検知し、診断を残して限定的に再試行する形にした。
SQL Server 内部のエラーを生む根本原因がリソース制約か製品側の不具合かは未確定。

2026-09-25 に、同じ SQL Server 2025 CU8 イメージを使い、初回だけ対象のエラーを出して終了する
専用コンテナで再起動後の実接続を確認した。これは回復処理の検証であり、内部エラーそのものの再現ではない。
実装の条件分岐は `scripts/tests/test_wait_sqlserver.py` で検証し、CI でも毎回実行する。

根拠（2026-09-25 参照）：

- [失敗した結合テストの初回ログ](https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Questionnaire/actions/runs/35977045993/attempts/1)
- [SQL Server コンテナのトラブルシューティング](https://learn.microsoft.com/en-us/sql/linux/containers/troubleshoot?view=sql-server-ver17)
- [docker container inspect](https://docs.docker.com/reference/cli/docker/container/inspect/)
- [sqlcmd の環境変数とタイムアウト](https://learn.microsoft.com/en-us/sql/tools/sqlcmd/sqlcmd-utility?view=sql-server-ver17)
