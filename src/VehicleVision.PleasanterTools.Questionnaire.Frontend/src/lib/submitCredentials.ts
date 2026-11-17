/**
 * 送信ごとの資格（送信チケットと proof-of-work の解答）の取り直し（Issue #959）。
 *
 * **サーバは proof-of-work の解答を、受付（添付の検査・入力の検証）より前に使い切る。**
 * 同じ解答は 2 度通らない。そのため、受付のあとで 422（添付の拒否・入力の検証）や
 * 503（スキャナ停止）を返した場合も、解答は消費済みになっている。
 * **使い回し防止の意味を変えないために、画面側で、受け付けられなかった送信のあとは毎回取り直す**
 * （403・429・422・503・通信の失敗）。
 */

import { requestTicket } from './api';
import { solveAltcha, type AltchaChallenge } from './altcha';

/** 取り直した資格。 */
export interface FreshCredentials {
  responseToken: string;
  ticket: string;
  /** 解けなかった・課題が無いときは空。 */
  altcha: string;
}

/**
 * チケットと proof-of-work の課題を取り直し、解く。
 *
 * **取れなければ `null`**（通信の失敗を含む）。呼ぶ側は、いま持っている値を残したまま
 * 次の操作で再び試せる。**例外は投げない**（元の失敗の表示を上書きしないため）。
 *
 * **回答トークンは変わらない**（サーバが端末の値を返す）。二重の回答は作らない。
 */
export async function renewSubmitCredentials(
  publicId: string,
  requiresProofOfWork: boolean,
  solve: (challenge: AltchaChallenge) => Promise<string | null> = solveAltcha,
): Promise<FreshCredentials | null> {
  try {
    const { ticket: issued } = await requestTicket(publicId);
    if (!issued) {
      return null;
    }

    // **使い終えた解答は 2 度通らない。** 課題も取り直して解く
    const altcha =
      requiresProofOfWork && issued.altcha ? ((await solve(issued.altcha)) ?? '') : '';

    return { responseToken: issued.responseToken, ticket: issued.ticket, altcha };
  } catch {
    return null;
  }
}
