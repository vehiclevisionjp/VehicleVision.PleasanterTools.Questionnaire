# AKS 導入更新運用手順書

## 対象と前提

本書は、本アプリを既存の Azure Kubernetes Service（AKS）へ Helm で導入し、
Azure Container Registry（ACR）のイメージへ更新する手順を定める。
AKS クラスター自体の作成、DB と Pleasanter の構築は対象外とする。

必要なものは次のとおり。

- AKS クラスター、ACR、外部 DB、Pleasanter が構築済み
- ローカルに Azure CLI、`kubectl`、Helm 4 または Helm 3
- AKS から DB と Pleasanter へ HTTPS/TLS で到達できるネットワーク
- ACR からイメージを pull できる AKS の権限
- `ReadWriteMany` を使える Azure Files CSI ストレージクラス
- Ingress を公開する場合は DNS 名と TLS 証明書

本リポジトリのチャートは `deploy/aks/questionnaire/` にある。
AKS Deployment Safeguards を前提に、非 root、読み取り専用ルートファイルシステム、
リソース要求・上限、probe、2 Pod 以上、PodDisruptionBudget を設定している。

## 構成

```mermaid
flowchart LR
    Browser["回答者・管理者"] -->|HTTPS| Ingress["Application Routing Ingress"]
    Ingress --> Service["ClusterIP Service"]
    Service --> Pods["Questionnaire Pods\n2 Pod 以上 / HPA"]
    Pods --> DB["外部 DB"]
    Pods --> Pleasanter["Pleasanter API"]
    Pods --> Keys["Azure Files PVC\nData Protection 鍵束"]
    ACR["Azure Container Registry"] --> Pods
    Hook["Helm migration Job\n同時実行数 1"] --> DB
```

`/healthz` はプロセスの生存、`/ready` は本アプリ DB への接続を確認する。
Pleasanter が一時停止しても回答を DB に保留できるため、`/ready` は Pleasanter を確認しない。

複数 Pod で管理画面の Cookie を共用するため、ASP.NET Core Data Protection の鍵束を
Azure Files PVC に保存する。この PVC を消すと既存 Cookie が無効になるため、通常の更新では削除しない。

## 事前設定

### ACR と AKS を接続する

AKS の kubelet identity が ACR から pull できるようにする。

```bash
az aks update \
  --resource-group <AKSリソースグループ> \
  --name <AKS名> \
  --attach-acr <ACR名>

az aks get-credentials \
  --resource-group <AKSリソースグループ> \
  --name <AKS名>
```

組織の方針で `--attach-acr` を使えない場合は、ACR の権限モデルに合わせて
kubelet identity へ pull の最小権限を割り当てる。

### Namespace と資格情報を作る

資格情報を Helm values に書くと Helm release history に残る。
チャートは Secret を作らず、事前作成した Secret の名前だけを参照する。

```bash
kubectl create namespace questionnaire
```

次のファイルを安全な作業場所へ作る。リポジトリへ置かず、利用後に安全に削除する。

```text
QUESTIONNAIRE_DB_CONNECTIONSTRING=<本アプリDBの接続文字列>
QUESTIONNAIRE_PLEASANTER_APIKEY=<Pleasanter APIキー>
QUESTIONNAIRE_SECRET_KEY=<Base64の32バイト鍵>
```

Secret を作成する。

```bash
kubectl create secret generic questionnaire-secrets \
  --namespace questionnaire \
  --from-env-file=.questionnaire-secrets.env
```

`QUESTIONNAIRE_SECRET_KEY` は紛失すると登録済みの2要素認証を復号できない。
Azure Key Vault 等へ控えを保管する。Secret の更新は同名で再生成して適用する。

```bash
kubectl create secret generic questionnaire-secrets \
  --namespace questionnaire \
  --from-env-file=.questionnaire-secrets.env \
  --dry-run=client -o yaml | kubectl apply -f -
```

### Ingress と TLS を用意する

AKS Application Routing add-on を使う場合、Ingress class は
`webapprouting.kubernetes.azure.com` とする。TLS Secret を先に作る。

`config.forwardedNetworks` には Application Routing Ingress からアプリ Pod へ接続するときの
送信元 CIDR だけを指定する。これは `X-Forwarded-For` と `X-Forwarded-Proto` を信頼する範囲である。
無関係な VNet 全体や `0.0.0.0/0` を指定しない。ネットワーク方式により Pod CIDR または
Ingress node/subnet の CIDR になるため、クラスター管理者が実際の構成から確定する。

Application Routing の Pod と node の配置は次で確認できる。

```bash
kubectl get pods --namespace app-routing-system -o wide
kubectl get nodes -o wide
```

```bash
kubectl create secret tls questionnaire-tls \
  --namespace questionnaire \
  --cert=questionnaire.crt \
  --key=questionnaire.key
```

証明書を別の仕組みで管理する場合は、同名の TLS Secret が作られるようにする。
Production ではアプリが HTTPS へリダイレクトするため、Ingress を有効にする場合は
host と TLS Secret の両方が必須である。

### サブパス配置と Pleasanter のログイン

v0.7.0 のサブパス配置（`QUESTIONNAIRE_PATH_BASE`）と、Pleasanter のログインで管理画面へ入る機能
（`QUESTIONNAIRE_PLEASANTERSSO_*`）は、チャート 0.2.0（v0.7.1）から values で渡せる。
**0.1.0（v0.7.0 まで）のチャートにはこれらの値が無く、Ingress のパスも `/` 固定だった**（Issue #479）。

Pleasanter のログインは、**本アプリと Pleasanter を同じホスト名に置き、本アプリをサブパスにする**ことが前提
（[`Pleasanter-SSO-運用手順書.md`](Pleasanter-SSO-運用手順書.md) 4 章、
[`サブパス配置-運用手順書.md`](サブパス配置-運用手順書.md)）。AKS では次の形になる。

```mermaid
flowchart LR
    B["ブラウザ<br>https://pleasanter.example.com"] --> I["Application Routing Ingress<br>（同じホスト名）"]
    I -->|"/questionnaire（Prefix。接頭辞を残す）"| Q["本アプリの Service<br>このチャートの Ingress"]
    I -->|"/"| P["Pleasanter の Service<br>チャートの外の Ingress"]
    Q -.->|"内部 URL（クラスター内）"| P
```

| 値 | 渡す環境変数 | 既定 | 内容 |
| --- | --- | --- | --- |
| `config.pathBase` | `QUESTIONNAIRE_PATH_BASE` | `""`（渡さない。`/` で動く） | 例 `/questionnaire`。書式はサブパス配置-運用手順書 2 章。`values.schema.json` でも書式を検査する |
| `config.pleasanterSso.<項目>` | `QUESTIONNAIRE_PLEASANTERSSO_<項目の大文字>` | `{}`（何も渡さない。無効） | 項目は `enabled`・`internalBaseUrl`・`loginUrl`・`logoutUrl`・`cookieNames`・`unknownUser`・`registerRole`・`revalidateMinutes`・`timeoutSeconds`・`buttonLabel`。綴り違いは schema で止まる |
| `ingress.path` | — | `""`（`config.pathBase`、それも無ければ `/`） | Ingress で本アプリへ振り分けるパス。`config.pathBase` で始まらない値は `helm template` の時点で止まる |
| `ingress.pathType` | — | `Prefix` | `Prefix`・`Exact`・`ImplementationSpecific` |

- **`config.pleasanterSso` は書いた項目だけを渡す。** 外部設定に値がある項目は管理画面から変えられなくなる
  （Pleasanter-SSO-運用手順書 7.1）ため、既定では 1 つも渡さず、`null` と空文字も渡さない。
  管理画面から設定する運用なら、この値は書かなくてよい。秘密を含む項目は無いので ConfigMap に載せる
- **`internalBaseUrl` はクラスター内の Pleasanter の Service を直接指してよい**
  （例 `http://pleasanter.pleasanter.svc.cluster.local`。Pleasanter-SSO-運用手順書 7.1）。
  `loginUrl`・`logoutUrl` はブラウザが開くので、同じホスト名のパス（`/users/login` など）にする
- **ConfigMap が変わると Pod を入れ替える。** サブパスなどは起動時に 1 度だけ読むため、
  Pod テンプレートに ConfigMap の `checksum/config` 注釈を付けている（チャート 0.2.0 から）
- **`/healthz`・`/ready` はサブパスの外でも応答する**（`DatabaseStartupMigration.IsAvailableBeforeMigration`）。
  サブパスを設定しても probe の path は変えない

Ingress について気を付けること。

- **接頭辞を剥がさない。** `nginx.ingress.kubernetes.io/rewrite-target` などで `/questionnaire` を書き換えると、
  本アプリはサブパスの外の要求として 404 を返す。`ingress.annotations` に書き換えの注釈を足さない
- **Pleasanter の Ingress はチャートの外で、同じホスト名の `/` に用意する。** Application Routing の NGINX は
  同じホスト名の Ingress を束ね、パスを長い順に照合する。`/questionnaire` は `/` より先に当たる
  （ingress-nginx の Path Ordering）。Pleasanter を `/` 以外に置くと cookie が本アプリへ届かない
- **同じホスト名のどれか 1 つの Ingress に `use-regex` か `rewrite-target` があると、そのホストの全パスが
  大文字小文字を区別しない正規表現の照合になる**（同上）。Pleasanter の Ingress の注釈も確かめる
- TLS Secret は Ingress と同じ Namespace に要る。Pleasanter と別の Namespace に入れるなら、同じ証明書の
  TLS Secret を本アプリの Namespace にも作る（`ingress.tlsSecretName`）
- `config.forwardedNetworks` の考え方は変わらない
- **AKS の実機では未検証**（2026-09-25 時点。`helm lint`・`helm template` での描画だけを確認した）。
  Application Routing の NGINX は 2026 年 11 月で Microsoft のサポートが終わり、後継は Gateway API である
  （AKS Application Routing の文書）。このチャートは Ingress だけを描き、Gateway API の HTTPRoute は描かない

`values-subpath-sso.example.yaml` を `values-production.example.yaml` に重ねた例。

```bash
helm template questionnaire deploy/aks/questionnaire \
  --namespace questionnaire \
  --values questionnaire-production.yaml \
  --values deploy/aks/questionnaire/values-subpath-sso.example.yaml
```

```yaml
# 描画結果の抜粋（ConfigMap）
  QUESTIONNAIRE_PATH_BASE: "/questionnaire"
  QUESTIONNAIRE_PLEASANTERSSO_ENABLED: "true"
  QUESTIONNAIRE_PLEASANTERSSO_INTERNALBASEURL: "http://pleasanter.pleasanter.svc.cluster.local"
  QUESTIONNAIRE_PLEASANTERSSO_LOGINURL: "/users/login"
  QUESTIONNAIRE_PLEASANTERSSO_LOGOUTURL: "/users/logout"
# 描画結果の抜粋（Ingress）
    - host: "pleasanter.example.com"
      http:
        paths:
          - path: "/questionnaire"
            pathType: Prefix
```

## イメージを ACR へ発行する

`.github/workflows/aks-image.yml` を手動実行する。GitHub Environment
`aks-production` に次の Repository/Environment variables を設定する。

| 変数 | 内容 |
| --- | --- |
| `AZURE_CLIENT_ID` | GitHub OIDC で使う Microsoft Entra アプリまたは user-assigned managed identity |
| `AZURE_TENANT_ID` | Microsoft Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | ACR のある subscription ID |
| `AZURE_ACR_NAME` | ACR 名。`azurecr.io` は付けない |

Azure 側には GitHub Environment `aks-production` を subject とする federated credential を作り、
対象 identity へ ACR Tasks のビルド送信に必要な最小権限を割り当てる。
GitHub へ client secret は登録しない。

`source_ref` にはリリース済みタグまたは確定したコミット SHA を指定する。
`image_tag` を省略すると、そのコミット SHA の先頭12桁を使う。上書き可能な `latest` は禁止している。

```bash
gh workflow run aks-image.yml \
  -f source_ref=v0.2.0 \
  -f image_tag=v0.2.0
```

Actions の Summary に、発行したイメージ名と digest が出る。

## 初回導入

`values-production.example.yaml` を作業場所へコピーし、ACR、Pleasanter URL、
`forwardedNetworks`、公開 host、TLS Secret 名を環境に合わせる。
例の `10.244.0.0/16` は仮値なので必ず置き換える。資格情報は書かない。

```bash
cp deploy/aks/questionnaire/values-production.example.yaml questionnaire-production.yaml

helm lint deploy/aks/questionnaire \
  --values questionnaire-production.yaml

helm upgrade --install questionnaire deploy/aks/questionnaire \
  --namespace questionnaire \
  --values questionnaire-production.yaml \
  --atomic \
  --wait \
  --timeout 10m
```

Helm の `pre-install` hook がマイグレーション Job を1 Podだけ起動し、成功してから
Deployment を作る。DB 接続に失敗するかマイグレーションに失敗した場合、導入は止まる。
アプリ Pod の起動時の自動適用（`QUESTIONNAIRE_DB_AUTO_MIGRATE`）は v0.5.0 から既定で有効である。
チャートは既定ではこの値を渡さず、アプリの既定（有効）に従う。hook が先に当て終えているので、
Pod は未適用が無いことを確かめるだけで起動する。複数 Pod が同時に起動しても DB の排他で 1 つずつ処理するため、
スケールアウト時に競合しない（[`導入-更新運用手順書.md`](導入-更新運用手順書.md) 3.3）。

| 値 | 渡す環境変数 | 既定 | 内容 |
| --- | --- | --- | --- |
| `migration.autoMigrateOnStartup` | `QUESTIONNAIRE_DB_AUTO_MIGRATE` | `null`（渡さない。アプリの既定の `true`） | `false` で Pod 起動時の自動適用を止め、未適用があれば起動を止める |
| `migration.startupLockTimeoutSeconds` | `QUESTIONNAIRE_DB_MIGRATION_LOCK_TIMEOUT_SECONDS` | `null`（渡さない。アプリの既定の `300`） | Pod 起動時の自動適用で DB の排他を待つ上限秒 |

**既定を「渡さない（有効）」にした理由。** hook Job が先に適用するので、有効のままでも Pod は確かめるだけで、
余分なスキーマ変更は起きない。一方で hook を通らない経路（`kubectl set image` で直接イメージを変えた場合など）でも
Pod が自分で追いつける。複数 Pod の同時起動は DB の排他（`DatabaseMigrationLock`）で直列になり、
待つ上限が `startupLockTimeoutSeconds` である。待つ間も `/healthz` は応答し、`/ready` は 503 を返すので、
startupProbe で落とされずに Ready を待つ。

アプリ用の DB アカウントからスキーマ変更の権限を外す場合は `migration.autoMigrateOnStartup: false` にする。
**ただし hook Job も同じ Secret の `QUESTIONNAIRE_DB_CONNECTIONSTRING` を使う**ので、チャートだけでは
Job と Pod の権限を分けられない。分けるなら、導入-更新運用手順書 3.3 の手動コマンドを別の資格情報で流す運用をチャートの外で組む。

> 0.1.0 のチャートの手順書は、`QUESTIONNAIRE_DB_AUTO_MIGRATE=false` を事前作成した Secret に入れるよう
> 案内していた。Secret と ConfigMap に同じ鍵があると、`envFrom` で後に並ぶ Secret が勝つ
> （`templates/deployment.yaml`）。values へ移したら Secret からは消す。

確認する。

```bash
kubectl get deploy,pod,service,ingress,hpa,pdb,pvc \
  --namespace questionnaire

kubectl rollout status deployment/questionnaire-questionnaire \
  --namespace questionnaire \
  --timeout=5m

curl --fail https://questionnaire.example.com/healthz
curl --fail https://questionnaire.example.com/ready
```

サブパスに置いた場合は、同じホスト名の `/healthz` が Pleasanter へ振り分けられるので、
`https://pleasanter.example.com/questionnaire/healthz` と `/questionnaire/ready` で確かめる。

リソース名は release 名と chart 名から作る。`questionnaire` 以外の release 名を使った場合は
`kubectl get` で実名を確認する。

## 更新

DB のバックアップを取得し、新イメージを ACR へ発行してから values の `image.tag` を更新する。
マイグレーション内容と後方互換性は Release 本文で確認する。

適用前に、Helm 標準の `template` でレンダリング結果をレビューする。

```bash
helm template questionnaire deploy/aks/questionnaire \
  --namespace questionnaire \
  --values questionnaire-production.yaml > rendered.yaml
```

適用する。

```bash
helm upgrade questionnaire deploy/aks/questionnaire \
  --namespace questionnaire \
  --values questionnaire-production.yaml \
  --atomic \
  --wait \
  --timeout 10m
```

`pre-upgrade` hook のマイグレーションが先に成功し、その後 Deployment が
`maxUnavailable: 0` でローリング更新される。更新後は初回導入と同じ確認を行う。

## 失敗時とロールバック

### migration Job を調べる

成功した hook Job は自動削除する。失敗した Job は調査のため残す。

```bash
kubectl get jobs --namespace questionnaire
kubectl logs job/questionnaire-questionnaire-migrate --namespace questionnaire
kubectl describe job questionnaire-questionnaire-migrate --namespace questionnaire
```

接続文字列、DB のファイアウォール、名前解決、TLS 設定を直してから同じ `helm upgrade` を再実行する。

### アプリをロールバックする

```bash
helm history questionnaire --namespace questionnaire
helm rollback questionnaire <REVISION> \
  --namespace questionnaire \
  --wait \
  --timeout 10m
```

Helm rollback では `pre-upgrade` hook を実行しない。DB マイグレーションは自動で戻らない。
旧アプリと新 DB に後方互換性がない場合は、先に取得した DB バックアップから復元するか、
互換性を戻す修正版イメージを発行する。判断できない場合は Pod だけを戻さない。

### Pod が Ready にならない

```bash
kubectl get pods --namespace questionnaire
kubectl describe pod <Pod名> --namespace questionnaire
kubectl logs <Pod名> --namespace questionnaire
kubectl get events --namespace questionnaire --sort-by=.lastTimestamp
```

- `/healthz` が失敗する: プロセス起動、ポート、リソース不足を確認する
- `/ready` だけ失敗する: DB 接続、Secret、名前解決、TLS、ファイアウォールを確認する
- PVC が Pending: `dataProtection.storageClassName` と Azure Files CSI を確認する
- 片方の Pod へ移るとログアウトする: Data Protection PVC の mount と書き込み権限を確認する
- ImagePullBackOff: AKS kubelet identity の ACR pull 権限とイメージタグを確認する

## バックアップと定期確認

- DB は組織の RPO/RTO に合わせてバックアップと復元試験を行う
- Data Protection PVC を削除・作り直す前に鍵束を退避する
- `QUESTIONNAIRE_SECRET_KEY` の控えを Key Vault 等で保全する
- TLS 証明書と OIDC federated credential の期限・subject を定期確認する
- HPA の replica 数、CPU/メモリ、再起動回数、`/ready` の失敗を監視する
- AKS、node image、Ingress、Azure Files CSI の更新方針をクラスター運用側で定める

## 参照した一次情報

いずれも 2026-08-30 参照。

- [AKS Deployment Safeguards](https://learn.microsoft.com/azure/aks/deployment-safeguards)
- [Azure Files CSI を AKS で使う](https://learn.microsoft.com/azure/aks/azure-files-csi)
- [AKS Application Routing](https://learn.microsoft.com/azure/aks/app-routing)
- [ACR Tasks の概要](https://learn.microsoft.com/azure/container-registry/container-registry-tasks-overview)
- [GitHub Actions から Azure への OpenID Connect](https://learn.microsoft.com/azure/developer/github/connect-from-azure-openid-connect)
- [Helm chart hook](https://helm.sh/docs/topics/charts_hooks/)

2026-09-25 参照。

- [ingress-nginx: Path Ordering and Matching](https://kubernetes.github.io/ingress-nginx/user-guide/ingress-path-matching/)
  — パスを長い順に並べる。同じホストの Ingress の `use-regex`・`rewrite-target` は全パスに効く
- [AKS Application Routing（NGINX）](https://learn.microsoft.com/azure/aks/app-routing)
  — NGINX のアドオンへの Microsoft のサポートは 2026 年 11 月まで。後継は Gateway API
