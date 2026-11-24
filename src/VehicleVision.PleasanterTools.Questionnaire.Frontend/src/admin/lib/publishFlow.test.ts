import { describe, expect, it } from 'vitest';
import {
  canPublish,
  isTestPublishChecked,
  needsReflectChoice,
  showsTestPublishCheck,
} from './publishFlow';

describe('公開の流れ', () => {
  it('本公開は下書きとテスト公開中から押せる', () => {
    expect([0, 1, 2, 3].map(canPublish)).toEqual([true, false, false, true]);
  });

  it('テスト公開のチェックは下書きとテスト公開中だけに出し、テスト公開中だけ ON', () => {
    expect([0, 1, 2, 3].map(showsTestPublishCheck)).toEqual([true, false, false, true]);
    expect([0, 1, 2, 3].map(isTestPublishChecked)).toEqual([false, false, false, true]);
  });

  it('下書きを反映するかの選択は、テスト公開中に食い違いがあるときだけ', () => {
    expect(needsReflectChoice(3, true, false)).toBe(true);
    expect(needsReflectChoice(3, false, true)).toBe(true);
    expect(needsReflectChoice(3, false, false)).toBe(false);
    // 下書きから本公開するときは、その場で固めるので選ばせない
    expect(needsReflectChoice(0, true, true)).toBe(false);
    expect(needsReflectChoice(1, true, true)).toBe(false);
  });
});
