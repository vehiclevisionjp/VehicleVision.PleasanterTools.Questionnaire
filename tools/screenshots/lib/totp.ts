import { createHmac } from 'node:crypto';
import { expect, type Page } from '@playwright/test';

/**
 * Base32（RFC 4648）を復号する。
 *
 * **これだけのために依存を増やさない。** 使うのは認証アプリの共有鍵を読む所だけ。
 */
function decodeBase32(input: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const normalized = input.toUpperCase().replace(/=+$/, '');

  let bits = 0;
  let value = 0;
  const bytes: number[] = [];

  for (const character of normalized) {
    const index = alphabet.indexOf(character);
    if (index === -1) {
      throw new Error(`Base32 として読めない文字が入っている: ${character}`);
    }

    value = (value << 5) | index;
    bits += 5;

    if (bits >= 8) {
      bits -= 8;
      bytes.push((value >>> bits) & 0xff);
    }
  }

  return Buffer.from(bytes);
}

/**
 * 認証アプリが出す 6 桁のコードを作る（RFC 6238 / SHA-1 / 30 秒）。
 *
 * **本体では Otp.NET に任せている。** ここは写しを撮るためだけの実装で、
 * 製品のコードではない。
 */
export function totp(secretBase32: string, at: Date = new Date()): string {
  // **空の共有鍵を黙って通さない。** 空の鍵でも HMAC は計算できてしまい、
  // 「コードが一致しません」という別の症状になって原因が見えなくなる（Issue #484）
  if (secretBase32.trim() === '') {
    throw new Error('共有鍵が空のまま 6 桁のコードを作ろうとした。画面に共有鍵が出る前に読んでいないか確かめること');
  }

  const step = Math.floor(at.getTime() / 1000 / 30);

  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));

  const digest = createHmac('sha1', decodeBase32(secretBase32)).update(counter).digest();
  const offset = digest[digest.length - 1]! & 0x0f;
  const code =
    ((digest[offset]! & 0x7f) << 24) |
    ((digest[offset + 1]! & 0xff) << 16) |
    ((digest[offset + 2]! & 0xff) << 8) |
    (digest[offset + 3]! & 0xff);

  return String(code % 1_000_000).padStart(6, '0');
}

/**
 * 直前に使った枠と違う枠のコードを作る。
 *
 * **同じ 30 秒枠のコードは一度しか通らない**（`AdminAuthenticator`）。
 * 登録した直後にログインし直すと、枠が変わるまで必ず弾かれる。
 * 一式が速くなるほど踏みやすい。**速さで隠れる不具合なので、待って避ける。**
 */
export async function totpInNewWindow(secretBase32: string, usedAt: Date): Promise<string> {
  const usedStep = Math.floor(usedAt.getTime() / 1000 / 30);

  for (;;) {
    const now = new Date();
    if (Math.floor(now.getTime() / 1000 / 30) !== usedStep) {
      return totp(secretBase32, now);
    }

    await new Promise((resolve) => setTimeout(resolve, 500));
  }
}

/**
 * 2 要素の登録画面に出た共有鍵を読む。
 *
 * ⚠️ **見出しが出た時点では、共有鍵はまだ空。** 登録画面は出てから
 * `/api/admin/enroll/begin` を呼び、応答が返ってから共有鍵を描く（`EnrollPanel.svelte`）。
 * 見出しを待っただけで読むと空の文字列を拾い、登録が「コードが一致しません」で断られる
 * （Issue #484。応答が遅れた回だけ落ちる）。**共有鍵が描かれるまで待ってから読む。**
 */
export async function readEnrollmentSecret(page: Page): Promise<string> {
  const code = page.locator('.secret code');
  // **Base32 の文字が描かれるまで待つ。** 固定の時間は待たない
  await expect(code).toHaveText(/[A-Z2-7]{4}/, { timeout: 15_000 });
  return (await code.innerText()).replace(/\s/g, '');
}
