import { describe, expect, it } from 'vitest';
import { sha256Hex } from './sha256';

function bytes(value: string): Uint8Array<ArrayBuffer> {
  return new TextEncoder().encode(value) as Uint8Array<ArrayBuffer>;
}

/**
 * SHA-256 の控え（Issue #55）。
 *
 * **既知の値（FIPS 180-4 と RFC の例）で確かめる。**
 * 自前の実装なので、他の実装と突き合わせないと壊れても気付けない。
 * **壊れると bot 対策の解答が合わず、回答の送信が丸ごと通らなくなる。**
 */
describe('sha256Hex', () => {
  it('空の入力', () => {
    expect(sha256Hex(bytes(''))).toBe(
      'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
    );
  });

  it('abc', () => {
    expect(sha256Hex(bytes('abc'))).toBe(
      'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad',
    );
  });

  // **56 バイト。** 詰め物が 1 ブロック増える境界
  it('詰め物がブロックをまたぐ長さ', () => {
    expect(
      sha256Hex(bytes('abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq')),
    ).toBe('248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1');
  });

  // **64 バイトちょうど。** ブロックの区切りと同じ長さ
  it('ブロックちょうどの長さ', () => {
    expect(sha256Hex(bytes('a'.repeat(64)))).toBe(
      'ffe054fe7ae0cb6dc65c3af9b61d5209f439851db43d0ba5997337df154668eb',
    );
  });

  it('2 ブロックを超える長さ', () => {
    expect(sha256Hex(bytes('a'.repeat(1000)))).toBe(
      '41edece42d63e8d9bf515a9ba6932e1c20cbc9f5a5d134645adb5db1b9737ea3',
    );
  });

  // **多バイト文字も byte 列として扱う**（課題はサーバが出した文字列そのもの）
  it('日本語を含む入力', () => {
    expect(sha256Hex(bytes('こんにちは'))).toBe(
      '125aeadf27b0459b8760c13a3d80912dfa8a81a68261906f60d87f4a0268646c',
    );
  });

  it('同じ入力なら何度でも同じ値', () => {
    expect(sha256Hex(bytes('繰り返し'))).toBe(sha256Hex(bytes('繰り返し')));
  });

  it('16 進の小文字 64 桁で返す', () => {
    expect(sha256Hex(bytes('形式'))).toMatch(/^[0-9a-f]{64}$/);
  });
});
