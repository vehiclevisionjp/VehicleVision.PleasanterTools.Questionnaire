import type { FormResponse, PayloadAnswer, RejectionReason } from './types';

/** 回答トークンの保存先。**URL には載せない。** */
const TOKEN_STORAGE_PREFIX = 'questionnaire.token.';

/**
 * 回答トークンを取り出す。無ければ作る。
 *
 * **これを知っている人はその回答を書き換えられる**ので、URL・メール・ログへ載せない
 * （`_documents/アーキテクチャ方針.md` 9 章）。
 */
export function getOrCreateResponseToken(publicId: string): string {
  const key = TOKEN_STORAGE_PREFIX + publicId;
  const existing = localStorage.getItem(key);
  if (existing) return existing;

  // **暗号論的乱数から作る。** Math.random は使わない
  const bytes = new Uint8Array(24);
  crypto.getRandomValues(bytes);
  const token = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  localStorage.setItem(key, token);
  return token;
}

/** 既に回答済みか（この端末で）。 */
export function hasSubmitted(publicId: string): boolean {
  return localStorage.getItem(TOKEN_STORAGE_PREFIX + publicId) !== null;
}

export interface LoadResult {
  form?: FormResponse;
  rejection?: RejectionReason;
}

/** 公開中のアンケートを取りに行く。 */
export async function loadForm(publicId: string): Promise<LoadResult> {
  const response = await fetch(`/api/forms/${encodeURIComponent(publicId)}`, {
    headers: { accept: 'application/json' },
  });

  if (response.ok) {
    return { form: (await response.json()) as FormResponse };
  }

  if (response.status === 403) {
    const body = (await response.json().catch(() => ({}))) as { reason?: RejectionReason };
    return { rejection: body.reason ?? 'notFound' };
  }

  return { rejection: 'notFound' };
}

export interface SubmitResult {
  accepted: boolean;
  /** 設問 ID → エラーの種別。サーバ側の検証結果。 */
  errors?: Record<string, string[]>;
  rejection?: RejectionReason;
}

/** 回答を送る。**受け付けられたら 202 が返る**（Pleasanter へはこの後ワーカーが送る）。 */
export async function submitAnswers(
  publicId: string,
  responseToken: string,
  answers: PayloadAnswer[],
): Promise<SubmitResult> {
  const response = await fetch(
    `/api/forms/${encodeURIComponent(publicId)}/responses/${encodeURIComponent(responseToken)}`,
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ answers }),
    },
  );

  if (response.status === 202) {
    return { accepted: true };
  }

  if (response.status === 400 || response.status === 422) {
    const body = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> };
    return { accepted: false, errors: body.errors ?? {} };
  }

  if (response.status === 403) {
    const body = (await response.json().catch(() => ({}))) as { reason?: RejectionReason };
    return { accepted: false, rejection: body.reason ?? 'notFound' };
  }

  if (response.status === 429) {
    // **レート制限。** 少し待てば通る
    return { accepted: false, rejection: undefined, errors: { '': ['TooManyRequests'] } };
  }

  return { accepted: false, rejection: 'notFound' };
}

/** 送信待ちにある自分の回答を読む。**Pleasanter より先に見る。** */
export async function loadPendingAnswers(
  publicId: string,
  responseToken: string,
): Promise<PayloadAnswer[] | null> {
  const response = await fetch(
    `/api/forms/${encodeURIComponent(publicId)}/responses/${encodeURIComponent(responseToken)}`,
    { headers: { accept: 'application/json' } },
  );

  if (!response.ok) return null;
  const body = (await response.json()) as { answers?: PayloadAnswer[] };
  return body.answers ?? null;
}
