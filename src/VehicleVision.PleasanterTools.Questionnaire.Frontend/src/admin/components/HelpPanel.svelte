<script lang="ts">
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    onback: () => void;
  }

  let { onback: _onback }: Props = $props();
</script>

<section aria-labelledby="help-title">
  <header class="head">
    <h1 id="help-title">{t('help.title')}</h1>
  </header>

  <p class="lead">{t('help.lead')}</p>

  <section class="guide" aria-labelledby="help-workflow">
    <h2 id="help-workflow">{t('help.workflowTitle')}</h2>
    <ol>
      <li>{t('help.create')}</li>
      <li>{t('help.questions')}</li>
      <li>{t('help.mapping')}</li>
      <li>{t('help.publish')}</li>
    </ol>
  </section>

  <section class="guide" aria-labelledby="help-pleasanter">
    <h2 id="help-pleasanter">{t('help.pleasanterTitle')}</h2>
    <ul>
      <li>{t('help.siteId')}</li>
      <li>{t('help.apiKey')}</li>
      <li>{t('help.columns')}</li>
    </ul>
  </section>

  <section class="guide" aria-labelledby="help-delivery">
    <h2 id="help-delivery">{t('help.deliveryTitle')}</h2>
    <ul>
      <li>{t('help.share')}</li>
      <li>{t('help.arrival')}</li>
    </ul>
  </section>

  <section class="guide" aria-labelledby="help-troubleshooting">
    <h2 id="help-troubleshooting">{t('help.troubleshootingTitle')}</h2>
    <ul>
      <li>{t('help.backlog')}</li>
      <li>{t('help.failed')}</li>
      <li>{t('help.mappingValidation')}</li>
    </ul>
  </section>
</section>

<style lang="scss">
  .head {
    display: flex;
    align-items: center;
    gap: 1rem;
  }

  h1 {
    margin: 0;
    font-size: 1.25rem;
  }

  h2 {
    margin: 0;
    font-size: 1rem;
  }

  .lead {
    color: var(--muted);
  }

  .guide {
    margin-top: 1.5rem;
    padding: 1.25rem;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--surface);
  }

  ol,
  ul {
    margin: 0.75rem 0 0;
    padding-left: 1.5rem;
  }

  li + li {
    margin-top: 0.75rem;
  }
</style>
