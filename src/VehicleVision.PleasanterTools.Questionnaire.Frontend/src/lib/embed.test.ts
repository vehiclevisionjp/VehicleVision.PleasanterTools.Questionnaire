import { describe, expect, it } from 'vitest';
import {
  DEFAULT_EMBED_ASPECT_RATIO,
  MAXIMUM_EMBED_ASPECT_RATIO,
  MINIMUM_EMBED_ASPECT_RATIO,
  embedAspectRatio,
  embedHost,
  embedSource,
  hasThirdPartyEmbed,
  isSafeEmbedUrl,
} from './embed';
import type { EmbedSource, Question } from './types';

function question(embed: EmbedSource | null, type: Question['type'] = 'Embed'): Question {
  return {
    questionId: 'q1',
    type,
    title: { ja: '埋め込み' },
    isRequired: false,
    choices: [],
    settings: { embed },
  };
}

describe('isSafeEmbedUrl', () => {
  it('https を通す', () => {
    expect(isSafeEmbedUrl('https://www.example.com/e')).toBe(true);
  });

  it.each([
    'http://www.example.com/e',
    'javascript:alert(1)',
    'data:text/html,<b>',
    '/relative',
    '',
  ])('https 以外は通さない: %s', (url) => {
    expect(isSafeEmbedUrl(url)).toBe(false);
  });

  it('未指定は通さない', () => {
    expect(isSafeEmbedUrl(undefined)).toBe(false);
    expect(isSafeEmbedUrl(null)).toBe(false);
  });

  it('利用者情報の付いた URL は通さない', () => {
    // **`https://www.example.com@evil.test/` のような紛らわしい URL を出さない**
    expect(isSafeEmbedUrl('https://user:pass@evil.test/e')).toBe(false);
  });
});

describe('embedSource', () => {
  it('埋め込み以外の設問では取り出さない', () => {
    expect(embedSource(question({ kind: 'Image', url: 'https://a.example/i' }, 'Text'))).toBeUndefined();
  });

  it('未設定なら取り出さない', () => {
    expect(embedSource(question(null))).toBeUndefined();
  });

  it('出せない URL は取り出さない', () => {
    // **保存の時点で弾いていても、設定は後から狭められる**
    expect(embedSource(question({ kind: 'Frame', url: 'http://a.example/i' }))).toBeUndefined();
  });

  it('出せるものだけ取り出す', () => {
    const embed: EmbedSource = { kind: 'Image', url: 'https://a.example/i' };
    expect(embedSource(question(embed))).toEqual(embed);
  });
});

describe('embedAspectRatio', () => {
  it('未指定なら既定', () => {
    expect(embedAspectRatio({ kind: 'Frame', url: 'https://a.example/e' })).toBe(
      DEFAULT_EMBED_ASPECT_RATIO,
    );
  });

  it('数でなければ既定', () => {
    expect(
      embedAspectRatio({ kind: 'Frame', url: 'https://a.example/e', aspectRatio: Number.NaN }),
    ).toBe(DEFAULT_EMBED_ASPECT_RATIO);
  });

  it('範囲へ収める', () => {
    expect(
      embedAspectRatio({ kind: 'Frame', url: 'https://a.example/e', aspectRatio: 999 }),
    ).toBe(MAXIMUM_EMBED_ASPECT_RATIO);
    expect(embedAspectRatio({ kind: 'Frame', url: 'https://a.example/e', aspectRatio: 0 })).toBe(
      MINIMUM_EMBED_ASPECT_RATIO,
    );
  });
});

describe('hasThirdPartyEmbed', () => {
  it('出せる埋め込みがあれば真', () => {
    expect(hasThirdPartyEmbed([question({ kind: 'Image', url: 'https://a.example/i' })])).toBe(true);
  });

  it('出せない埋め込みしか無ければ偽', () => {
    // **知らせを出しても、実際には通信しない**
    expect(hasThirdPartyEmbed([question({ kind: 'Image', url: 'http://a.example/i' })])).toBe(false);
  });

  it('埋め込みが無ければ偽', () => {
    expect(hasThirdPartyEmbed([question(null, 'Text')])).toBe(false);
  });
});

describe('embedHost', () => {
  it('ホストを取り出す', () => {
    expect(embedHost({ kind: 'Frame', url: 'https://video.example.net/e/1' })).toBe(
      'video.example.net',
    );
  });

  it('読めない URL では空', () => {
    expect(embedHost({ kind: 'Frame', url: 'not a url' })).toBe('');
  });
});
