import { describe, expect, it } from 'vitest';
import {
  heightReportTargetOrigin,
  framedHeaders,
  isFramed,
  MAXIMUM_EMBEDDED_FORM_HEIGHT,
  receivedEmbeddedFormHeight,
  shouldReportHeight,
} from './framed';

describe('枠内の判定', () => {
  it('自分自身が最上位なら枠外と判定する', () => {
    const page = {};

    expect(isFramed({ self: page, top: page })).toBe(false);
    expect(framedHeaders({ self: page, top: page })).toEqual({});
  });

  it('最上位が別なら申告ヘッダを付ける', () => {
    expect(isFramed({ self: {}, top: {} })).toBe(true);
    expect(framedHeaders({ self: {}, top: {} })).toEqual({
      'X-Questionnaire-Framed': '1',
    });
  });

  describe('高さの通知先', () => {
    it('枠内で参照元があれば、その生成元だけへ送る', () => {
      expect(heightReportTargetOrigin(true, 'https://parent.example/page?survey=1')).toBe(
        'https://parent.example',
      );
    });

    it.each([
      [false, 'https://parent.example/page'],
      [true, ''],
      [true, 'not a URL'],
    ])('送信先が確定しない場合は送らない', (insideFrame, referrer) => {
      expect(heightReportTargetOrigin(insideFrame, referrer)).toBeNull();
    });

    it('前回と異なる有効な高さだけ送る', () => {
      expect(shouldReportHeight(null, 480)).toBe(true);
      expect(shouldReportHeight(480, 480)).toBe(false);
      expect(shouldReportHeight(480, -1)).toBe(false);
      expect(shouldReportHeight(480, 1.5)).toBe(false);
    });
  });

  describe('埋め込み回答画面の高さの受信', () => {
    const applicationOrigin = 'https://questionnaire.example';
    const iframeWindow = {};
    const publicId = 'pub-123';
    const message = { type: 'questionnaire:height', publicId, height: 640 };

    it('生成元、送信元、公開 ID が一致する高さだけ受け入れる', () => {
      expect(
        receivedEmbeddedFormHeight(
          { origin: applicationOrigin, source: iframeWindow, data: message },
          applicationOrigin,
          iframeWindow,
          publicId,
        ),
      ).toBe(640);
    });

    it('別の生成元からの通知を無視する', () => {
      expect(
        receivedEmbeddedFormHeight(
          { origin: 'https://attacker.example', source: iframeWindow, data: message },
          applicationOrigin,
          iframeWindow,
          publicId,
        ),
      ).toBeNull();
    });

    it('別の iframe を装った通知を無視する', () => {
      expect(
        receivedEmbeddedFormHeight(
          { origin: applicationOrigin, source: {}, data: message },
          applicationOrigin,
          iframeWindow,
          publicId,
        ),
      ).toBeNull();
    });

    it('上限を超える高さ、別の公開 ID、形式外の通知を無視する', () => {
      expect(
        receivedEmbeddedFormHeight(
          {
            origin: applicationOrigin,
            source: iframeWindow,
            data: { ...message, height: MAXIMUM_EMBEDDED_FORM_HEIGHT + 1 },
          },
          applicationOrigin,
          iframeWindow,
          publicId,
        ),
      ).toBeNull();
      expect(
        receivedEmbeddedFormHeight(
          { origin: applicationOrigin, source: iframeWindow, data: { ...message, publicId: 'pub-other' } },
          applicationOrigin,
          iframeWindow,
          publicId,
        ),
      ).toBeNull();
      expect(
        receivedEmbeddedFormHeight(
          { origin: applicationOrigin, source: iframeWindow, data: { type: 'other' } },
          applicationOrigin,
          iframeWindow,
          publicId,
        ),
      ).toBeNull();
    });
  });
});
