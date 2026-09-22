import { describe, expect, it } from 'vitest';
import { MAXIMUM_EMBEDDED_FORM_HEIGHT } from '../../lib/framed';
import { iframeTag } from './iframeTag';

describe('貼り付け用コード', () => {
  const tag = iframeTag(
    'https://questionnaire.example/f/pub-123',
    'お客様アンケート',
    'pub-123',
  );

  it('通知元の生成元を照合する', () => {
    expect(tag).toContain('event.origin !== applicationOrigin');
    expect(tag).toContain('const applicationOrigin = "https://questionnaire.example"');
  });

  it('通知元の iframe を contentWindow で照合する', () => {
    expect(tag).toContain('event.source !== iframe.contentWindow');
  });

  it('公開 ID を照合する', () => {
    expect(tag).toContain("message.publicId !== publicId");
    expect(tag).toContain('const publicId = "pub-123"');
  });

  it('高さの上限を照合する', () => {
    expect(tag).toContain(`const maximumHeight = ${MAXIMUM_EMBEDDED_FORM_HEIGHT}`);
    expect(tag).toContain('message.height > maximumHeight');
  });
});
