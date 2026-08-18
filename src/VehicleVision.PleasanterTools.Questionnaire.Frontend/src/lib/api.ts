import type { FormResponse, PayloadAnswer, RejectionReason, Ticket } from './types';

/** 回答トークンの保存先。**URL には載せない。** */
const TOKEN_STORAGE_PREFIX = 'questionnaire.token.';

/** 「この端末で回答済み」の印の保存先。 */
const SUBMITTED_STORAGE_PREFIX = 'questionnaire.submitted.';

/** 同じ印を Cookie にも置くときの名前。 */
const SUBMITTED_COOKIE_PREFIX = 'q.a.';

/** 印を残す期間（秒）。180 日。 */
const SUBMITTED_MAX_AGE = 180 * 24 * 60 * 60;

/**
 * Web Storage を触る。**使えない状況でも画面を落とさない。**
 *
 * プライベートブラウジングや iframe の制限で、参照した瞬間に例外が飛ぶことがある。
 * **回答できなくなる方が、多重投稿を許すより重い。**
 */
function readStorage(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStorage(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // 保存できなくても回答は続けられる
  }
}

function removeStorage(key: string): void {
  try {
    window.localStorage.removeItem(key);
  } catch {
    // 同上
  }
}

/**
 * 回答済みの印を置く Cookie の属性。
 *
 * **`path` を `/f/{publicId}` に絞る。** こうすると API の要求には付かず、
 * 回答画面の JavaScript から読むためだけの Cookie になる。
 * **入るのは真偽値だけで、回答者を識別する値は入れない。**
 */
function cookieAttributes(publicId: string): string {
  const secure = location.protocol === 'https:' ? '; secure' : '';
  return `; path=/f/${encodeURIComponent(publicId)}; max-age=${SUBMITTED_MAX_AGE}; samesite=lax${secure}`;
}

function readCookie(name: string): string | null {
  try {
    const found = document.cookie
      .split(';')
      .map((entry) => entry.trim())
      .find((entry) => entry.startsWith(`${name}=`));
    return found ? found.slice(name.length + 1) : null;
  } catch {
    return null;
  }
}

/** この端末に残っている回答トークン。無ければ `null`。 */
export function readResponseToken(publicId: string): string | null {
  return readStorage(TOKEN_STORAGE_PREFIX + publicId);
}

/**
 * 送信チケットを受け取る。**回答トークンもサーバが決める。**
 *
 * 端末が既にトークンを持っていれば、それを渡して同じ回答を指し続ける
 * （渡さないと、編集のたびに別の回答になる）。
 * **トークンは本文で送る。** URL に載せると経路のログへ残る。
 */
export async function requestTicket(publicId: string): Promise<Ticket | null> {
  const response = await fetch(`/api/forms/${encodeURIComponent(publicId)}/ticket`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ responseToken: readResponseToken(publicId) }),
  });

  if (!response.ok) return null;

  const ticket = (await response.json()) as Ticket;
  writeStorage(TOKEN_STORAGE_PREFIX + publicId, ticket.responseToken);
  return ticket;
}

/**
 * この端末から回答済みか。
 *
 * **「防止」ではなく「抑止」。** Web Storage も Cookie も回答者が消せるし、
 * 端末を変えれば残らない。**2 か所に置くのは、片方だけ消えても効くようにするため**
 * （Safari は JavaScript が置いた Cookie を 7 日で切ることがある）。
 */
export function hasSubmitted(publicId: string): boolean {
  return (
    readStorage(SUBMITTED_STORAGE_PREFIX + publicId) !== null ||
    readCookie(SUBMITTED_COOKIE_PREFIX + publicId) !== null
  );
}

/** 回答済みの印を残す。 */
export function markSubmitted(publicId: string): void {
  writeStorage(SUBMITTED_STORAGE_PREFIX + publicId, '1');
  try {
    document.cookie = `${SUBMITTED_COOKIE_PREFIX}${publicId}=1${cookieAttributes(publicId)}`;
  } catch {
    // Cookie が使えなくても Web Storage 側が残る
  }
}

/** この端末の回答を忘れる。**「新しい回答を送信する」を選んだとき。** */
export function forgetSubmission(publicId: string): void {
  removeStorage(TOKEN_STORAGE_PREFIX + publicId);
  removeStorage(SUBMITTED_STORAGE_PREFIX + publicId);
  try {
    document.cookie = `${SUBMITTED_COOKIE_PREFIX}${publicId}=; path=/f/${encodeURIComponent(
      publicId,
    )}; max-age=0`;
  } catch {
    // 同上
  }
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

/** 送信するときに一緒に渡すもの。 */
export interface SubmitContext {
  /** 画面を開いたときにサーバが発行した送信チケット。 */
  ticket: string;
  /** ハニーポット項目の値。**人が触れば埋まらない。** */
  trap: string;
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
/**
 * 回答を送る。**受け付けられたら 202 が返る**（Pleasanter へはこの後ワーカーが送る）。
 *
 * **添付があるときは `multipart/form-data` で送る。**
 * 添付だけ先に預ける口は無く、サーバは送信待ちへ保存する前に中身を検査する
 * （`_documents/添付ファイル検査-運用手順書.md`）。
 *
 * **チケットと罠はどちらの送り方でも同じ場所に載せる。**
 * multipart のときは `answers` の欄へまとめて入れるので、サーバ側の読み方は 1 つで済む。
 */
export async function submitAnswers(
  publicId: string,
  responseToken: string,
  answers: PayloadAnswer[],
  context: SubmitContext,
  attachments: Attachment[] = [],
): Promise<SubmitResult> {
  const url = `/api/forms/${encodeURIComponent(publicId)}/responses/${encodeURIComponent(responseToken)}`;

  let request: RequestInit;
  if (attachments.length === 0) {
    request = {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ answers, ticket: context.ticket, trap: context.trap }),
    };
  } else {
    const form = new FormData();
    form.append(
      'answers',
      JSON.stringify({ answers, ticket: context.ticket, trap: context.trap }),
    );
    for (const attachment of attachments) {
      form.append(attachment.questionId, attachment.file, attachment.file.name);
    }
    // **content-type を自分で付けない。** 境界文字列はブラウザが決める
    request = { method: 'PUT', body: form };
  }

  const response = await fetch(url, request);

  if (response.status === 202) {
    // **受け付けられて初めて印を置く。** 開いただけで回答済みにしない
    markSubmitted(publicId);
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
    return { accepted: false, rejection: 'tooManyRequests' };
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
