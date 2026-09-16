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
    settings: { descriptionFormat: 'Markup' },
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

describe('設問の説明文', () => {
  it('プレーンでは記法を解釈せず改行を保つ', () => {
    const question: Question = {
      ...confirmQuestion(),
      type: 'Text',
      title: { ja: '設問' },
      description: { ja: '**そのまま**\n次の行<script>alert(1)</script>' },
      settings: {},
    };

    const { body } = render(QuestionField, {
      props: { question, language: 'ja', answer: { values: [], otherText: '' } },
    });

    expect(body).toContain('**そのまま**\n次の行');
    expect(body).toContain('&lt;script>alert(1)&lt;/script>');
    expect(body).not.toContain('<strong>');
    expect(body).not.toContain('<script>');
  });

  it('記法ではサーバが組み立てた要素を描画する', () => {
    const question: Question = {
      ...confirmQuestion(),
      type: 'Text',
      title: { ja: '設問' },
    };

    const { body } = render(QuestionField, {
      props: { question, language: 'ja', answer: { values: [], otherText: '' } },
    });

    expect(body).toContain(
      '<a href="https://example.com/terms" target="_blank" rel="noopener noreferrer"',
    );
  });
});
