import type { Translate } from './i18n/messages';
import type { AnswerState, Question } from './types';
import { allowsMultiplePerRow, hasChoices, hasRows, isDisplayOnly, rowValues } from './types';

/**
 * 画面側の検証。
 *
 * **これは体験のためだけのもの。** 受け付けるかどうかはサーバ側が決める
 * （`_documents/アプリケーション設計.md` 6 章）。
 * 判定規則はサーバ側と同じ定義から導いているので、通常は一致する。
 *
 * **文言は呼ぶ側から渡してもらう。** ここで組み立てると言語を持ち込むことになる
 * （`_documents/多言語対応方針.md` 4 章）。
 */
export function validateQuestion(
  question: Question,
  answer: AnswerState | undefined,
  t: Translate,
): string | null {
  if (isDisplayOnly(question)) return null;

  // **添付の設問は値ではなくファイルの有無で見る。** サーバ側と同じ見方にする
  if (question.type === 'File') {
    const files = answer?.files ?? [];
    if (files.length === 0) {
      return question.isRequired ? t('validation.fileRequired') : null;
    }
    if (question.settings.maxFileCount !== undefined && files.length > question.settings.maxFileCount) {
      return t('validation.fileCount', { count: question.settings.maxFileCount });
    }
    const tooLarge = files.find(
      (file) =>
        question.settings.maxFileSizeBytes !== undefined &&
        file.size > question.settings.maxFileSizeBytes,
    );
    return tooLarge ? t('validation.fileTooLarge', { name: tooLarge.name }) : null;
  }

  // **グリッドは行ごとに見る**（Issue #74）。値の配列ではなく行の辞書に入る
  if (hasRows(question)) {
    return validateGrid(question, answer, t);
  }

  const values = (answer?.values ?? []).filter((value) => value.trim() !== '');

  if (values.length === 0) {
    return question.isRequired ? t('validation.required') : null;
  }

  // **ランキングは並べた順そのものが答え。** 同じ項目が 2 回出ると順位が決まらない
  if (question.type === 'Ranking' && new Set(values).size !== values.length) {
    return t('validation.duplicateRank');
  }

  // **選択肢を持たない形式は 1 つしか持てない。**
  // ランキングは選択肢を持つ側（並べた順を全部返す）なので、ここでは弾かない
  if (!hasChoices(question) && values.length > 1) {
    return t('validation.singleValueOnly');
  }

  for (const value of values) {
    const error = validateValue(question, value, t);
    if (error) return error;
  }

  return null;
}

/**
 * グリッドを見る（Issue #74）。
 *
 * **必須は「行が全部埋まっていること」。** 1 行でも空なら足りない。
 * 行の一部だけ答えて送れると、どこまで答えたのか誰にも分からなくなる。
 * **サーバ側（`AnswerValidator`）と同じ見方。**
 */
function validateGrid(
  question: Question,
  answer: AnswerState | undefined,
  t: Translate,
): string | null {
  for (const row of question.settings.rows ?? []) {
    const values = rowValues(answer, row.rowId).filter((value) => value.trim() !== '');

    if (values.length === 0) {
      if (question.isRequired) return t('validation.rowRequired');
      continue;
    }

    if (!allowsMultiplePerRow(question) && values.length > 1) {
      return t('validation.singleValueOnly');
    }
  }

  return null;
}

function validateValue(question: Question, value: string, t: Translate): string | null {
  const settings = question.settings;

  switch (question.type) {
    case 'Text':
    case 'Paragraph': {
      if (settings.maxLength !== undefined && value.length > settings.maxLength) {
        return t('validation.tooLong', { max: settings.maxLength });
      }
      if (settings.format === 'Email' && !isEmail(value)) {
        return t('validation.email');
      }
      if (settings.format === 'Url' && !isHttpUrl(value)) {
        return t('validation.url');
      }
      return null;
    }

    case 'Scale':
    case 'Rating': {
      const number = Number(value);
      if (!Number.isFinite(number)) return t('validation.notANumber');
      if (settings.scaleMinimum !== undefined && number < settings.scaleMinimum) {
        return t('validation.minimum', { minimum: settings.scaleMinimum });
      }
      if (settings.scaleMaximum !== undefined && number > settings.scaleMaximum) {
        return t('validation.maximum', { maximum: settings.scaleMaximum });
      }
      return null;
    }

    case 'Date':
      return Number.isNaN(Date.parse(value)) ? t('validation.date') : null;

    default:
      return null;
  }
}

/**
 * メールアドレスらしいか。
 *
 * **正規表現の作り込みはしない。** 厳密な判定はサーバ側が行う。
 * ここは打ち間違いに気づいてもらうためのもの。
 */
function isEmail(value: string): boolean {
  const at = value.indexOf('@');
  return at > 0 && at < value.length - 1 && !value.includes(' ');
}

function isHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}

/** ページ内の設問をまとめて見る。 */
export function validatePage(
  questions: Question[],
  answers: Record<string, AnswerState>,
  t: Translate,
): Record<string, string> {
  const errors: Record<string, string> = {};
  for (const question of questions) {
    const error = validateQuestion(question, answers[question.questionId], t);
    if (error) errors[question.questionId] = error;
  }
  return errors;
}
