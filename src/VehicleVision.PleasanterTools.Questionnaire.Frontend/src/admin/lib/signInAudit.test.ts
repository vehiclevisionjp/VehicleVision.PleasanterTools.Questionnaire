import { describe, expect, it } from 'vitest';
import { auditKindOf, loginMethodKey, readSignInNote } from './signInAudit';

describe('readSignInNote', () => {
  it('SAML の失敗の結果と理由を鍵へ直す', () => {
    const note = readSignInNote(
      'POST /api/admin/saml/acs',
      JSON.stringify({ result: 'invalid', reason: 'response-invalid' }),
    );
    expect(note?.resultKey).toBe('audit.signIn.result.invalid');
    expect(note?.reasonKey).toBe('audit.signIn.reason.response-invalid');
  });

  it('知らない理由は原文のまま返す', () => {
    const note = readSignInNote(
      'POST /api/admin/pleasanter-sso/check',
      JSON.stringify({ result: 'upstream-error', reason: 'user-no-row' }),
    );
    expect(note?.resultKey).toBe('audit.signIn.result.upstream-error');
    expect(note?.reasonKey).toBeUndefined();
    expect(note?.reason).toBe('user-no-row');
  });

  it('ログインの操作でなければ読まない', () => {
    expect(readSignInNote('POST /api/admin/users', JSON.stringify({ result: 'x' }))).toBeNull();
  });

  it('補足が壊れていても落とさない', () => {
    expect(readSignInNote('POST /api/admin/saml/acs', '{')).toBeNull();
    expect(readSignInNote('POST /api/admin/saml/acs', null)).toBeNull();
    expect(readSignInNote('POST /api/admin/saml/acs', '{"a":1}')).toBeNull();
  });

  it('プロトタイプの名前を鍵として扱わない', () => {
    const note = readSignInNote(
      'POST /api/admin/saml/acs',
      JSON.stringify({ result: 'constructor' }),
    );
    expect(note?.resultKey).toBeUndefined();
    expect(note?.result).toBe('constructor');
  });
});

describe('auditKindOf', () => {
  it('選択肢と一致する値だけを選ぶ', () => {
    expect(auditKindOf('/saml/acs')).toBe('/saml/acs');
    expect(auditKindOf('users')).toBe('');
  });
});

describe('loginMethodKey', () => {
  it('3 つの方式を鍵へ直し、それ以外は null', () => {
    expect(loginMethodKey('Password')).toBe('sessions.method.Password');
    expect(loginMethodKey('Saml')).toBe('sessions.method.Saml');
    expect(loginMethodKey('PleasanterSso')).toBe('sessions.method.PleasanterSso');
    expect(loginMethodKey(null)).toBeNull();
    expect(loginMethodKey('x')).toBeNull();
  });
});
