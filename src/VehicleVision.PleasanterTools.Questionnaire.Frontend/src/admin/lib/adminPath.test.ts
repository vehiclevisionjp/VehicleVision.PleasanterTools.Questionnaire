import { describe, expect, it } from 'vitest';
import { adminRoute, adminUrl } from './adminPath';

describe('adminUrl', () => {
  it('変更した入口から管理画面内のURLを組み立てる', () => {
    expect(adminUrl('/surveys/123', '/back-office')).toBe('/back-office/surveys/123');
  });

  it('入口そのものを返す', () => {
    expect(adminUrl('', '/back-office')).toBe('/back-office');
  });

  it('相対パスの書式違反を拒否する', () => {
    expect(() => adminUrl('surveys', '/back-office')).toThrow();
  });
});

describe('adminRoute', () => {
  it('変更した入口を除いた経路を返す', () => {
    expect(adminRoute('/back-office/users', '/back-office')).toBe('/users');
    expect(adminRoute('/back-office/users/', '/back-office')).toBe('/users');
  });

  it('管理画面の外は受け入れない', () => {
    expect(adminRoute('/back-office-other/users', '/back-office')).toBeNull();
  });
});
