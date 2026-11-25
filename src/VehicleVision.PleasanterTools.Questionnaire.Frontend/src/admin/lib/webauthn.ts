/**
 * WebAuthn（パスキー）の呼び出しと、サーバとの値の変換（Issue #1068。親 Core#213）。
 *
 * **ここは画面の都合だけを持つ。** 資格情報の検証・保管・どの方式を使えるかの判断はサーバ側の責務で、
 * 画面が隠した・出した方式を権限判定の代わりにしない。
 *
 * 仕様は Core の「認証とセッションの仕様書」3.10（登録 3.10.3・認証 3.10.4・API 3.10.6）。
 * **サーバとやり取りする値の形（base64url・フィールド名）は Fido2NetLib の JSON に合わせた想定**で、
 * Core#216 の実装と食い違ったら、ここの変換（`creationOptionsFromJson` など）と
 * `api.ts` の `passkeyApi` だけを直す。画面（`.svelte`）はこの層の戻り値だけを見る。
 *
 * **ブラウザの機能はすべて引数で受ける**（`credentials`・`environment`）。単体試験でモックするため。
 */
import type { Result } from './api';

// ---- base64url ---------------------------------------------------------------

/** base64url の文字列を、バイト列へ戻す。**不正な文字なら例外**（呼び出し側が受ける）。 */
export function base64UrlToBytes(value: string): Uint8Array<ArrayBuffer> {
  if (!/^[A-Za-z0-9_-]*$/.test(value)) {
    throw new Error('base64url ではない値です。');
  }

  const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

/** バイト列を、パディングなしの base64url へ直す。 */
export function bytesToBase64Url(buffer: ArrayBuffer | ArrayBufferView): string {
  const bytes =
    buffer instanceof ArrayBuffer
      ? new Uint8Array(buffer)
      : new Uint8Array(buffer.buffer, buffer.byteOffset, buffer.byteLength);

  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// ---- サーバとやり取りする JSON の形 -------------------------------------------

/** 資格情報の指定（`excludeCredentials`・`allowCredentials`）。`id` は base64url。 */
export interface CredentialDescriptorJson {
  id: string;
  type?: string;
  transports?: string[];
}

/** 登録の開始でサーバが返すオプション。バイト列は base64url の文字列。 */
export interface CreationOptionsJson {
  rp: { id?: string; name: string };
  user: { id: string; name: string; displayName: string };
  challenge: string;
  pubKeyCredParams: { type: string; alg: number }[];
  timeout?: number;
  excludeCredentials?: CredentialDescriptorJson[];
  authenticatorSelection?: AuthenticatorSelectionCriteria;
  attestation?: AttestationConveyancePreference;
}

/** ログインの開始でサーバが返すオプション。**`allowCredentials` は空**（ディスコバラブル）。 */
export interface RequestOptionsJson {
  challenge: string;
  timeout?: number;
  rpId?: string;
  allowCredentials?: CredentialDescriptorJson[];
  userVerification?: UserVerificationRequirement;
}

/** 登録の完了でサーバへ返す応答。バイト列は base64url の文字列。 */
export interface AttestationJson {
  id: string;
  rawId: string;
  type: 'public-key';
  response: { attestationObject: string; clientDataJSON: string; transports?: string[] };
}

/** ログインの完了でサーバへ返す応答。バイト列は base64url の文字列。 */
export interface AssertionJson {
  id: string;
  rawId: string;
  type: 'public-key';
  response: {
    authenticatorData: string;
    clientDataJSON: string;
    signature: string;
    userHandle: string | null;
  };
}

function toDescriptors(
  descriptors: CredentialDescriptorJson[] | undefined,
): PublicKeyCredentialDescriptor[] {
  return (descriptors ?? []).map((descriptor) => ({
    type: 'public-key',
    id: base64UrlToBytes(descriptor.id),
    ...(descriptor.transports === undefined
      ? {}
      : { transports: descriptor.transports as AuthenticatorTransport[] }),
  }));
}

/**
 * 登録のオプションを、`navigator.credentials.create` へ渡せる形へ直す。
 * **不正な値なら例外**（呼び出し側が `invalid-options` として扱う）。
 */
export function creationOptionsFromJson(json: CreationOptionsJson): PublicKeyCredentialCreationOptions {
  return {
    rp: json.rp,
    user: {
      id: base64UrlToBytes(json.user.id),
      name: json.user.name,
      displayName: json.user.displayName,
    },
    challenge: base64UrlToBytes(json.challenge),
    pubKeyCredParams: json.pubKeyCredParams as PublicKeyCredentialParameters[],
    ...(json.timeout === undefined ? {} : { timeout: json.timeout }),
    excludeCredentials: toDescriptors(json.excludeCredentials),
    ...(json.authenticatorSelection === undefined
      ? {}
      : { authenticatorSelection: json.authenticatorSelection }),
    ...(json.attestation === undefined ? {} : { attestation: json.attestation }),
  };
}

/** ログインのオプションを、`navigator.credentials.get` へ渡せる形へ直す。 */
export function requestOptionsFromJson(json: RequestOptionsJson): PublicKeyCredentialRequestOptions {
  return {
    challenge: base64UrlToBytes(json.challenge),
    ...(json.timeout === undefined ? {} : { timeout: json.timeout }),
    ...(json.rpId === undefined ? {} : { rpId: json.rpId }),
    allowCredentials: toDescriptors(json.allowCredentials),
    ...(json.userVerification === undefined ? {} : { userVerification: json.userVerification }),
  };
}

/**
 * サーバの応答を、オプション本体へ開く。
 *
 * **オプションがそのまま返る形と、`options` / `publicKey` で包まれた形のどちらも受ける**
 * （仕様書 3.10.6 は「オプション」としか書いていない）。
 */
export function unwrapOptions<T extends object>(value: unknown): T | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }

  const record = value as Record<string, unknown>;
  for (const key of ['options', 'publicKey']) {
    const inner = record[key];
    if (typeof inner === 'object' && inner !== null) {
      return inner as T;
    }
  }
  return value as T;
}

interface PublicKeyCredentialLike {
  id: string;
  rawId: ArrayBuffer;
  type: string;
  response: unknown;
}

function isPublicKeyCredential(credential: unknown): credential is PublicKeyCredentialLike {
  return (
    typeof credential === 'object' &&
    credential !== null &&
    (credential as PublicKeyCredentialLike).type === 'public-key' &&
    'rawId' in credential &&
    'response' in credential
  );
}

/** 登録で得た資格情報を、サーバへ返す JSON にする。**取れない形なら `null`。** */
export function attestationToJson(credential: unknown): AttestationJson | null {
  if (!isPublicKeyCredential(credential)) {
    return null;
  }

  const response = credential.response as AuthenticatorAttestationResponse;
  if (!response?.attestationObject || !response.clientDataJSON) {
    return null;
  }

  const transports =
    typeof response.getTransports === 'function' ? response.getTransports() : undefined;

  return {
    id: credential.id,
    rawId: bytesToBase64Url(credential.rawId),
    type: 'public-key',
    response: {
      attestationObject: bytesToBase64Url(response.attestationObject),
      clientDataJSON: bytesToBase64Url(response.clientDataJSON),
      ...(transports === undefined || transports.length === 0 ? {} : { transports }),
    },
  };
}

/** ログインで得た資格情報を、サーバへ返す JSON にする。**取れない形なら `null`。** */
export function assertionToJson(credential: unknown): AssertionJson | null {
  if (!isPublicKeyCredential(credential)) {
    return null;
  }

  const response = credential.response as AuthenticatorAssertionResponse;
  if (!response?.authenticatorData || !response.clientDataJSON || !response.signature) {
    return null;
  }

  return {
    id: credential.id,
    rawId: bytesToBase64Url(credential.rawId),
    type: 'public-key',
    response: {
      authenticatorData: bytesToBase64Url(response.authenticatorData),
      clientDataJSON: bytesToBase64Url(response.clientDataJSON),
      signature: bytesToBase64Url(response.signature),
      userHandle: response.userHandle ? bytesToBase64Url(response.userHandle) : null,
    },
  };
}

// ---- 対応状況 ----------------------------------------------------------------

/** 対応状況を調べるのに要る、ブラウザの値。 */
export interface PasskeyEnvironment {
  PublicKeyCredential?: unknown;
  isSecureContext?: boolean;
  navigator?: { credentials?: unknown };
}

/**
 * このブラウザでパスキーを使えるか。
 *
 * - `insecure`: HTTPS（または localhost）ではない。WebAuthn は安全な文脈でしか動かない
 * - `unsupported`: WebAuthn に対応していない
 */
export function passkeySupport(environment: PasskeyEnvironment): 'ok' | 'insecure' | 'unsupported' {
  if (environment.isSecureContext === false) {
    return 'insecure';
  }
  if (
    typeof environment.PublicKeyCredential === 'undefined' ||
    !environment.navigator?.credentials
  ) {
    return 'unsupported';
  }
  return 'ok';
}

// ---- 失敗の分類 --------------------------------------------------------------

/**
 * 失敗の種類。
 *
 * - `cancelled`: 利用者が取り消した・時間切れ。**ログインでは「使えるパスキーが無い」場合も、
 *   ブラウザはこれと区別しない**（ユーザーの列挙を防ぐための仕様）
 * - `noAuthenticator`: 本人確認つきの保存に対応する認証器が無い
 * - `alreadyRegistered`: 同じ認証器が登録済み（登録のみ）
 * - `insecure`: RP ID・Origin が合わない／安全な接続ではない
 * - `invalidOptions`: サーバのオプションを読めない
 * - `server`: サーバが断った（`status`・`message` を持つ）
 */
export type PasskeyFailure =
  | { kind: 'unsupported' }
  | { kind: 'insecureContext' }
  | { kind: 'cancelled' }
  | { kind: 'noAuthenticator' }
  | { kind: 'alreadyRegistered' }
  | { kind: 'insecure' }
  | { kind: 'invalidOptions' }
  | { kind: 'noResponse' }
  | { kind: 'failed' }
  | { kind: 'server'; status: number; message: string };

/** `navigator.credentials` が投げた例外を、失敗の種類へ分ける。 */
export function classifyWebAuthnError(error: unknown): PasskeyFailure {
  const name =
    typeof error === 'object' && error !== null && 'name' in error
      ? String((error as { name: unknown }).name)
      : '';

  switch (name) {
    case 'NotAllowedError':
    case 'AbortError':
      return { kind: 'cancelled' };
    case 'InvalidStateError':
      return { kind: 'alreadyRegistered' };
    case 'NotSupportedError':
    case 'ConstraintError':
      return { kind: 'noAuthenticator' };
    case 'SecurityError':
      return { kind: 'insecure' };
    default:
      return { kind: 'failed' };
  }
}

// ---- 流れ --------------------------------------------------------------------

/** `navigator.credentials` のうち使うもの。単体試験で差し替える。 */
export interface CredentialsLike {
  create(options?: CredentialCreationOptions): Promise<Credential | null>;
  get(options?: CredentialRequestOptions): Promise<Credential | null>;
}

/** 薄い API 層（`api.ts` の `passkeyApi`）。単体試験で差し替える。 */
export interface PasskeyApi {
  beginSignIn(): Promise<Result<unknown>>;
  completeSignIn(response: AssertionJson): Promise<Result<{ next?: string }>>;
  beginRegistration(password?: string): Promise<Result<unknown>>;
  completeRegistration(name: string, response: AttestationJson): Promise<Result<unknown>>;
}

export type PasskeyOutcome<T> = { ok: true; value: T } | { ok: false; failure: PasskeyFailure };

function serverFailure(result: { status: number; message: string }): PasskeyOutcome<never> {
  return { ok: false, failure: { kind: 'server', status: result.status, message: result.message } };
}

/**
 * パスキーでログインする（3.10.4）。**ユーザー名は取らない**（ディスコバラブル）。
 *
 * **成功しても「ログイン完了」とは言わない。** サーバが Session を発行したあとに、
 * 呼び出し側が状態を読み直す。
 */
export async function signInWithPasskey(
  api: PasskeyApi,
  credentials: CredentialsLike,
  options: { signal?: AbortSignal } = {},
): Promise<PasskeyOutcome<{ next: string }>> {
  const begin = await api.beginSignIn();
  if (!begin.ok) {
    return serverFailure(begin);
  }

  let publicKey: PublicKeyCredentialRequestOptions;
  try {
    const json = unwrapOptions<RequestOptionsJson>(begin.value);
    if (json === null) {
      return { ok: false, failure: { kind: 'invalidOptions' } };
    }
    publicKey = requestOptionsFromJson(json);
  } catch {
    return { ok: false, failure: { kind: 'invalidOptions' } };
  }

  let credential: Credential | null;
  try {
    credential = await credentials.get({ publicKey, signal: options.signal });
  } catch (error) {
    return { ok: false, failure: classifyWebAuthnError(error) };
  }

  const assertion = assertionToJson(credential);
  if (assertion === null) {
    return { ok: false, failure: { kind: 'noResponse' } };
  }

  const complete = await api.completeSignIn(assertion);
  if (!complete.ok) {
    return serverFailure(complete);
  }

  return { ok: true, value: { next: complete.value?.next ?? 'done' } };
}

/**
 * パスキーを登録する（3.10.3）。**開始に直近の再認証を要する**ので、
 * 要求されたとき（401）は `server` の `status: 401` で返り、呼び出し側が本人確認を求める。
 */
export async function registerPasskey(
  api: PasskeyApi,
  credentials: CredentialsLike,
  input: { name: string; password?: string; signal?: AbortSignal },
): Promise<PasskeyOutcome<null>> {
  const begin = await api.beginRegistration(input.password);
  if (!begin.ok) {
    return serverFailure(begin);
  }

  let publicKey: PublicKeyCredentialCreationOptions;
  try {
    const json = unwrapOptions<CreationOptionsJson>(begin.value);
    if (json === null) {
      return { ok: false, failure: { kind: 'invalidOptions' } };
    }
    publicKey = creationOptionsFromJson(json);
  } catch {
    return { ok: false, failure: { kind: 'invalidOptions' } };
  }

  let credential: Credential | null;
  try {
    credential = await credentials.create({ publicKey, signal: input.signal });
  } catch (error) {
    return { ok: false, failure: classifyWebAuthnError(error) };
  }

  const attestation = attestationToJson(credential);
  if (attestation === null) {
    return { ok: false, failure: { kind: 'noResponse' } };
  }

  const complete = await api.completeRegistration(input.name, attestation);
  if (!complete.ok) {
    return serverFailure(complete);
  }

  return { ok: true, value: null };
}
