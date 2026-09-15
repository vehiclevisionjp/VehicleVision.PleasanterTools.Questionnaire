/**
 * proof-of-work の課題を解く（Issue #55）。
 *
 * **部品を足さずに自分で書いている。**
 * 公式の部品は Web Component で、読み上げの文言も見た目も持ってくる。
 * **解く処理そのものは十数行**なので、依存を増やすより自分で持つ方が軽い。
 *
 * **項目名はサーバが実際に出す形に合わせてある**
 * （`AltchaWireFormatTests` が実物の JSON を見て確かめている）。
 * **仕様書ではなく実物が正。**
 *
 * **これは人かどうかを確かめるものではない。** 総当たりの費用を上げるだけで、
 * 送信チケット・最短時間・honeypot と重ねて初めて意味がある。
 */

import { sha256Hex } from './sha256';

/** サーバから来る課題。 */
export interface AltchaChallenge {
  algorithm: string;
  challenge: string;
  salt: string;
  signature: string;
  maxnumber: number;
}

/** 16 進の文字列にする。 */
function toHex(buffer: ArrayBuffer): string {
  return Array.from(new Uint8Array(buffer))
    .map((byte) => byte.toString(16).padStart(2, '0'))
    .join('');
}

/**
 * 1 回分の計算。
 *
 * **`crypto.subtle` があればそれを使い、無ければ自前で計算する。**
 * Web Crypto は「安全なコンテキスト」（https か localhost）でしか使えず、
 * 平文の http で開かれると `undefined` になる。
 * **そこで諦めると、回答の送信が丸ごと通らなくなる**（実際に踏んだ）。
 */
async function digest(bytes: Uint8Array<ArrayBuffer>): Promise<string> {
  if (typeof crypto !== 'undefined' && typeof crypto.subtle !== 'undefined') {
    return toHex(await crypto.subtle.digest('SHA-256', bytes));
  }

  return sha256Hex(bytes);
}

/**
 * 課題を解いて、送信に添える解答を作る。
 *
 * **解けなければ `null`。** 送信を止めるのではなく、呼ぶ側が判断できるようにする。
 *
 * **画面を止めない。** 一定回数ごとに制御を返すので、
 * 解いている間も入力できる。
 */
export async function solveAltcha(
  challenge: AltchaChallenge,
  signal?: AbortSignal,
): Promise<string | null> {
  // **知らない算法は解かない。** 黙って間違った答えを作らない
  if (challenge.algorithm !== 'SHA-256') {
    return null;
  }

  const encoder = new TextEncoder();

  for (let number = 0; number <= challenge.maxnumber; number++) {
    if (signal?.aborted === true) {
      return null;
    }

    const hash = await digest(encoder.encode(challenge.salt + String(number)));

    if (hash === challenge.challenge) {
      return btoa(
        JSON.stringify({
          algorithm: challenge.algorithm,
          challenge: challenge.challenge,
          number,
          salt: challenge.salt,
          signature: challenge.signature,
        }),
      );
    }

    // **時々そのページへ制御を返す。** 詰めて回すと入力が固まる
    if (number % 2000 === 0) {
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
  }

  return null;
}
