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
  it('回答者の言語に訳が無ければアンケートの落とし先言語を描画する', () => {
    const question = confirmQuestion();
    question.title = { ja: '日本語', en: 'English' };

    const { body } = render(QuestionField, {
      props: {
        question,
        language: 'de',
        fallbackLanguage: 'en',
        answer: { values: [], otherText: '' },
      },
    });

    expect(body).toContain('English');
    expect(body).not.toContain('日本語');
  });

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

describe('回答の自動変換', () => {
  it('有効な設問では回答者へ自動変換を伝える', () => {
    const question: Question = {
      ...confirmQuestion(),
      type: 'Text',
      title: { ja: '郵便番号' },
      settings: { convertFullWidthAsciiToHalfWidth: true },
    };

    const { body } = render(QuestionField, {
      props: { question, language: 'ja', answer: { values: [], otherText: '' } },
    });

    expect(body).toContain('自動で変換します');
    expect(body).toContain('aria-describedby="normalization-confirm1"');
  });

  it('無効な設問では自動変換の案内を出さない', () => {
    const question: Question = {
      ...confirmQuestion(),
      type: 'Text',
      title: { ja: '氏名' },
      settings: {},
    };

    const { body } = render(QuestionField, {
      props: { question, language: 'ja', answer: { values: [], otherText: '' } },
    });

    expect(body).not.toContain('自動で変換します');
  });
});
