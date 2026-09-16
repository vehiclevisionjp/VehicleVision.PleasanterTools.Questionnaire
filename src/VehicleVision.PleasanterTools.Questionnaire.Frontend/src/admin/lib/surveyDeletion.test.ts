import { describe, expect, it } from 'vitest';
import { canConfirmSurveyDeletion, deletionResponseCount } from './surveyDeletion';
import type { SurveySummary } from './types';

const archived: SurveySummary = {
  surveyId: '11111111-1111-1111-1111-111111111111',
  publicId: 'public-id',
  title: '顧客満足度調査',
  pleasanterSiteId: 123,
  status: 2,
  updatedAt: '2026-09-16T07:00:00',
  responseCount: 12,
  testResponseCount: 3,
  archivedAt: '2026-09-16T08:00:00',
};

describe('アンケートの完全削除', () => {
  it('アーカイブ済みで題名が完全一致したときだけ確認できる', () => {
    expect(canConfirmSurveyDeletion(archived, '顧客満足度調査')).toBe(true);
    expect(canConfirmSurveyDeletion(archived, ' 顧客満足度調査')).toBe(false);
    expect(canConfirmSurveyDeletion(archived, '顧客満足度調査 ')).toBe(false);
    expect(canConfirmSurveyDeletion({ ...archived, archivedAt: null }, archived.title)).toBe(false);
  });

  it('本番回答とテスト回答を合わせて表示する', () => {
    expect(deletionResponseCount(archived)).toBe(15);
  });
});
