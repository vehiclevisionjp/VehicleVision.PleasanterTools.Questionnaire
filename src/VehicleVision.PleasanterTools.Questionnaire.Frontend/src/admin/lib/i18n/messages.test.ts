import { describe, expect, it } from 'vitest';
import { en, ja } from './messages';

describe('一覧へ戻るリンクの文言', () => {
  const removedKeys = [
    'help.back',
    'users.back',
    'account.back',
    'saml.back',
    'appSettings.back',
    'editor.back',
    'audit.back',
    'notifications.back',
    'outbox.back',
  ] as const;

  it.each(removedKeys)('%s を日本語と英語のカタログから除いている', (key) => {
    expect(ja).not.toHaveProperty(key);
    expect(en).not.toHaveProperty(key);
  });
});
