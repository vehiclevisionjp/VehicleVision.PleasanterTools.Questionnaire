import { describe, expect, it } from 'vitest';
import { availableLanguages } from './surveyLanguages';

describe('availableLanguages', () => {
  it('1 言語しか文言が無ければその言語だけを返す', () => {
    expect(
      availableLanguages({ title: { ja: '題名' }, pages: [{ questions: [{ title: { ja: '設問' } }] }] }),
    ).toEqual(['ja']);
  });

  it('設問や選択肢にだけある言語も拾い、定義された順に並べる', () => {
    const definition = {
      title: { ja: '題名' },
      pages: [
        {
          questions: [
            {
              title: { ja: '設問' },
              choices: [{ label: { ja: 'はい', vi: 'Có' } }, { label: { en: 'No' } }],
            },
          ],
        },
      ],
    };
    expect(availableLanguages(definition)).toEqual(['ja', 'en', 'vi']);
  });

  it('空の文言だけの言語は数えない', () => {
    expect(availableLanguages({ title: { ja: '題名', en: '  ' } })).toEqual(['ja']);
  });

  it('言語コードでないキーのオブジェクトや、値が配列の完了画面の構造は文言として扱わない', () => {
    expect(
      availableLanguages({
        title: { ja: '題名' },
        confirmationBlocks: { en: [{ type: 'paragraph' }] },
        theme: { accent: '#fff' },
      }),
    ).toEqual(['ja']);
  });

  it('何も無ければ空', () => {
    expect(availableLanguages(undefined)).toEqual([]);
  });
});
