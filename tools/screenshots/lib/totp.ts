import { createHmac } from 'node:crypto';

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
