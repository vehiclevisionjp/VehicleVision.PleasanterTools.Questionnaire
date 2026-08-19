import type { Page } from '@playwright/test';

/**
 * 豆腐（□）の検出結果。
 *
 * @property tofu 描けていない文字。空なら問題なし。
 * @property fontFamily 判定に使った font-family。
 */
export interface TofuReport {
  tofu: string[];
  fontFamily: string;
}

/**
 * 画面に日本語が描けているかを確かめる。
 *
 * **フォントを入れただけで安心しない。** Linux のコンテナには日本語フォントが無いのが
 * 普通で、入れ忘れれば確実に豆腐（□）になる。しかも**撮った画像を目で見るまで
 * 気付かない**ので、機械で見張る。
 *
 * ## 見分け方
 *
 * **字形を持たない文字は、どれも同じ「豆腐」の字形で描かれる。**
 * そこで、確かめたい文字と「どのフォントにも無い文字」を同じ書体で canvas へ描き、
 * **画素が完全に一致したら描けていない**と判断する。
 *
 * 幅の比較だけでは足りない。等幅の書体では、描けている文字も同じ幅になる。
 */
export async function findTofu(page: Page, samples: string[]): Promise<TofuReport> {
  return page.evaluate((chars: string[]) => {
    const fontFamily = getComputedStyle(document.body).fontFamily;
    const font = `48px ${fontFamily}`;

    const draw = (text: string): string => {
      const canvas = document.createElement('canvas');
      canvas.width = 64;
      canvas.height = 64;

      const context = canvas.getContext('2d');
      if (context === null) {
        throw new Error('canvas の 2d コンテキストを取れない');
      }

      context.font = font;
      context.textBaseline = 'top';
      context.fillStyle = '#000';
      context.fillText(text, 0, 0);
      return canvas.toDataURL();
    };

    // **どのフォントにも字形が無い文字。** これが「豆腐そのもの」の見本になる。
    // U+FFFF は Unicode で「文字として使わない」と決められている
    const tofuSample = draw('￿');
    // **白紙と区別する。** 豆腐すら描かれない書体だと、比較が常に一致してしまう
    const blank = draw(' ');

    const failed: string[] = [];
    for (const char of chars) {
      const drawn = draw(char);

      if (drawn === blank) {
        // 何も描かれていない。**これも「出ていない」**
        failed.push(char);
        continue;
      }

      if (drawn === tofuSample) {
        failed.push(char);
      }
    }

    return { tofu: failed, fontFamily };
  }, samples);
}

/**
 * 取説に載る画面で使う文字の見本。
 *
 * **ひらがな・カタカナ・漢字・全角記号・囲み数字をそれぞれ入れる。**
 * 書体によっては一部だけ欠けることがあり、1 文字だけ見ても気付けない。
 *
 * **空白の文字は入れない。** 全角空白（U+3000）は描けていても白紙なので、
 * 「描けていない」と見分けられない（最初これを入れて誤検知した）。
 */
export const japaneseSamples = ['あ', 'ア', '漢', '％', '①', '髙'];
