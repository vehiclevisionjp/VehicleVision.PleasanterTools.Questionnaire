/**
 * 管理者の一覧で「押せる操作」を決める（Issue #156）。
 *
 * ⚠️ **ここで隠すのは、押せない釦を出さないため。**
 * 守りはサーバ側（`AdminUserService`）にある。
 * **画面から入口を隠すだけでは守りにならない**ので、同じ判定を両方に置いている。
 */

import type { AdminUserRow } from './types';
import { t } from './i18n/state.svelte';

/**
 * 選べる役割。**サーバ側の `AdminRole` と揃える。**
 *
 * ⚠️ **並び順は「強い順」。** 画面の選択肢もこの順で出す。
 */
export const ADMIN_ROLES = [
  'Administrator',
  'SurveyAdministrator',
  'UserAdministrator',
  'Auditor',
  'Editor',
] as const;

export type AdminRoleName = (typeof ADMIN_ROLES)[number];

/**
 * 役割の表示名。
 *
 * **知らない役割はそのまま出す。** サーバ側で増えた役割を、
 * 画面が「空欄」にしてしまうより素直
 * （`AdminPermissions` は増える前提の作り。Issue #160）。
 */
export function roleLabel(role: string): string {
  switch (role) {
    case 'Administrator':
      return t('users.role.Administrator');
    case 'SurveyAdministrator':
      return t('users.role.SurveyAdministrator');
    case 'UserAdministrator':
      return t('users.role.UserAdministrator');
    case 'Auditor':
      return t('users.role.Auditor');
    case 'Editor':
      return t('users.role.Editor');
    default:
      return role;
  }
}

/** 2 要素の方針の表示名。**知らない値は空にする**（何も足さない）。 */
export function twoFactorPolicyLabel(policy: string): string {
  switch (policy) {
    case 'Required':
      return t('account.policy.Required');
    case 'Optional':
      return t('account.policy.Optional');
    case 'Disabled':
      return t('account.policy.Disabled');
    default:
      return '';
  }
}

/**
 * 一度でもログインしたことがあるか。
 *
 * ⚠️ **`null` だけを見ない。** サーバの JSON は `null` を省くので、
 * 一度も入っていない人では**項目そのものが来ない**（`undefined`）。
 */
export function hasLoggedIn(user: AdminUserRow): boolean {
  return user.lastLoginAt !== null && user.lastLoginAt !== undefined;
}

/** 招待を受け取る URL。**この形はサーバ側の経路と揃える。** */
export function invitationUrl(token: string, origin: string): string {
  return `${origin}/admin/invitations/accept?token=${encodeURIComponent(token)}`;
}

/**
 * その相手のほかに「入れる Administrator」が居るか。
 *
 * **「入れる」は、有効かつ一度でもログインしたことがある**という意味
 * （サーバ側の `LastAdministrator` の判定と同じ）。
 * 招待を出しただけの人は**まだ入れない**ので数えない。
 */
export function hasOtherUsableAdministrator(
  users: readonly AdminUserRow[],
  adminUserId: string,
): boolean {
  return users.some(
    (user) =>
      user.adminUserId !== adminUserId &&
      user.role === 'Administrator' &&
      !user.isDisabled &&
      hasLoggedIn(user),
  );
}

/**
 * 止められるか。
 *
 * - **自分は止められない**（手が滑ったときに自分では戻せない）
 * - **最後の Administrator は止められない**（誰も入れなくなる）
 */
export function canDisable(
  users: readonly AdminUserRow[],
  target: AdminUserRow,
  ownAdminUserId: string,
): boolean {
  if (target.adminUserId === ownAdminUserId || target.isDisabled) {
    return false;
  }

  return target.role !== 'Administrator' || hasOtherUsableAdministrator(users, target.adminUserId);
}

/** 役割を変えられるか。**自分の役割は変えられない。** */
export function canChangeRole(
  users: readonly AdminUserRow[],
  target: AdminUserRow,
  ownAdminUserId: string,
  nextRole: string,
): boolean {
  if (target.adminUserId === ownAdminUserId || target.role === nextRole) {
    return false;
  }

  // **Administrator から降ろすときだけ、他に入れる人が要る**
  return (
    target.role !== 'Administrator' || hasOtherUsableAdministrator(users, target.adminUserId)
  );
}

/**
 * 2 要素を解除できるか（端末を失った人の救済）。
 *
 * - **相手が登録していなければ、解除するものが無い**
 * - **自分の分はここから解除しない。** パスワードの再確認つきの「自分のアカウント」で行う
 */
export function canResetTwoFactor(target: AdminUserRow, ownAdminUserId: string): boolean {
  return target.hasTotp && target.adminUserId !== ownAdminUserId;
}

/** 招待を出し直せるか。**まだ受け取っていない人だけ。** */
export function canReissueInvitation(target: AdminUserRow): boolean {
  return target.invitationPending;
}
