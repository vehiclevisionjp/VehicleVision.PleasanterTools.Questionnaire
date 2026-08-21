<script lang="ts">
  import QuestionField from './components/QuestionField.svelte';
  import {
    forgetSubmission,
    hasSubmitted,
    loadForm,
    loadPendingAnswers,
    requestTicket,
    submitAnswers,
  } from './lib/api';
  import type { Attachment } from './lib/api';
  import { solveAltcha } from './lib/altcha';
  import { toSteps, tracePath } from './lib/flow';
  import type { AnswerState, PayloadAnswer, RejectionReason, SurveyDefinition } from './lib/types';
  import { isDisplayOnly, text } from './lib/types';
  import { validatePage } from './lib/validation';
  import {
    applyDocumentLanguage,
    browserLanguages,
    LANGUAGE_NAMES,
    negotiateLanguage,
    SUPPORTED_LANGUAGES,
    type Language,
  } from './lib/i18n/language';
  import { serverValidationKey, translator, type MessageKey } from './lib/i18n/messages';
  import { applyTheme, headerImageUrl } from './lib/theme';
  import { clearDraft, hasDraft, readDraft, saveDraft } from './lib/draft';

  type Screen = 'loading' | 'answering' | 'answered' | 'completed' | 'rejected' | 'error';

  /**
    * 画面に出す言語。
    *
    * **URL の `?lang=` → ブラウザの言語設定 → `ja`**
    * （`_documents/多言語対応方針.md` 2 章）。
    * **サーバへは送らず、どこにも保存しない。**
    * 保存すると「回答済みの印」と組み合わさって回答者を絞り込む材料になる。
    */
  let language = $state<Language>(
    negotiateLanguage(new URLSearchParams(location.search).get('lang'), browserLanguages()),
  );

  const t = $derived(translator(language));

  $effect(() => {
    // **読み上げの声と行折り返しが変わる。** `<html lang>` を合わせておく
    applyDocumentLanguage(language);
  });

  $effect(() => {
    // **題名も言語に合わせる。** タブに出るのはこれ
    const title = definition ? text(definition.title, language) : '';
    if (title !== '') {
      document.title = title;
    }
  });

  /**
    * 言語を切り替える。**URL を書き換えるだけ。**
    *
    * Web Storage にも Cookie にもサーバにも残さない。
    * 共有された URL がそのまま言語の指定になる。
    */
  function changeLanguage(next: Language) {
    language = next;
    const url = new URL(location.href);
    url.searchParams.set('lang', next);
    history.replaceState(null, '', url);
  }

  let screen = $state<Screen>('loading');
  let rejection = $state<RejectionReason>();
  let definition = $state<SurveyDefinition>();
  let publicId = $state('');
  let responseToken = $state('');
  /** 送信チケット。**画面を開いたときにサーバから受け取る。** */
  let ticket = $state('');
  /** ハニーポット項目。**人が触らない場所に置いてあるので、空のままのはず。** */
  let trap = $state('');
  /**
   * proof-of-work の解答（Issue #55）。
   *
   * **画面を開いた時点から解き始める。** 書き終えるころには計算が済んでいるので、
   * 送信の時に待たせない。
   */
  let altcha = $state('');
  /**
   * このアンケートが proof-of-work を要るとしているか（Issue #66）。
   *
   * **要らないアンケートでは解かない。** 課題は要否に関わらず届く
   * （出し分けると公開 ID の実在が漏れる）ので、**解くかどうかはここで決める。**
   *
   * **これは待ち時間の話でしかない。** 受け付けるかどうかを決めるのはサーバ側で、
   * ここを false にしても送信が通るようにはならない。
   */
  let requiresProofOfWork = $state(true);
  /**
   * 下書きを端末へ残してよいか（Issue #59）。
   *
   * ⚠️ **分からなければ残さない。** 端末は共有され得るので、
   * 判断が付かないときは残さない側へ倒す。
   */
  let allowsDraft = $state(false);
  /** 端末に前回の下書きがあるか。**勝手には戻さない。** */
  let draftFound = $state(false);
  /** 下書きから戻したことの知らせ。 */
  let draftNotice = $state('');
  /** 今どの区切りを見ているか。**ページではなく「区切り」の番号**（1 問 1 ページ表示があるため） */
  let stepIndex = $state(0);
  let answers = $state<Record<string, AnswerState>>({});
  let errors = $state<Record<string, string>>({});
  let submitting = $state(false);
  let submitError = $state('');
  /** 前に送った回答を読めたか。**読めなければ編集を出せない。** */
  let canEdit = $state(false);
  /** 添付を受け付けなかった理由。**どのファイルが駄目かを出す。** */
  let attachmentMessages = $state<string[]>([]);

  /**
   * 回答から辿る経路。**答えを変えると変わる。**
   *
   * **正はサーバ側**（`Core/Flow/SurveyFlow.cs`）。ここはその写しで、
   * 画面を進めるためだけのもの（`lib/flow.ts`）。
   */
  const path = $derived(tracePath(definition, answers));

  $effect(() => {
    // **管理者が決めた見た目を反映する**（Issue #56）。
    // **CSS のカスタムプロパティへ入れるだけ**で、スタイル表は組み立てない
    // （`lib/theme.ts`）。設定が無ければ何も入らず、今までの見た目のまま
    applyTheme(document.documentElement, definition?.theme);
  });

  /** ヘッダ画像の URL。**本アプリの口だけを指す**（外部へ取りに行かない）。 */
  const headerImage = $derived(definition ? headerImageUrl(publicId, definition.theme) : null);

  /** 画面に出す区切り。**1 問 1 ページ表示なら 1 設問で 1 区切り。** */
  const steps = $derived(toSteps(path, definition?.displayMode ?? 'Paged'));

  const currentStep = $derived(steps[Math.min(stepIndex, Math.max(steps.length - 1, 0))]);
  const currentPage = $derived(currentStep?.page);
  const isLastStep = $derived(stepIndex >= steps.length - 1);

  // **進みは経路の長さで測る。** 全ページ数で測ると、飛ばした先で 100% にならない
  const progress = $derived(
    steps.length === 0 ? 0 : Math.round(((Math.min(stepIndex, steps.length - 1) + 1) / steps.length) * 100),
  );

  /** URL の `/f/{publicId}` から公開 ID を取る。 */
  function readPublicId(): string {
    const match = /^\/f\/([^/]+)/.exec(location.pathname);
    return match?.[1] ?? '';
  }

  function emptyAnswer(): AnswerState {
    return { values: [], otherText: '' };
  }

  $effect(() => {
    void start();
  });

  $effect(() => {
    // **経路が縮んだら、行き過ぎた位置を戻す。**
    // 前の答えを変えると、今いた区切りが無くなることがある
    if (steps.length > 0 && stepIndex > steps.length - 1) {
      stepIndex = steps.length - 1;
    }
  });

  $effect(() => {
    // **通らなくなった設問の入力を消す。**
    // 残すと「見えないのに送られる」ことになり、サーバ側で落ちて食い違う
    // （`ResponseIntake` が隠れた回答を落とす）。
    // **消した結果で経路がさらに変わっても、消す対象は増えるだけなので落ち着く。**
    for (const [questionId, answer] of Object.entries(answers)) {
      if (path.visible.has(questionId)) {
        continue;
      }

      const hasInput =
        answer.values.length > 0 || answer.otherText !== '' || (answer.files?.length ?? 0) > 0;

      if (hasInput) {
        answers[questionId] = emptyAnswer();
      }
    }
  });

  async function start() {
    publicId = readPublicId();
    if (publicId === '') {
      screen = 'error';
      return;
    }

    const result = await loadForm(publicId);
    if (!result.form) {
      rejection = result.rejection ?? 'notFound';
      screen = 'rejected';
      return;
    }

    definition = result.form.definition;

    // **サーバが null の項目を落として返す**ので、`?? true` で受ける。
    // **分からなければ解く側に倒す**（解かずに送って断られる方が重い）
    requiresProofOfWork = result.form.requiresProofOfWork ?? true;

    // **分からなければ残さない側へ倒す**（Issue #59）
    allowsDraft = result.form.allowsDraft ?? false;

    // **回答トークンと送信チケットをサーバから受け取る。**
    // チケットが無いと送信できないので、ここで失敗したら回答させない
    const issued = await requestTicket(publicId);
    if (!issued) {
      screen = 'error';
      return;
    }

    responseToken = issued.responseToken;
    ticket = issued.ticket;

    // **待たない。** 解けたら入るだけで、入力は先に進められる。
    // **要らないアンケートでは解かない**（Issue #66）
    if (requiresProofOfWork && issued.altcha) {
      void solveAltcha(issued.altcha).then((solved) => {
        altcha = solved ?? '';
      });
    }

    for (const page of definition.pages) {
      for (const question of page.questions) {
        answers[question.questionId] = emptyAnswer();
      }
    }

    // **送信待ちを先に見る。** 未送信の回答は Pleasanter にまだ無い
    const pending = await loadPendingAnswers(publicId, responseToken);
    if (pending) {
      canEdit = true;
      for (const answer of pending) {
        answers[answer.questionId] = {
          values: answer.values ?? [],
          otherText: answer.otherText ?? '',
        };
      }
    }

    // **下書きは勝手に戻さない**（Issue #59）。
    // 共有の端末では、前に使った人の回答をそのまま見せることになる。
    // **サーバから読めた回答（編集）の方が確かなので、そちらがあれば下書きは出さない**
    draftFound = allowsDraft && !canEdit && hasDraft(publicId);

    // **この端末から回答済みなら、いきなり空のフォームを出さない**
    // （`_documents/画面設計.md` 1 章「既に回答済み」）。
    // **消されたら分からない。** これは防止ではなく抑止
    screen = hasSubmitted(publicId) ? 'answered' : 'answering';
  }

  /**
   * 書いた内容を端末へ残す（Issue #59）。
   *
   * **許可されたアンケートで、回答中のときだけ。**
   * 完了画面や停止中の画面で書き足すことはない。
   *
   * **打つたびに書く。** 落ちる直前まで残っていてほしいので、間引かない。
   * 書く先は端末の Web Storage で、通信は起きない。
   */
  $effect(() => {
    // **中身を読んでから判定する。** 先に return すると、
    // 回答の変化を見張る対象から外れて 2 度と走らない
    const snapshot = $state.snapshot(answers) as Record<string, AnswerState>;

    if (!allowsDraft || screen !== 'answering') return;

    // ⚠️ **戻すかどうかを聞いている間は書かない。**
    // 読み込み直後の回答は空なので、書くと「全部消した」と見なして
    // **置いてある下書きを、戻す前に消してしまう**
    if (draftFound) return;

    saveDraft(publicId, snapshot);
  });

  /** 下書きから戻す。**回答者が押したときだけ。** */
  function restoreDraft() {
    const draft = readDraft(publicId);
    draftFound = false;

    if (draft === null) {
      // 期限切れなどで消えていた
      return;
    }

    for (const [questionId, answer] of Object.entries(draft)) {
      // **今の定義に無い設問は戻さない。** 公開し直しで設問が消えていることがある
      if (answers[questionId] === undefined) continue;

      answers[questionId] = { ...answer };
    }

    draftNotice = t('draft.restored');
  }

  /** 下書きを捨てる。**共有の端末で、前の人の回答を消せる口。** */
  function discardDraft() {
    clearDraft(publicId);
    draftFound = false;
    draftNotice = t('draft.discarded');
  }

  /**
   * 区切りを移るときに、その区切り分だけ見る。
   *
   * **出している設問だけを見る。** 条件で隠れている設問の必須を求めると、
   * 答えようのない設問で止まる。
   */
  function checkCurrentStep(): boolean {
    if (!currentStep) return true;
    errors = validatePage(currentStep.questions, answers, t);
    return Object.keys(errors).length === 0;
  }

  function goNext() {
    if (!checkCurrentStep()) return;
    stepIndex = Math.min(stepIndex + 1, Math.max(steps.length - 1, 0));
    errors = {};
    window.scrollTo({ top: 0 });
  }

  function goBack() {
    stepIndex = Math.max(stepIndex - 1, 0);
    errors = {};
    window.scrollTo({ top: 0 });
  }

  /** 選ばれた添付を、設問と組にして取り出す。 */
  function toAttachments(): Attachment[] {
    return Object.entries(answers).flatMap(([questionId, answer]) =>
      (answer.files ?? []).map((file) => ({ questionId, file })),
    );
  }

  /** 添付が受け付けられなかった理由。**符号を文言へ当てるだけ。** */
  const ATTACHMENT_MESSAGES: Record<string, MessageKey> = {
    extensionNotAllowed: 'attachment.extensionNotAllowed',
    contentDoesNotMatchExtension: 'attachment.contentDoesNotMatchExtension',
    tooLarge: 'attachment.tooLarge',
    totalTooLarge: 'attachment.totalTooLarge',
    tooMany: 'attachment.tooMany',
    invalidFileName: 'attachment.invalidFileName',
  };

  /** 添付が受け付けられなかった理由を、回答者に分かる言葉にする。 */
  function attachmentMessage(reason: string, fileName?: string): string {
    const name = fileName ?? t('attachment.defaultName');

    // **検出したことは伝えない**（サーバ側も理由を丸めて返す）
    return t(ATTACHMENT_MESSAGES[reason] ?? 'attachment.unknown', { name });
  }

  /** サーバが返した検証エラーの符号を文言へ当てる。 */
  function serverValidationMessage(code: string): string {
    return t(serverValidationKey(code));
  }

  /** 何も選んでいない行を落とす。**全部空なら行そのものを送らない。** */
  function compactRows(
    rows: Record<string, string[]> | undefined,
  ): Record<string, string[]> | undefined {
    if (rows === undefined) return undefined;

    const kept = Object.entries(rows).filter(([, values]) =>
      values.some((value) => value.trim() !== ''),
    );

    return kept.length === 0 ? undefined : Object.fromEntries(kept);
  }

  function toPayload(): PayloadAnswer[] {
    return Object.entries(answers)
      .filter(([questionId]) => {
        // **出していない設問は送らない。** サーバ側でも落とすが、
        // 送らない方が「見えないのに送られた」を作らずに済む
        if (!path.visible.has(questionId)) {
          return false;
        }

        const question = path.pages
          .flatMap((page) => page.questions)
          .find((candidate) => candidate.questionId === questionId);

        return question !== undefined && !isDisplayOnly(question);
      })
      .map(([questionId, answer]) => ({
        questionId,
        values: answer.values,
        otherText: answer.otherText === '' ? undefined : answer.otherText,
        // **空の行は送らない。** 何も選んでいない行まで送ると、
        // 正本 JSON に空の入れ物が並ぶ
        rows: compactRows(answer.rows),
      }));
  }

  async function submit() {
    if (!checkCurrentStep()) return;

    submitting = true;
    submitError = '';
    attachmentMessages = [];

    try {
      const result = await submitAnswers(
        publicId,
        responseToken,
        toPayload(),
        { ticket, trap, altcha },
        toAttachments(),
      );
      if (result.accepted) {
        canEdit = true;
        // **送れたら下書きは要らない**（Issue #59）。端末へ残し続けない
        clearDraft(publicId);
        draftFound = false;
        screen = 'completed';
        return;
      }

      // **受付そのものを断られた場合だけ画面を切り替える。**
      // それ以外は入力内容を残したまま、その場でやり直させる
      if (result.rejection === 'rejected' || result.rejection === 'tooManyRequests') {
        submitError =
          result.rejection === 'tooManyRequests'
            ? t('submit.tooManyRequests')
            : t('submit.rejected');

        // **チケットが切れていただけのことがある。** 取り直して次の操作で通るようにする
        const reissued = await requestTicket(publicId);
        if (reissued) {
          responseToken = reissued.responseToken;
          ticket = reissued.ticket;

          // **課題も取り直す。** 使い終えた解答は 2 度通らない
          altcha = '';
          if (requiresProofOfWork && reissued.altcha) {
            altcha = (await solveAltcha(reissued.altcha)) ?? '';
          }
        }
        return;
      }

      if (result.rejection) {
        rejection = result.rejection;
        screen = 'rejected';
        return;
      }

      // **入力内容は消さない。** 失われたら再入力してもらう以外に手が無い
      submitError = t('submit.failed');
      if (result.attachmentErrors) {
        attachmentMessages = result.attachmentErrors.map((error) =>
          attachmentMessage(error.reason, error.fileName ?? undefined),
        );
      }
      if (result.errors) {
        const mapped: Record<string, string> = {};
        for (const [questionId, codes] of Object.entries(result.errors)) {
          mapped[questionId] = codes.map(serverValidationMessage).join(' / ');
        }
        errors = mapped;
      }
    } catch {
      submitError = t('submit.failed');
    } finally {
      submitting = false;
    }
  }

  /** 別の回答として新しく登録する。**この端末の記録は捨てる。** */
  function answerAgain() {
    forgetSubmission(publicId);
    location.reload();
  }

  const REJECTION_MESSAGES: Record<RejectionReason, MessageKey> = {
    notStarted: 'rejected.notStarted',
    closed: 'rejected.closed',
    suspended: 'rejected.suspended',
    notFound: 'rejected.notFound',
    rejected: 'rejected.rejected',
    tooManyRequests: 'rejected.tooManyRequests',
  };
</script>

<main>
  <!-- **言語の切り替えは URL を書き換えるだけ。**
       どこにも保存しないので、回答者を追う材料にならない
       （`_documents/多言語対応方針.md` 3 章） -->
  <div class="language">
    <label for="language">{t('form.languageLabel')}</label>
    <select
      id="language"
      value={language}
      onchange={(event) => changeLanguage(event.currentTarget.value as Language)}
    >
      {#each SUPPORTED_LANGUAGES as option (option)}
        <option value={option}>{LANGUAGE_NAMES[option]}</option>
      {/each}
    </select>
  </div>

  {#if screen === 'loading'}
    <p class="status">{t('status.loading')}</p>
  {:else if screen === 'rejected'}
    <!-- **理由を明示する。「エラー」で済ませない** -->
    <h1>{rejection === 'notFound' ? t('rejected.notFound.title') : t('rejected.closed.title')}</h1>
    <p class="status">{t(REJECTION_MESSAGES[rejection ?? 'notFound'])}</p>
  {:else if screen === 'error'}
    <h1>{t('error.badUrl.title')}</h1>
    <p class="status">{t('error.badUrl')}</p>
  {:else if screen === 'answered' && definition}
    <!-- **同じ端末からの再訪**（`_documents/画面設計.md` 1 章）。
         編集するか、新しく回答するかを選ばせる -->
    <h1>{t('answered.title')}</h1>
    {#if definition.allowEditingAfterSubmit && canEdit}
      <p class="status">{t('answered.canEdit')}</p>
      <button type="button" onclick={() => (screen = 'answering')}>{t('completed.edit')}</button>
    {:else if definition.allowEditingAfterSubmit}
      <!-- **前の回答が読めない。** 送信済みで Pleasanter へ渡った後や、
           トークンだけ消えた後はこちらになる -->
      <p class="status">{t('answered.cannotRead')}</p>
    {:else}
      <p class="status">{t('answered.editingNotAllowed')}</p>
    {/if}
    <button type="button" class="secondary" onclick={answerAgain}>
      {t('answered.answerAgain')}
    </button>
    <p class="note">{t('answered.answerAgainNote')}</p>
  {:else if screen === 'completed' && definition}
    <!-- **管理者が入れた文言が先。** 無ければ本アプリの文言へ落とす -->
    <h1>{text(definition.confirmationMessage, language) || t('completed.title')}</h1>
    <p class="status">{t('completed.thanks')}</p>
    {#if definition.allowEditingAfterSubmit}
      <button type="button" onclick={() => (screen = 'answering')}>{t('completed.edit')}</button>
    {/if}
    <button type="button" class="secondary" onclick={answerAgain}>
      {t('completed.answerAgain')}
    </button>
  {:else if definition && currentPage}
    <header>
      <!-- **飾り。** 意味は題名が伝えるので `alt` は空にする
           （読み上げに「ヘッダ画像」と挟まる方が邪魔になる） -->
      {#if headerImage}
        <img class="header-image" src={headerImage} alt="" />
      {/if}
      <h1>{text(definition.title, language)}</h1>
      {#if definition.description}
        <p class="lead">{text(definition.description, language)}</p>
      {/if}

      {#if definition.showProgress && steps.length > 1}
        <div
          class="progress"
          role="progressbar"
          aria-valuenow={progress}
          aria-valuemin="0"
          aria-valuemax="100"
          aria-label={t('form.progressLabel')}
        >
          <div class="bar" style={`width:${progress}%`}></div>
        </div>
        <p class="progress-text">
          {t('form.pageCount', { current: Math.min(stepIndex, steps.length - 1) + 1, total: steps.length })}
        </p>
      {/if}
    </header>

    <!--
      **勝手に戻さない**（Issue #59）。共有の端末では、
      前に使った人の回答をそのまま見せることになる
    -->
    {#if draftFound}
      <div class="draft" role="status">
        <p>{t('draft.found')}</p>
        <p class="draft-note">{t('draft.foundNote')}</p>
        <div class="draft-actions">
          <button type="button" onclick={restoreDraft}>{t('draft.restore')}</button>
          <button type="button" class="secondary" onclick={discardDraft}>
            {t('draft.discard')}
          </button>
        </div>
      </div>
    {:else if draftNotice !== ''}
      <p class="draft-notice" role="status">{draftNotice}</p>
    {/if}

    {#if currentPage.title}<h2>{text(currentPage.title, language)}</h2>{/if}
    {#if currentPage.description}
      <p class="lead">{text(currentPage.description, language)}</p>
    {/if}

    <form onsubmit={(event) => event.preventDefault()}>
      <!-- **出している設問だけを描く。** 条件で隠れているものは経路に含まれない -->
      {#each currentStep?.questions ?? [] as question (question.questionId)}
        <QuestionField
          {question}
          {language}
          bind:answer={answers[question.questionId]}
          error={errors[question.questionId]}
        />
      {/each}

      <!-- **ハニーポット。** 画面にも読み上げにも出さず、キーボードでも辿れない場所に置く。
           人には触れないので、埋まっていたら bot（`Services/SubmissionGuard.cs`）。
           **自動入力に拾われない名前にすること。** 拾われると正規の回答者を弾く -->
      <div class="trap" aria-hidden="true">
        <label for="q-extra">{t('form.trapLabel')}</label>
        <input
          id="q-extra"
          name="q-extra"
          type="text"
          tabindex="-1"
          autocomplete="off"
          bind:value={trap}
        />
      </div>

      {#if attachmentMessages.length > 0}
        <ul class="error" role="alert">
          {#each attachmentMessages as message (message)}
            <li>{message}</li>
          {/each}
        </ul>
      {:else if submitError}
        <p class="error" role="alert">{submitError}</p>
      {/if}

      <nav class="actions">
        {#if stepIndex > 0}
          <button type="button" class="secondary" onclick={goBack}>{t('form.back')}</button>
        {/if}
        {#if isLastStep}
          <button type="button" onclick={submit} disabled={submitting}>
            {submitting ? t('form.submitting') : t('form.submit')}
          </button>
        {:else}
          <button type="button" onclick={goNext}>{t('form.next')}</button>
        {/if}
      </nav>
    </form>
  {/if}
</main>

<style lang="scss">
  /* **テーマで差し替わるのはここに書いた既定値**（Issue #56）。
     何も指定されなければ、この値のまま＝今までの見た目 */
  :global(:root) {
    --border: #d0d5dd;
    --muted: #667085;
    --error: #b42318;
    --accent: #175cd3;
    --bg: #f9fafb;
    --text: #101828;
    /* **釦の文字色と入力欄の地**（Issue #109）。
       今までは #fff を直に書いていた。既定値は変えていないので見た目は同じ */
    --accent-text: #fff;
    --surface: #fff;
    --font: system-ui, sans-serif;
  }

  :global(body) {
    margin: 0;
    background: var(--bg);
    font-family: var(--font);
    color: var(--text);
    line-height: 1.6;
  }

  main {
    max-width: 46rem;
    margin: 0 auto;
    padding: 2rem 1rem 4rem;
  }

  h1 {
    font-size: 1.5rem;
    margin: 0 0 0.5rem;
  }

  h2 {
    font-size: 1.15rem;
    margin: 1.5rem 0 0.5rem;
  }

  .lead {
    color: var(--muted);
    margin: 0 0 1rem;
  }

  /* ---- 下書き（Issue #59）------------------------------------------------- */

  .draft {
    border: 1px solid var(--border);
    /* **色だけに頼らない。** 左端の太い線でも「いつもと違う」が分かる */
    border-left: 4px solid var(--accent);
    border-radius: 8px;
    padding: 0.75rem 1rem;
    margin: 0 0 1rem;
  }

  .draft p {
    margin: 0 0 0.25rem;
  }

  .draft-note {
    color: var(--muted);
    font-size: 0.85rem;
  }

  .draft-actions {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    margin-top: 0.5rem;
  }

  .draft-notice {
    color: var(--muted);
    font-size: 0.9rem;
    margin: 0 0 1rem;
  }

  .status {
    color: var(--muted);
  }

  .progress {
    height: 6px;
    background: var(--border);
    border-radius: 3px;
    overflow: hidden;
    margin: 1rem 0 0.25rem;
  }

  .bar {
    height: 100%;
    background: var(--accent);
    transition: width 0.2s ease;
  }

  .progress-text {
    color: var(--muted);
    font-size: 0.85rem;
    margin: 0 0 1.5rem;
  }

  .actions {
    display: flex;
    gap: 0.75rem;
    justify-content: flex-end;
    margin-top: 1.5rem;
  }

  /* **横幅に収める。** 縦横比は保つ（縦長の画像で画面が埋まらないよう上限を置く） */
  .header-image {
    display: block;
    width: 100%;
    max-height: 14rem;
    object-fit: cover;
    border-radius: 8px;
    margin-bottom: 1rem;
  }

  .language {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 0.5rem;
    margin-bottom: 1rem;
    font-size: 0.85rem;
    color: var(--muted);

    select {
      font: inherit;
      padding: 0.25rem 0.4rem;
      border: 1px solid var(--border);
      border-radius: 4px;
      /* **地と文字を対で追随させる**（Issue #109）。
         片方だけテーマに従わせると、白地に白い文字のような組み合わせが作れてしまう */
      background: var(--surface);
      color: var(--text);
    }
  }

  button {
    font: inherit;
    padding: 0.6rem 1.25rem;
    border-radius: 6px;
    border: 1px solid var(--accent);
    background: var(--accent);
    color: var(--accent-text);
    cursor: pointer;

    &:disabled {
      opacity: 0.6;
      cursor: progress;
    }

    &.secondary {
      background: var(--surface);
      color: var(--accent);
    }
  }

  .error {
    color: var(--error);
  }

  .note {
    color: var(--muted);
    font-size: 0.85rem;
    margin-top: 0.75rem;
  }

  /* **ハニーポットを画面から外す。** `display: none` にしないのは、
     それだけを見て無視する bot が居るため */
  .trap {
    position: absolute;
    left: -9999px;
    width: 1px;
    height: 1px;
    overflow: hidden;
  }
</style>
