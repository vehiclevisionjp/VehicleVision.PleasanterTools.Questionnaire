import type { Translate } from './i18n/messages';
import type { AnswerState, Question } from './types';
import { hasChoices, isDisplayOnly } from './types';

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

  const values = (answer?.values ?? []).filter((value) => value.trim() !== '');

  if (values.length === 0) {
    return question.isRequired ? t('validation.required') : null;
  }

  if (!hasChoices(question) && question.type !== 'Checkbox' && values.length > 1) {
    return t('validation.singleValueOnly');
  }

  for (const value of values) {
    const error = validateValue(question, value, t);
    if (error) return error;
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
