import type { QuestionSettings } from './types';

const HALF_WIDTH_KANA =
  '｡｢｣､･ｦｧｨｩｪｫｬｭｮｯｰｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝﾞﾟ';
const FULL_WIDTH_KANA =
  '。「」、・ヲァィゥェォャュョッーアイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン゛゜';

const kanaMappings = new Map<string, string>(
  [...HALF_WIDTH_KANA].map((character, index) => [character, [...FULL_WIDTH_KANA][index] ?? character]),
);

for (const [halfWidth, mark, fullWidth] of [
  ['ｳｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾊﾋﾌﾍﾎ', 'ﾞ', 'ヴガギグゲゴザジズゼゾダヂヅデドバビブベボ'],
  ['ﾊﾋﾌﾍﾎ', 'ﾟ', 'パピプペポ'],
] as const) {
  [...halfWidth].forEach((character, index) => {
    kanaMappings.set(`${character}${mark}`, [...fullWidth][index] ?? character);
  });
}

/** 設問の指定に従って自由入力の見た目をサーバと同じ形へ揃える。 */
export function normalizeAnswer(value: string, settings: QuestionSettings): string {
  let normalized = value;

  if (settings.convertFullWidthAsciiToHalfWidth) {
    normalized = [...normalized]
      .map((character) => {
        const code = character.codePointAt(0) ?? 0;
        return code >= 0xff01 && code <= 0xff5e
          ? String.fromCodePoint(code - 0xfee0)
          : character;
      })
      .join('');
  }

  if (settings.convertHalfWidthKanaToFullWidth) {
    let converted = '';
    for (let index = 0; index < normalized.length; index += 1) {
      const pair = normalized.slice(index, index + 2);
      const paired = kanaMappings.get(pair);
      if (paired !== undefined) {
        converted += paired;
        index += 1;
      } else {
        const character = normalized[index] ?? '';
        converted += kanaMappings.get(character) ?? character;
      }
    }
    normalized = converted;
  }

  if (settings.convertFullWidthSpacesToHalfWidth) {
    normalized = normalized.replaceAll('\u3000', ' ');
  }

  if (settings.trimWhitespace) {
    normalized = normalized.trim();
  }

  return normalized;
}

/** 1 つでも回答の自動変換を有効にしているか。 */
export function hasAnswerNormalization(settings: QuestionSettings): boolean {
  return Boolean(
    settings.convertFullWidthAsciiToHalfWidth ||
      settings.convertHalfWidthKanaToFullWidth ||
      settings.convertFullWidthSpacesToHalfWidth ||
      settings.trimWhitespace,
  );
}
