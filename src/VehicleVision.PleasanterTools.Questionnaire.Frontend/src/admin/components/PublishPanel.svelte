<script lang="ts">
  import { onMount } from 'svelte';
  import { appUrl } from '../../lib/basePath';
  import { loadEmbedParentOptions, saveSurveySettings } from '../lib/api';
  import { iframeTag } from '../lib/iframeTag';
  import { t } from '../lib/i18n/state.svelte';
  import SurveyQrCode from './SurveyQrCode.svelte';

  /**
   * 公開タブ（Issue #580）。回答用 URL・QR コード・公開設定を 1 か所に置く。
   *
   * **他のタブと保存の性質が違う。** 設問などは下書きにだけ効くが、ここは保存すると
   * 公開中の回答画面へ即時に反映される（定義ではなく運用の設定。公開し直しは要らず、
   * 版にも `DraftRevision` にも入らない）。**混ぜると「保存したのに効かない」と誤解される**ので、
   * 独自の保存ボタンを持ち、その旨を明記する。
   */
  interface Props {
    surveyId: string;
    title: string;
    /** 回答用 URL に使う公開用 ID。古いサーバが返さなかったときは空。 */
    publicId: string;
    /** 公開状態。0 下書き / 1 本公開 / 2 停止中 / 3 テスト公開中。 */
    status: number;
    /**
     * 設定を保存してよい相手か（`surveys.publish`）。
     *
     * **編集（`surveys.write`）だけできる人には読み取り専用で見せる。**
     * 隠すと「どこで設定するのか」が分からなくなる。サーバ側でも同じ判定をしている。
     */
    canPublish: boolean;
    responseLimit: number | null;
    requireProofOfWork: boolean;
    allowDraft: boolean;
    allowEmbedding: boolean;
    /** 受付の開始日時（UTC, ISO 8601）。無ければ開始の制限なし（Issue #1006）。 */
    acceptFrom: string | null;
    /** 受付の終了日時（UTC, ISO 8601）。無ければ終了の制限なし（Issue #1006）。 */
    acceptTo: string | null;
    onsaved: (values: {
      responseLimit: number | null;
      requireProofOfWork: boolean;
      allowDraft: boolean;
      allowEmbedding: boolean;
      acceptFrom: string | null;
      acceptTo: string | null;
    }) => void;
  }

  let {
    surveyId,
    title,
    publicId,
    status,
    canPublish,
    responseLimit,
    requireProofOfWork,
    allowDraft,
    allowEmbedding,
    acceptFrom,
    acceptTo,
    onsaved,
  }: Props = $props();

  /**
   * UTC の ISO 文字列を、`datetime-local` 入力が受ける現地時刻（`YYYY-MM-DDTHH:mm`）にする。
   * **入力欄は現地時刻、サーバへは UTC で送る**（Issue #1006）。
   */
  function toLocalInput(iso: string | null): string {
    if (!iso) {
      return '';
    }
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
      return '';
    }
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }

  /** `datetime-local` の値を UTC の ISO 文字列にする。空は `null`。 */
  function fromLocalInput(value: string): string | null {
    if (value.trim() === '') {
      return null;
    }
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  }

  /** 回答数の上限。**空欄は「上限なし」。** */
  let limitText = $state('');
  let proofOfWork = $state(true);
  let draftEnabled = $state(false);
  let embedding = $state(false);
  /** 受付期間（現地時刻）。**空欄は「その側の制限なし」。** */
  let fromText = $state('');
  let toText = $state('');
  let busy = $state(false);
  let error = $state('');
  let saved = $state(false);
  /** 運用側が親サイトを 1 つ以上許可しているか。 */
  let embedParentsEnabled = $state(false);

  // **保存済みの値が変わったら入力欄を合わせる**（別のアンケートを開いた・読み直した）
  $effect(() => {
    limitText = responseLimit == null ? '' : String(responseLimit);
    proofOfWork = requireProofOfWork;
    draftEnabled = allowDraft;
    embedding = allowEmbedding;
    fromText = toLocalInput(acceptFrom);
    toText = toLocalInput(acceptTo);
  });

  onMount(async () => {
    const result = await loadEmbedParentOptions();
    if (result.ok) {
      embedParentsEnabled = result.value.enabled;
    }
  });

  /** 公開しているか（本公開・停止中・テスト公開中）。下書きには回答用 URL がまだ無い。 */
  const published = $derived(status === 1 || status === 2 || status === 3);

  /** 回答用 URL。**公開用 ID しか出さない。** サブパスに置いたときはサブパス込み（Issue #465）。 */
  const formUrl = $derived(`${location.origin}${appUrl(`/f/${publicId}`)}`);

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (busy || !canPublish) {
      return;
    }

    error = '';
    saved = false;

    // **空欄は「上限なし」。** 0 を送らない（サーバも断るが、ここで伝える方が早い）
    const trimmed = limitText.trim();
    let limit: number | null = null;
    if (trimmed !== '') {
      limit = Number(trimmed);
      if (!Number.isInteger(limit) || limit <= 0) {
        error = t('settings.limitInvalid');
        return;
      }
    }

    // **終了は開始より後に。** サーバも断るが、ここで伝える方が早い
    const from = fromLocalInput(fromText);
    const to = fromLocalInput(toText);
    if (from !== null && to !== null && new Date(to) <= new Date(from)) {
      error = t('settings.acceptPeriodInvalid');
      return;
    }

    busy = true;
    const result = await saveSurveySettings(
      surveyId,
      limit,
      proofOfWork,
      draftEnabled,
      embedding,
      from,
      to,
    );
    busy = false;

    if (!result.ok) {
      error = result.message;
      return;
    }

    saved = true;
    onsaved({
      ...result.value,
      acceptFrom: result.value.acceptFrom ?? null,
      acceptTo: result.value.acceptTo ?? null,
    });
  }
</script>

<section class="publish">
  <h2>{t('publish.title')}</h2>

  {#if published && publicId !== ''}
    <SurveyQrCode url={formUrl} {title} {publicId} />
  {:else}
    <p class="hint">{t('publish.notPublished')}</p>
  {/if}

  <form class="settings" onsubmit={save}>
    <!-- **ここだけ保存の性質が違う**ことを、保存ボタンの前に必ず読ませる -->
    <p class="warn">{t('publish.immediateNotice')}</p>
    {#if !canPublish}
      <p class="hint">{t('publish.readOnly')}</p>
    {/if}

    <fieldset disabled={!canPublish || busy}>
      <label>
        {t('settings.responseLimit')}
        <input type="text" inputmode="numeric" bind:value={limitText} />
        <span class="hint">{t('settings.responseLimitHint')}</span>
      </label>
      <p class="hint">{t('settings.noAutoResume')}</p>

      <!-- **受付期間**（Issue #1006）。期間外は回答者に理由を出さず受付を止める -->
      <label>
        {t('settings.acceptFrom')}
        <input type="datetime-local" bind:value={fromText} />
      </label>
      <label>
        {t('settings.acceptTo')}
        <input type="datetime-local" bind:value={toText} />
      </label>
      <p class="hint">{t('settings.acceptPeriodHint')}</p>

      <!-- **切っても他の bot 対策は外れない**ことを併せて出す（Issue #66） -->
      <label class="check">
        <input type="checkbox" bind:checked={proofOfWork} />
        {t('settings.proofOfWork')}
      </label>
      <p class="hint">{t('settings.proofOfWorkHint')}</p>

      <!-- **既定は無効。** 端末は共有され得るので、入れる前に読ませる（Issue #59） -->
      <label class="check">
        <input type="checkbox" bind:checked={draftEnabled} />
        {t('settings.allowDraft')}
      </label>
      <p class="hint">{t('settings.allowDraftHint')}</p>
      {#if draftEnabled}
        <p class="warn">{t('settings.allowDraftWarning')}</p>
      {/if}

      <label class="check">
        <input type="checkbox" bind:checked={embedding} />
        {t('settings.allowEmbedding')}
      </label>
      <p class="hint">{t('settings.allowEmbeddingHint')}</p>
      {#if !embedParentsEnabled}
        <p class="warn">{t('settings.allowEmbeddingUnavailable')}</p>
      {:else if embedding && published && publicId !== ''}
        <label>
          {t('settings.iframeTag')}
          <textarea readonly rows="3" value={iframeTag(formUrl, title, publicId)}></textarea>
        </label>
        <p class="hint">{t('settings.iframeTagHint')}</p>
      {/if}
      <p class="hint">{t('settings.proofOfWorkKeepsOthers')}</p>
    </fieldset>

    {#if error}<p class="error" role="alert">{error}</p>{/if}
    {#if saved}<p class="saved" role="status">{t('publish.saved')}</p>{/if}

    {#if canPublish}
      <div class="actions">
        <button type="submit" disabled={busy}>{t('settings.submit')}</button>
      </div>
    {/if}
  </form>
</section>

<style lang="scss">
  .publish {
    display: grid;
    gap: var(--space-3);
    padding: 1.25rem;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius-md);
  }

  h2 {
    margin: 0;
    font-size: 1.05rem;
  }

  .settings,
  fieldset {
    display: grid;
    gap: var(--space-3);
  }

  fieldset {
    min-width: 0;
    padding: 0;
    margin: 0;
    border: 0;
  }

  label.check {
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }

  label.check input {
    display: inline-block;
    width: auto;
    margin-top: 0;
  }

  .hint {
    display: block;
    margin: var(--space-1) 0 0;
    color: var(--muted);
    font-size: 0.8rem;
  }

  .warn {
    margin: 0;
    padding: var(--space-2) var(--space-3);
    background: var(--bg);
    border-left: 3px solid var(--error);
    font-size: 0.85rem;
  }

  .error {
    margin: 0;
    color: var(--error);
  }

  .saved {
    margin: 0;
    color: var(--success);
    font-size: 0.9rem;
  }

  .actions {
    display: flex;
    gap: var(--space-2);
  }
</style>
