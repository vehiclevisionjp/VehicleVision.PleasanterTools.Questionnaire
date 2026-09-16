import { render } from 'svelte/server';
import { describe, expect, it } from 'vitest';
import QuestionField from './QuestionField.svelte';
import type { Question } from '../lib/types';

function confirmQuestion(): Question {
  return {
    questionId: 'confirm1',
    type: 'Confirm',
    title: { ja: '利用規約に同意します' },
    description: { ja: '[利用規約](https://example.com/terms)' },
    descriptionBlocks: {
      ja: [
        {
          kind: 'Paragraph',
          inlines: [
            {
              kind: 'Link',
              text: '利用規約',
              href: 'https://example.com/terms',
            },
          ],
          items: [],
          level: 0,
        },
      ],
    },
    isRequired: true,
    choices: [],
    settings: {},
  };
}

describe('確認・同意の回答欄', () => {
  it('見出しを含むラベルと安全な説明リンクを描画する', () => {
    const { body } = render(QuestionField, {
      props: {
        question: confirmQuestion(),
        language: 'ja',
        answer: { values: ['true'], otherText: '' },
      },
    });

    expect(body).toMatch(/<label class="choice confirm [^"]+">/);
    expect(body).toContain('type="checkbox" checked=""');
    expect(body).toContain('利用規約に同意します');
    expect(body).toMatch(
      /<a href="https:\/\/example\.com\/terms" target="_blank" rel="noopener noreferrer"[^>]*>利用規約<\/a>/,
    );
    expect(body).not.toContain('value="true"');
  });
});
