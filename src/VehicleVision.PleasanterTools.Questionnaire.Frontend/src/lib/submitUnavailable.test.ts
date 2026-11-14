import { describe, expect, it } from 'vitest';
import { maintenanceNotice, unavailableNotice } from './submitUnavailable';

const messages = { maintenance: '固定のメンテナンス文言', unavailable: '固定の一時的な不調の文言' };

describe('maintenanceNotice', () => {
  it('日本語・英語の画面では、サーバの案内を優先する', () => {
    expect(maintenanceNotice('22 時まで停止します', 'ja', messages.maintenance)).toBe('22 時まで停止します');
    expect(maintenanceNotice('Back at 22:00', 'en', messages.maintenance)).toBe('Back at 22:00');
  });

  it('それ以外の言語の画面では、サーバの案内を使わず翻訳済みの固定文言にする', () => {
    for (const language of ['zh', 'de', 'ko', 'es', 'vi'] as const) {
      expect(maintenanceNotice('22 時まで停止します', language, messages.maintenance)).toBe(
        messages.maintenance,
      );
    }
  });

  it('サーバの案内が無い・空白だけなら固定文言にする', () => {
    expect(maintenanceNotice(undefined, 'ja', messages.maintenance)).toBe(messages.maintenance);
    expect(maintenanceNotice('', 'en', messages.maintenance)).toBe(messages.maintenance);
    expect(maintenanceNotice('  \n', 'ja', messages.maintenance)).toBe(messages.maintenance);
  });
});

describe('unavailableNotice', () => {
  it('メンテナンスと一時的な不調で文言を分ける', () => {
    expect(
      unavailableNotice({ unavailable: 'maintenance', maintenanceMessage: '' }, 'ja', messages),
    ).toBe(messages.maintenance);
    expect(unavailableNotice({ unavailable: 'temporary' }, 'ja', messages)).toBe(messages.unavailable);
  });

  it('サーバ側の都合でなければ null（呼び出し側が従来の扱いを続ける）', () => {
    expect(unavailableNotice({}, 'ja', messages)).toBeNull();
  });
});
