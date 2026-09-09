import { describe, expect, it } from 'vitest';
import {
  canChangeRole,
  canDisable,
  canReissueInvitation,
  canResetTwoFactor,
  hasOtherUsableAdministrator,
  invitationUrl,
} from './adminUsers';
import type { AdminUserRow } from './types';

/** 試験用の 1 行。**既定は「入れる Administrator」。** */
function row(overrides: Partial<AdminUserRow> = {}): AdminUserRow {
  return {
    adminUserId: overrides.adminUserId ?? '11111111-1111-1111-1111-111111111111',
    loginId: overrides.loginId ?? 'admin',
    role: overrides.role ?? 'Administrator',
    isDisabled: overrides.isDisabled ?? false,
    hasTotp: overrides.hasTotp ?? false,
    invitationPending: overrides.invitationPending ?? false,
    // ⚠️ **`??` を使わない。** `null` を渡したときに既定値へ落ちてしまう
    lastLoginAt: 'lastLoginAt' in overrides ? overrides.lastLoginAt! : '2026-09-01T00:00:00Z',
    createdAt: overrides.createdAt ?? '2026-08-01T00:00:00Z',
  };
}

const me = row({ adminUserId: 'me', loginId: 'me' });
const other = row({ adminUserId: 'other', loginId: 'other' });

describe('入れる Administrator の数え方', () => {
  it('招待を出しただけの人は数えない', () => {
    // **一度も入っていない人を数えると、誰も入れない状態を作れてしまう**
    const invited = row({ adminUserId: 'invited', invitationPending: true, lastLoginAt: null });

    expect(hasOtherUsableAdministrator([me, invited], 'me')).toBe(false);
  });

  it('止められている人は数えない', () => {
    const stopped = row({ adminUserId: 'stopped', isDisabled: true });

    expect(hasOtherUsableAdministrator([me, stopped], 'me')).toBe(false);
  });

  it('Editor は数えない', () => {
    const editor = row({ adminUserId: 'editor', role: 'Editor' });

    expect(hasOtherUsableAdministrator([me, editor], 'me')).toBe(false);
  });

  it('入れる人が居れば数える', () => {
    expect(hasOtherUsableAdministrator([me, other], 'me')).toBe(true);
  });
});

describe('止める', () => {
  it('自分は止められない', () => {
    expect(canDisable([me, other], me, 'me')).toBe(false);
  });

  it('止まっている人は止められない', () => {
    const stopped = row({ adminUserId: 'stopped', isDisabled: true });

    expect(canDisable([me, stopped], stopped, 'me')).toBe(false);
  });

  it('最後の Administrator は止められない', () => {
    const editor = row({ adminUserId: 'editor', role: 'Editor' });

    expect(canDisable([editor, other], other, 'editor')).toBe(false);
  });

  it('ほかに入れる Administrator が居れば止められる', () => {
    expect(canDisable([me, other], other, 'me')).toBe(true);
  });

  it('Editor は 1 人でも止められる', () => {
    const editor = row({ adminUserId: 'editor', role: 'Editor' });

    expect(canDisable([me, editor], editor, 'me')).toBe(true);
  });
});

describe('役割を変える', () => {
  it('自分の役割は変えられない', () => {
    expect(canChangeRole([me, other], me, 'me', 'Editor')).toBe(false);
  });

  it('同じ役割へは変えない', () => {
    expect(canChangeRole([me, other], other, 'me', 'Administrator')).toBe(false);
  });

  it('最後の Administrator は降ろせない', () => {
    const editor = row({ adminUserId: 'editor', role: 'Editor' });

    expect(canChangeRole([editor, other], other, 'editor', 'Editor')).toBe(false);
  });

  it('Editor を上げるのは 1 人目でもよい', () => {
    // **上げる方向は誰も入れなくならない**
    const editor = row({ adminUserId: 'editor', role: 'Editor', lastLoginAt: null });

    expect(canChangeRole([editor], editor, 'me', 'Administrator')).toBe(true);
  });
});

describe('2 要素の解除', () => {
  it('登録していない相手には出さない', () => {
    expect(canResetTwoFactor(other, 'me')).toBe(false);
  });

  it('自分の分はここから解除しない', () => {
    // **パスワードの再確認つきの「自分のアカウント」で行う**
    const self = row({ adminUserId: 'me', hasTotp: true });

    expect(canResetTwoFactor(self, 'me')).toBe(false);
  });

  it('登録している他人は解除できる', () => {
    const enrolled = row({ adminUserId: 'other', hasTotp: true });

    expect(canResetTwoFactor(enrolled, 'me')).toBe(true);
  });
});

describe('招待', () => {
  it('受け取っていない人だけ出し直せる', () => {
    expect(canReissueInvitation(row({ invitationPending: true }))).toBe(true);
    expect(canReissueInvitation(row({ invitationPending: false }))).toBe(false);
  });

  it('URL は受け取る画面へ向ける', () => {
    // **トークンは URL に載る。** 逃がし忘れると壊れる
    expect(invitationUrl('a b+c/d', 'https://example.jp')).toBe(
      'https://example.jp/admin/invitations/accept?token=a%20b%2Bc%2Fd',
    );
  });
});
