import type { AnswerState, Question } from './types';
import { hasChoices, isDisplayOnly } from './types';

/**
 * 画面側の検証。
 *
 * **これは体験のためだけのもの。** 受け付けるかどうかはサーバ側が決める
 * （`_documents/アプリケーション設計.md` 6 章）。
 * 判定規則はサーバ側と同じ定義から導いているので、通常は一致する。
 */
export function validateQuestion(question: Question, answer: AnswerState | undefined): string | null {
  if (isDisplayOnly(question)) return null;

  const values = (answer?.values ?? []).filter((value) => value.trim() !== '');

  if (values.length === 0) {
    return question.isRequired ? '回答してください' : null;
  }

  if (!hasChoices(question) && question.type !== 'Checkbox' && values.length > 1) {
    return '回答は 1 つだけ選んでください';
  }

  for (const value of values) {
    const error = validateValue(question, value);
    if (error) return error;
  }

  return null;
}

function validateValue(question: Question, value: string): string | null {
  const settings = question.settings;

  switch (question.type) {
    case 'Text':
    case 'Paragraph': {
      if (settings.maxLength !== undefined && value.length > settings.maxLength) {
        return `${settings.maxLength} 文字以内で入力してください`;
      }
      if (settings.format === 'Email' && !isEmail(value)) {
        return 'メールアドレスの形式で入力してください';
      }
      if (settings.format === 'Url' && !isHttpUrl(value)) {
        return 'http:// または https:// で始まる URL を入力してください';
      }
      return null;
    }

    case 'Scale':
    case 'Rating': {
      const number = Number(value);
      if (!Number.isFinite(number)) return '数値で入力してください';
      if (settings.scaleMinimum !== undefined && number < settings.scaleMinimum) {
        return `${settings.scaleMinimum} 以上で入力してください`;
      }
      if (settings.scaleMaximum !== undefined && number > settings.scaleMaximum) {
        return `${settings.scaleMaximum} 以下で入力してください`;
      }
      return null;
    }

    case 'Date':
      return Number.isNaN(Date.parse(value)) ? '日付を選んでください' : null;

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
): Record<string, string> {
  const errors: Record<string, string> = {};
  for (const question of questions) {
    const error = validateQuestion(question, answers[question.questionId]);
    if (error) errors[question.questionId] = error;
  }
  return errors;
}
