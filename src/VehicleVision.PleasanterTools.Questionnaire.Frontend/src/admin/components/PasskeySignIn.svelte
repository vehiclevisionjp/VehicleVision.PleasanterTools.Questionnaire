<script lang="ts">
  /**
   * ログイン画面の「パスキー」の区画（Issue #1068。Core の仕様書 3.10.4）。
   *
   * **ユーザー名は取らない**（ディスコバラブル資格情報。端末が資格情報を選ぶ）。
   * 「パスキーでログイン」を押した 1 回の操作で始める。**成功してもここで「ログイン完了」とは言わず**、
   * サーバが Session を発行したあと `onadvance` で状態を読み直す。
   * 非対応のブラウザでは、動かない釦を残さず、理由を出す（DesignReference の契約）。
   * ⚠️ **PIN・生体情報はブラウザーと端末が扱う。この画面は入力欄を作らない。**
   */
  import { passkeyApi } from '../lib/api';
  import { failureMessage } from '../lib/passkeys';
  import { passkeySupport, signInWithPasskey } from '../lib/webauthn';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    /** ログインが通ったので、状態を読み直してほしい。 */
    onadvance: () => void;
  }

  let { onadvance }: Props = $props();

  const support = passkeySupport(window);

  let busy = $state(false);
  let error = $state('');
  let controller: AbortController | null = null;

  async function start() {
    error = '';
    busy = true;
    controller = new AbortController();

    const outcome = await signInWithPasskey(passkeyApi, navigator.credentials, {
      signal: controller.signal,
    });
    controller = null;
    busy = false;

    if (!outcome.ok) {
      const message = failureMessage(outcome.failure, 'signIn');
      error = 'key' in message ? t(message.key) : message.serverMessage;
      return;
    }

    onadvance();
  }

  /** 進行中の操作を取り消す。**未完了として扱う**（エラーにはしない）。 */
  function cancel() {
    controller?.abort();
  }
</script>

<div class="passkey">
  {#if support !== 'ok'}
    <p class="hint" role="status">
      {support === 'insecure' ? t('passkey.signIn.insecureContext') : t('passkey.signIn.unsupported')}
    </p>
  {:else}
    <p class="hint">{t('passkey.signIn.lead')}</p>

    {#if error}<p class="error" role="alert">{error}</p>{/if}

    {#if busy}
      <p class="hint" role="status">{t('passkey.signIn.waiting')}</p>
      <button type="button" class="secondary" data-vv-login-action onclick={cancel}>
        {t('passkey.signIn.cancel')}
      </button>
    {:else}
      <button type="button" class="primary" data-vv-login-action onclick={start}>
        {t('passkey.signIn.button')}
      </button>
    {/if}
  {/if}
</div>

<style lang="scss">
  .hint {
    color: var(--muted);
    font-size: 0.85rem;
    margin: 0 0 var(--space-3);
  }

  .error {
    color: var(--error);
    font-size: 0.9rem;
  }

  button {
    display: block;
    width: 100%;
    min-height: 48px;
    padding: 0.6rem;
    border-radius: var(--radius-sm);
    font: inherit;
    cursor: pointer;
  }

  .primary {
    border: 0;
    background: var(--accent);
    color: var(--accent-text);
  }

  .secondary {
    border: 1px solid var(--border);
    background: var(--surface);
    color: inherit;
  }
</style>
