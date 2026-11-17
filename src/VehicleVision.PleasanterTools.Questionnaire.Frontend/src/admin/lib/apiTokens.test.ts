import { describe, expect, it } from 'vitest';
import {
  API_TOKEN_SCOPES,
  activeTokenCount,
  daysRemaining,
  isExpiryInRange,
  parseIpAllowListInput,
  revokeReasonLabelKey,
  scopeLabelKey,
  shouldOfferTokenRevoke,
  statusLabelKey,
} from './apiTokens';
import { en, ja } from './i18n/messages';
import type { ApiTokenRow } from './types';

function row(status: ApiTokenRow['status']): ApiTokenRow {
  return {
    adminApiTokenId: crypto.randomUUID(),
    name: 'ci',
    prefix: 'qat_12345678',
    scopes: ['surveys.read'],
    ipAllowList: [],
    createdAt: '2026-11-18T00:00:00Z',
    expiresAt: '2027-02-16T00:00:00Z',
    status,
  };
}

describe('付けられるスコープ', () => {
  it('許可リストは surveys.read・surveys.write・surveys.publish だけ', () => {
    expect([...API_TOKEN_SCOPES]).toEqual(['surveys.read', 'surveys.write', 'surveys.publish']);
  });

  it('users.*・settings.*・lookups.manage・surveys.delete は選択肢に無い', () => {
    for (const forbidden of ['users.write', 'settings.manage', 'lookups.manage', 'surveys.delete']) {
      expect(API_TOKEN_SCOPES as readonly string[]).not.toContain(forbidden);
      expect(scopeLabelKey(forbidden)).toBeNull();
    }
  });

  it('すべてのスコープに表示の文言がある', () => {
    for (const scope of API_TOKEN_SCOPES) {
      const key = scopeLabelKey(scope);
      expect(key, scope).not.toBeNull();
      expect(ja[key!], scope).toBeTruthy();
      expect(en[key!], scope).toBeTruthy();
    }
  });
});

describe('状態と失効の理由の文言', () => {
  it('3 つの状態に文言がある', () => {
    for (const status of ['active', 'expired', 'revoked'] as const) {
      expect(ja[statusLabelKey(status)]).toBeTruthy();
    }
  });

  it('サーバが返す 5 つの失効の理由すべてに文言がある', () => {
    for (const reason of [
      'owner',
      'administrator',
      'ownerDisabled',
      'passwordChanged',
      'twoFactorReset',
    ]) {
      const key = revokeReasonLabelKey(reason);
      expect(key, reason).not.toBeNull();
      expect(ja[key!], reason).toBeTruthy();
    }
  });

  it('知らない理由・理由なしは文言なし', () => {
    expect(revokeReasonLabelKey('unknown')).toBeNull();
    expect(revokeReasonLabelKey(null)).toBeNull();
    expect(revokeReasonLabelKey(undefined)).toBeNull();
  });
});

describe('接続元の許可リストの入力', () => {
  it('カンマ・空白・改行・セミコロンで分け、重複を除く', () => {
    expect(parseIpAllowListInput('203.0.113.0/24, 198.51.100.7\n2001:db8::/32;203.0.113.0/24')).toEqual([
      '203.0.113.0/24',
      '198.51.100.7',
      '2001:db8::/32',
    ]);
  });

  it('空の入力は空（制限しない）', () => {
    expect(parseIpAllowListInput('')).toEqual([]);
    expect(parseIpAllowListInput('  \n , ')).toEqual([]);
  });

  it('CIDR として正しくない値も捨てない（サーバが断る）', () => {
    expect(parseIpAllowListInput('not-an-ip')).toEqual(['not-an-ip']);
  });
});

describe('期限', () => {
  it('1 日以上・上限以下の整数だけ通る（無期限・0・負・小数は通らない）', () => {
    expect(isExpiryInRange(1, 365)).toBe(true);
    expect(isExpiryInRange(365, 365)).toBe(true);
    expect(isExpiryInRange(0, 365)).toBe(false);
    expect(isExpiryInRange(-1, 365)).toBe(false);
    expect(isExpiryInRange(366, 365)).toBe(false);
    expect(isExpiryInRange(1.5, 365)).toBe(false);
    expect(isExpiryInRange(Number.NaN, 365)).toBe(false);
    expect(isExpiryInRange(Number.POSITIVE_INFINITY, 365)).toBe(false);
  });

  it('残り日数は切り上げ、過ぎていれば 0', () => {
    const now = new Date('2026-11-18T00:00:00Z');
    expect(daysRemaining('2026-11-18T12:00:00Z', now)).toBe(1);
    expect(daysRemaining('2026-11-20T00:00:00Z', now)).toBe(2);
    expect(daysRemaining('2026-11-18T00:00:00Z', now)).toBe(0);
    expect(daysRemaining('2026-11-01T00:00:00Z', now)).toBe(0);
  });
});

describe('API キーも失効させる確認を出すか', () => {
  it('機能が無効なら出さない', () => {
    expect(shouldOfferTokenRevoke(false, 3)).toBe(false);
    expect(shouldOfferTokenRevoke(undefined, 3)).toBe(false);
  });

  it('自分の操作は、使えるキーがあるときだけ', () => {
    expect(shouldOfferTokenRevoke(true, 2)).toBe(true);
    expect(shouldOfferTokenRevoke(true, 0)).toBe(false);
  });

  it('他人の操作（本数が分からない）は、機能が有効なら出す', () => {
    expect(shouldOfferTokenRevoke(true, null)).toBe(true);
  });

  it('使えるキーの数え方は active だけ', () => {
    expect(activeTokenCount([row('active'), row('revoked'), row('expired'), row('active')])).toBe(2);
  });
});
