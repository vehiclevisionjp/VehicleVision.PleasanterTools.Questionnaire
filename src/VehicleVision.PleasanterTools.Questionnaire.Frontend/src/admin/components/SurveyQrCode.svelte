<script lang="ts">
  import QRCode from 'qrcode';
  import { t } from '../lib/i18n/state.svelte';

  /**
   * 回答用 URL の QR コード（Issue #57）。
   *
   * **画面の中だけで描き、サーバへは送らない。** 公開用 URL をサーバへ渡すと、
   * 経路の記録や監査ログに公開用 ID が増える。**公開用 ID は推測不能な値であること
   * が前提**（`_documents/データモデル設計.md` 3 章）なので、増やす先は少ないほどよい。
   *
   * **紙で配るためのもの。** 画面で読ませるなら URL を押させれば済む。
   * 画像として保存できるようにしてある。
   */
  interface Props {
    /** QR にする回答用 URL。**そのまま符号化する。** */
    url: string;
    /** 見出しに出すアンケートの題名。 */
    title: string;
    /** 保存する画像の名前に使う。**題名は使わない**（ファイル名に使えない文字が入る）。 */
    publicId: string;
    onclose: () => void;
  }

  let { url, title, publicId, onclose }: Props = $props();

  let dataUrl = $state('');
  let failed = $state(false);

  /**
   * 印刷して読める大きさで作る。
   *
   * **画面の見た目は CSS で縮める。** 表示に合わせて小さく作ると、
   * 保存した画像を紙に印刷したときに潰れる。
   */
  const IMAGE_WIDTH = 512;

  $effect(() => {
    // **URL が変わったら描き直す。** 別のアンケートの QR を出したままにしない
    const target = url;
    void draw(target);
  });

  async function draw(target: string) {
    try {
      dataUrl = await QRCode.toDataURL(target, { margin: 2, width: IMAGE_WIDTH });
      failed = false;
    } catch {
      // **読み取れない QR を出すより、出さない方がまし。** URL は下に文字でも出している
      dataUrl = '';
      failed = true;
    }
  }
</script>

<div class="qr-panel">
  <h2>{t('qr.title', { title })}</h2>
  <p class="hint">{t('qr.description')}</p>

  {#if failed}
    <p class="error" role="alert">{t('qr.failed')}</p>
  {:else if dataUrl}
    <img class="qr" src={dataUrl} alt={t('qr.alt')} width={IMAGE_WIDTH} height={IMAGE_WIDTH} />
  {/if}

  <!-- **URL も文字で出す。** 読み取れない相手には打ってもらうしかない -->
  <p class="url"><code>{url}</code></p>

  <div class="actions">
    {#if dataUrl}
      <!-- **data URL をそのまま落とす。** サーバへ取りに行かないので、
           公開用 URL がサーバの記録に増えない -->
      <a class="download" href={dataUrl} download="qr-{publicId}.png">{t('qr.download')}</a>
    {/if}
    <button type="button" class="secondary" onclick={onclose}>{t('qr.close')}</button>
  </div>
</div>

<style lang="scss">
  .qr-panel {
    padding: 1.25rem;
    margin-bottom: 1.5rem;
    background: #fff;
    border: 1px solid var(--border);
    border-radius: 8px;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  .hint {
    margin: 0 0 1rem;
    color: var(--muted);
    font-size: 0.8rem;
  }

  .qr {
    display: block;
    /* **保存する画像は大きいまま。** 画面に出す分だけ縮める */
    width: 15rem;
    height: auto;
    max-width: 100%;
    border: 1px solid var(--border);
    border-radius: 4px;
  }

  .url {
    margin: 0.75rem 0;
    font-size: 0.85rem;

    code {
      padding: 0.35rem 0.5rem;
      background: var(--bg);
      border-radius: 4px;
      word-break: break-all;
    }
  }

  .actions {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .download {
    color: var(--accent);
    font-size: 0.9rem;
  }

  .error {
    color: var(--error);
    font-size: 0.9rem;
  }
</style>
