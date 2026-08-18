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
  /** 添付を受け付けてもらえなかった理由。 */
  attachmentErrors?: AttachmentError[];
}

/** 添付 1 件を受け付けなかった理由。 */
export interface AttachmentError {
  questionId?: string;
  fileName?: string;
  reason: string;
}

/** 設問に紐づく添付。**欄の名前が設問 ID になる。** */
export interface Attachment {
  questionId: string;
  file: File;
}

/**
 * 回答を送る。**受け付けられたら 202 が返る**（Pleasanter へはこの後ワーカーが送る）。
 *
 * **添付があるときは `multipart/form-data` で送る。**
 * 添付だけ先に預ける口は無く、サーバは送信待ちへ保存する前に中身を検査する
 * （`_documents/添付ファイル検査-運用手順書.md`）。
 */
export async function submitAnswers(
  publicId: string,
  responseToken: string,
  answers: PayloadAnswer[],
  attachments: Attachment[] = [],
): Promise<SubmitResult> {
  const url = `/api/forms/${encodeURIComponent(publicId)}/responses/${encodeURIComponent(responseToken)}`;

  let request: RequestInit;
  if (attachments.length === 0) {
    request = {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ answers }),
    };
  } else {
    const form = new FormData();
    form.append('answers', JSON.stringify({ answers }));
    for (const attachment of attachments) {
      form.append(attachment.questionId, attachment.file, attachment.file.name);
    }
    // **content-type を自分で付けない。** 境界文字列はブラウザが決める
    request = { method: 'PUT', body: form };
  }

  const response = await fetch(url, request);

  if (response.status === 202) {
    return { accepted: true };
  }

  if (response.status === 413) {
    return { accepted: false, errors: { '': ['TooLarge'] } };
  }

  if (response.status === 503) {
    // **ウイルススキャナへ到達できない。** 添付を受け付けられない状態が続いている
    return { accepted: false, errors: { '': ['ScannerUnavailable'] } };
  }

  if (response.status === 400 || response.status === 422) {
    const body = (await response.json().catch(() => ({}))) as {
      errors?: Record<string, string[]>;
      attachments?: AttachmentError[];
    };
    return body.attachments
      ? { accepted: false, attachmentErrors: body.attachments }
      : { accepted: false, errors: body.errors ?? {} };
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
