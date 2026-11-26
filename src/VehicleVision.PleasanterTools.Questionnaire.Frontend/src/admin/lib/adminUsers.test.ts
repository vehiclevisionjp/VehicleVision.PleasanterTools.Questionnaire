import { describe, expect, it } from 'vitest';
import {
  canChangeRole,
  hasLoggedIn,
  canDisable,
  canMarkPasswordless,
  canMarkPasswordSet,
  canReissueInvitation,
  canResetPasskeys,
  canResetTwoFactor,
  hasOtherUsableAdministrator,
  invitationUrl,
  isPasswordless,
  resetPasskeysFailureKey,
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
    // 件数は、権限がないとき・パスキーが無効のときは項目そのものが来ない
    ...('passkeyCount' in overrides ? { passkeyCount: overrides.passkeyCount } : {}),
    invitationPending: overrides.invitationPending ?? false,
    passwordSet: overrides.passwordSet ?? true,
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

describe('一度も入っていない人の見分け方', () => {
  it('項目が来ないときも「入っていない」とみなす', () => {
    // ⚠️ **サーバの JSON は null を省く**（DefaultIgnoreCondition = WhenWritingNull）。
    // null だけを見ていると undefined を取りこぼし、
    // **招待を出しただけの人を「入れる管理者」と数えてしまう**（実際に踏んだ）
    const invited = row({ adminUserId: 'invited', invitationPending: true });
    delete (invited as { lastLoginAt?: string | null }).lastLoginAt;

    expect(hasLoggedIn(invited)).toBe(false);
    expect(hasOtherUsableAdministrator([me, invited], 'me')).toBe(false);
  });

  it('日時が入っていれば「入っている」', () => {
    expect(hasLoggedIn(other)).toBe(true);
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

describe('パスキーの復旧（Issue #1087）', () => {
  it('登録している他人には出す', () => {
    expect(canResetPasskeys(row({ adminUserId: 'other', passkeyCount: 2 }), 'me')).toBe(true);
  });

  it('消すものが無い相手には出さない', () => {
    expect(canResetPasskeys(row({ adminUserId: 'other', passkeyCount: 0 }), 'me')).toBe(false);
  });

  it('件数が来ない（権限がない・パスキーが無効）ときは出さない', () => {
    expect(canResetPasskeys(row({ adminUserId: 'other' }), 'me')).toBe(false);
  });

  it('自分の分はここから消さない（Core が自分を拒む。自分の設定で 1 件ずつ消せる）', () => {
    expect(canResetPasskeys(row({ adminUserId: 'me', passkeyCount: 3 }), 'me')).toBe(false);
  });

  it('失敗は code から文言の鍵にする', () => {
    expect(resetPasskeysFailureKey(400, { code: 'users.selfNotAllowed' })).toBe(
      'users.passkeys.error.self',
    );
    expect(resetPasskeysFailureKey(404, { code: 'users.notFound' })).toBe(
      'users.passkeys.error.notFound',
    );
    expect(resetPasskeysFailureKey(403, { code: 'auth.forbidden' })).toBe(
      'users.passkeys.error.forbidden',
    );
    // パスキーが無効の間は、経路そのものが本文なしの 404
    expect(resetPasskeysFailureKey(404, undefined)).toBe('users.passkeys.error.disabled');
    expect(resetPasskeysFailureKey(500, undefined)).toBe('users.failed');
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

  it('変更した管理画面パスからURLを組み立てる', () => {
    expect(invitationUrl('token', 'https://example.jp', '/back-office')).toBe(
      'https://example.jp/back-office/invitations/accept?token=token',
    );
  });
});

describe('「パスワードなし（SSO のみ）」の印（Issue #1086）', () => {
  const jit = row({ adminUserId: 'jit', passwordSet: false });

  it('印が偽でも招待中の人は「パスワードなし」と見せない', () => {
    expect(isPasswordless(jit)).toBe(true);
    expect(isPasswordless(row({ passwordSet: false, invitationPending: true }))).toBe(false);
    expect(isPasswordless(row())).toBe(false);
  });

  it('付けられるのは、パスワードありの人（自分にも出す。招待中には出さない）', () => {
    expect(canMarkPasswordless(row())).toBe(true);
    expect(canMarkPasswordless(me)).toBe(true);
    expect(canMarkPasswordless(jit)).toBe(false);
    expect(canMarkPasswordless(row({ invitationPending: true }))).toBe(false);
  });

  it('戻せるのは、パスワードなしの他人だけ（自分には出さない）', () => {
    expect(canMarkPasswordSet(jit, 'me')).toBe(true);
    expect(canMarkPasswordSet(row({ adminUserId: 'me', passwordSet: false }), 'me')).toBe(false);
    expect(canMarkPasswordSet(row(), 'me')).toBe(false);
  });
});
