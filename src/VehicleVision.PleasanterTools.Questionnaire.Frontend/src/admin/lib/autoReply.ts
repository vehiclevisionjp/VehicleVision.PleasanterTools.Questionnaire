import { ja, type MessageKey } from './i18n/messages';
import type { SurveyDefinition } from './types';

/**
 * 自動返信の設定の不備 1 件（Issue #189）。
 *
 * **サーバの `AutoReplyProblem` と同じ形**（`Core/Validation/AutoReplyValidator.cs`）。
 * 公開の口が `autoReply` として返すものと、編集中にこの画面が出すものの両方に使う。
 */
export interface AutoReplyProblem {
  code: string;
  detail?: string | null;
}

/**
 * 不備の文言の鍵。
 *
 * **サーバ側に符号が増えたときに落とさない。**
 * 鍵が無ければ符号そのものを出す（`null` を返す）。
 */
export function autoReplyKey(code: string): MessageKey | null {
  const key = `autoReply.problem.${code}`;
  return key in ja ? (key as MessageKey) : null;
}

/**
 * 自動返信の設定を見る。
 *
 * ⚠️ **サーバ側の `AutoReplyValidator` と同じ規則を持つ。**
 * 片方だけ緩めないこと。**最後に判定するのはサーバ**で、ここは
 * 「公開して初めて弾かれて作り直し」を避けるための先出しに過ぎない。
 */
export function validateAutoReply(definition: SurveyDefinition): AutoReplyProblem[] {
  const settings = definition.autoReply;
  if (!settings?.enabled) return [];

  const problems: AutoReplyProblem[] = [];
  const questions = definition.pages.flatMap((page) => page.questions);

  if (!settings.toQuestionId) {
    problems.push({ code: 'ToQuestionMissing' });
  } else {
    const question = questions.find((item) => item.questionId === settings.toQuestionId);
    if (!question) {
      problems.push({ code: 'ToQuestionNotFound', detail: settings.toQuestionId });
    } else if (question.type !== 'Text' || question.settings?.format !== 'Email') {
      problems.push({ code: 'ToQuestionNotEmail', detail: settings.toQuestionId });
    }
  }

  if (isBlank(settings.subject)) problems.push({ code: 'SubjectMissing' });
  if (isBlank(settings.body)) problems.push({ code: 'BodyMissing' });

  const usesEditLink =
    usesKeyword(settings, 'editUrl') || usesKeyword(settings, 'editUrlExpiresAt');
  if (usesEditLink) {
    // 既知だが現在の設定では使えないキーワードは公開時に止める。
    // 打ち間違いである未知のキーワードは、プレビューで警告しつつ本文へ残す。
    if (!definition.allowEditingAfterSubmit) {
      problems.push({ code: 'EditLinkKeywordUnavailable' });
    }

    // **永久に生きるリンクを作らせない**
    const days = settings.editLinkDays ?? 7;
    if (!Number.isInteger(days) || days < 1 || days > 365) {
      problems.push({ code: 'EditLinkDaysInvalid', detail: String(days) });
    }
  }

  const usesAssetsLink =
    usesKeyword(settings, 'assetsUrl') || usesKeyword(settings, 'assetsUrlExpiresAt');
  if (
    usesAssetsLink &&
    (!hasTicketedAssets(definition) || definition.assetDelivery?.expiration === 'CompletedOnly')
  ) {
    problems.push({ code: 'AssetsUrlKeywordUnavailable' });
  }

  return problems;
}

function usesKeyword(
  settings: NonNullable<SurveyDefinition['autoReply']>,
  keyword: string,
): boolean {
  const pattern = new RegExp(`\\{\\{\\s*${keyword}\\s*\\}\\}`);
  return [settings.subject, settings.body].some(
    (localized) => localized && Object.values(localized).some((value) => pattern.test(value)),
  );
}

function hasTicketedAssets(definition: SurveyDefinition): boolean {
  const ids = (text: string | undefined): string[] =>
    [...(text ?? '').matchAll(/asset:([0-9a-f]{8}-[0-9a-f-]{27})/gi)].map(
      (match) => match[1]!,
    );
  const questionIds = new Set(
    definition.pages.flatMap((page) =>
      page.questions.flatMap((question) =>
        Object.values(question.description ?? {}).flatMap((text) => ids(text)),
      ),
    ),
  );
  return Object.values(definition.confirmationMessage ?? {})
    .flatMap((text) => ids(text))
    .some((id) => !questionIds.has(id));
}

/**
 * どの言語にも中身が無いか。
 *
 * **英語だけ書いてあるのは翻訳漏れであって不備ではない**（既定の言語へ落ちる）。
 */
function isBlank(value: Record<string, string> | undefined): boolean {
  if (!value) return true;
  return Object.values(value).every((text) => text.trim() === '');
}
