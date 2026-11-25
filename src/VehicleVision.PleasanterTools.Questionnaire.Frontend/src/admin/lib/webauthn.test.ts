import { describe, expect, it } from 'vitest';
import {
  assertionToJson,
  attestationToJson,
  base64UrlToBytes,
  bytesToBase64Url,
  classifyWebAuthnError,
  creationOptionsFromJson,
  passkeySupport,
  registerPasskey,
  requestOptionsFromJson,
  signInWithPasskey,
  unwrapOptions,
  type CreationOptionsJson,
  type CredentialsLike,
  type PasskeyApi,
} from './webauthn';
import {
  failureMessage,
  formatPasskeyDate,
  isLastPasskey,
  needsReauthentication,
  normalizePasskeyName,
} from './passkeys';

function buffer(...values: number[]): ArrayBuffer {
  return new Uint8Array(values).buffer;
}

const ok = <T>(value: T) => Promise.resolve({ ok: true as const, value });
const ng = (status: number, message = 'x') =>
  Promise.resolve({ ok: false as const, status, message });

/** API 層のモック。呼ばれた順と引数を残す。 */
function fakeApi(overrides: Partial<PasskeyApi> = {}) {
  const calls: string[] = [];
  const api: PasskeyApi = {
    beginSignIn: () => {
      calls.push('beginSignIn');
      return ok({ challenge: 'AQID', allowCredentials: [] });
    },
    completeSignIn: () => {
      calls.push('completeSignIn');
      return ok({ next: 'done' });
    },
    beginRegistration: (password) => {
      calls.push(`beginRegistration:${password ?? ''}`);
      return ok({
        options: {
          rp: { id: 'example.test', name: 'Q' },
          user: { id: 'AQID', name: 'a', displayName: 'a' },
          challenge: 'BAUG',
          pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
          excludeCredentials: [{ id: 'BwgJ', type: 'public-key' }],
        },
      });
    },
    completeRegistration: (name) => {
      calls.push(`completeRegistration:${name}`);
      return ok({});
    },
    ...overrides,
  };
  return { api, calls };
}

const assertionCredential = {
  id: 'abc',
  type: 'public-key',
  rawId: buffer(1, 2, 3),
  response: {
    authenticatorData: buffer(4),
    clientDataJSON: buffer(5),
    signature: buffer(6),
    userHandle: buffer(7),
  },
};

const attestationCredential = {
  id: 'abc',
  type: 'public-key',
  rawId: buffer(1, 2, 3),
  response: {
    attestationObject: buffer(4),
    clientDataJSON: buffer(5),
    getTransports: () => ['internal'],
  },
};

describe('base64url', () => {
  it('往復できる（パディングなし・URL 安全な文字）', () => {
    const bytes = new Uint8Array([251, 255, 254, 0, 1]);
    const text = bytesToBase64Url(bytes);
    expect(text).toBe('-__-AAE');
    expect([...base64UrlToBytes(text)]).toEqual([...bytes]);
  });

  it('base64url ではない文字は断る', () => {
    expect(() => base64UrlToBytes('a+b/')).toThrow();
  });
});

describe('オプションの変換', () => {
  it('ログインのオプションは allowCredentials が空のまま渡る', () => {
    const options = requestOptionsFromJson({ challenge: 'AQID', rpId: 'example.test' });
    expect([...new Uint8Array(options.challenge as ArrayBuffer)]).toEqual([1, 2, 3]);
    expect(options.allowCredentials).toEqual([]);
    expect(options.rpId).toBe('example.test');
  });

  it('登録のオプションは、ID とチャレンジをバイト列にする', async () => {
    const { api } = fakeApi();
    const result = await api.beginRegistration();
    const json = unwrapOptions<CreationOptionsJson>(result.ok ? result.value : null);
    expect(json).not.toBeNull();

    const options = creationOptionsFromJson(json!);
    expect(options.excludeCredentials).toHaveLength(1);
    expect(options.user.name).toBe('a');
  });

  it('包まれた形と、そのままの形のどちらも開く', () => {
    expect(unwrapOptions({ options: { a: 1 } })).toEqual({ a: 1 });
    expect(unwrapOptions({ publicKey: { a: 2 } })).toEqual({ a: 2 });
    expect(unwrapOptions({ challenge: 'x' })).toEqual({ challenge: 'x' });
    expect(unwrapOptions('x')).toBeNull();
  });
});

describe('資格情報の JSON 化', () => {
  it('登録の応答を base64url にし、transports を添える', () => {
    const json = attestationToJson(attestationCredential)!;
    expect(json.rawId).toBe('AQID');
    expect(json.response.attestationObject).toBe('BA');
    expect(json.response.transports).toEqual(['internal']);
  });

  it('ログインの応答を base64url にする', () => {
    const json = assertionToJson(assertionCredential)!;
    expect(json.response.signature).toBe('Bg');
    expect(json.response.userHandle).toBe('Bw');
  });

  it('形の違う値は null', () => {
    expect(attestationToJson(null)).toBeNull();
    expect(assertionToJson({ type: 'password' })).toBeNull();
    expect(assertionToJson({ ...assertionCredential, response: {} })).toBeNull();
  });
});

describe('対応状況', () => {
  const credentials = {};

  it('対応していて安全な文脈なら ok', () => {
    expect(
      passkeySupport({
        PublicKeyCredential: class {},
        isSecureContext: true,
        navigator: { credentials },
      }),
    ).toBe('ok');
  });

  it('安全でない文脈・未対応を区別する', () => {
    expect(passkeySupport({ isSecureContext: false })).toBe('insecure');
    expect(passkeySupport({ isSecureContext: true, navigator: { credentials } })).toBe('unsupported');
    expect(passkeySupport({ PublicKeyCredential: class {}, navigator: {} })).toBe('unsupported');
  });
});

describe('失敗の分類', () => {
  it.each([
    ['NotAllowedError', 'cancelled'],
    ['AbortError', 'cancelled'],
    ['InvalidStateError', 'alreadyRegistered'],
    ['NotSupportedError', 'noAuthenticator'],
    ['ConstraintError', 'noAuthenticator'],
    ['SecurityError', 'insecure'],
    ['UnknownError', 'failed'],
  ])('%s は %s', (name, kind) => {
    expect(classifyWebAuthnError({ name }).kind).toBe(kind);
  });
});

describe('ログインの流れ', () => {
  const nothing: CredentialsLike = {
    create: () => Promise.resolve(null),
    get: () => Promise.resolve(null),
  };

  it('開始→取得→完了の順に呼び、next を返す', async () => {
    const { api, calls } = fakeApi();
    const credentials: CredentialsLike = {
      ...nothing,
      get: () => Promise.resolve(assertionCredential as unknown as Credential),
    };

    const outcome = await signInWithPasskey(api, credentials);

    expect(outcome).toEqual({ ok: true, value: { next: 'done' } });
    expect(calls).toEqual(['beginSignIn', 'completeSignIn']);
  });

  it('キャンセルされたら完了を呼ばない', async () => {
    const { api, calls } = fakeApi();
    const credentials: CredentialsLike = {
      ...nothing,
      get: () => Promise.reject({ name: 'NotAllowedError' }),
    };

    const outcome = await signInWithPasskey(api, credentials);

    expect(outcome).toEqual({ ok: false, failure: { kind: 'cancelled' } });
    expect(calls).toEqual(['beginSignIn']);
  });

  it('開始が断られたら認証器を呼ばない', async () => {
    const { api } = fakeApi({ beginSignIn: () => ng(404) });
    let called = false;
    const credentials: CredentialsLike = {
      ...nothing,
      get: () => {
        called = true;
        return Promise.resolve(null);
      },
    };

    const outcome = await signInWithPasskey(api, credentials);

    expect(outcome.ok).toBe(false);
    expect(called).toBe(false);
  });

  it('完了が 401 ならサーバの失敗。画面へは決まった文言だけ出す', async () => {
    const { api } = fakeApi({ completeSignIn: () => ng(401, '理由') });
    const credentials: CredentialsLike = {
      ...nothing,
      get: () => Promise.resolve(assertionCredential as unknown as Credential),
    };

    const outcome = await signInWithPasskey(api, credentials);

    expect(outcome.ok).toBe(false);
    if (!outcome.ok) {
      expect(outcome.failure).toEqual({ kind: 'server', status: 401, message: '理由' });
      expect(failureMessage(outcome.failure, 'signIn')).toEqual({ key: 'passkey.error.signInFailed' });
    }
  });

  it('壊れたオプションは invalidOptions', async () => {
    const { api } = fakeApi({ beginSignIn: () => ok({ challenge: '!!' }) });

    expect(await signInWithPasskey(api, nothing)).toEqual({
      ok: false,
      failure: { kind: 'invalidOptions' },
    });
  });
});

describe('登録の流れ', () => {
  it('開始（パスワードを添える）→作成→名前つきで完了', async () => {
    const { api, calls } = fakeApi();
    const credentials: CredentialsLike = {
      create: () => Promise.resolve(attestationCredential as unknown as Credential),
      get: () => Promise.resolve(null),
    };

    const outcome = await registerPasskey(api, credentials, { name: '仕事用 PC', password: 'pw' });

    expect(outcome.ok).toBe(true);
    expect(calls).toEqual(['beginRegistration:pw', 'completeRegistration:仕事用 PC']);
  });

  it('再認証が要る（401）なら、認証器を呼ばずに 401 を返す', async () => {
    const { api } = fakeApi({ beginRegistration: () => ng(401) });
    let called = false;
    const credentials: CredentialsLike = {
      create: () => {
        called = true;
        return Promise.resolve(null);
      },
      get: () => Promise.resolve(null),
    };

    const outcome = await registerPasskey(api, credentials, { name: 'a' });

    expect(called).toBe(false);
    expect(outcome.ok === false && needsReauthentication(outcome.failure)).toBe(true);
  });

  it('同じ認証器は alreadyRegistered', async () => {
    const { api } = fakeApi();
    const credentials: CredentialsLike = {
      create: () => Promise.reject({ name: 'InvalidStateError' }),
      get: () => Promise.resolve(null),
    };

    expect(await registerPasskey(api, credentials, { name: 'a' })).toEqual({
      ok: false,
      failure: { kind: 'alreadyRegistered' },
    });
  });
});

describe('画面の判断', () => {
  it('名前は前後の空白を落とし、空・長すぎるものは断る', () => {
    expect(normalizePasskeyName('  仕事用 PC ')).toBe('仕事用 PC');
    expect(normalizePasskeyName('   ')).toBeNull();
    expect(normalizePasskeyName('a'.repeat(101))).toBeNull();
    expect(normalizePasskeyName('あ'.repeat(100))).toBe('あ'.repeat(100));
  });

  it('日付は不正な値を空にする', () => {
    expect(formatPasskeyDate(null)).toBe('');
    expect(formatPasskeyDate('not a date')).toBe('');
    expect(formatPasskeyDate('2026-11-25T00:00:00Z')).not.toBe('');
  });

  it('最後の 1 件かを判定する', () => {
    const row = { id: 'a', name: 'n', createdAt: '2026-01-01T00:00:00Z' };
    expect(isLastPasskey([row], 'a')).toBe(true);
    expect(isLastPasskey([row, { ...row, id: 'b' }], 'a')).toBe(false);
  });

  it('管理の失敗はサーバの文言を使い、空なら決まった文言', () => {
    expect(failureMessage({ kind: 'server', status: 409, message: '最後です' }, 'manage')).toEqual({
      serverMessage: '最後です',
    });
    expect(failureMessage({ kind: 'server', status: 500, message: '' }, 'manage')).toEqual({
      key: 'passkey.error.failed',
    });
  });
});
