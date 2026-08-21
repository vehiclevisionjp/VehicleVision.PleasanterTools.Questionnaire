import { describe, expect, it } from 'vitest';
import type { Question, QuestionType } from './types';
import {
  attachmentRejectionReasonKey,
  conditionOperatorKey,
  conditionOperators,
  displayText,
  hasRowPorts,
  isAttachmentColumn,
  isDisplayOnly,
  isPublished,
  needsConditionValue,
  picksFromChoices,
  problemKey,
  questionTypeKey,
  questionTypes,
  rowPorts,
  suspendedReasonKey,
  surveyStatusKey,
  withText,
} from './types';
import { ja } from './i18n/messages';

function question(type: QuestionType, overrides: Partial<Question> = {}): Question {
  return {
    questionId: 'q1',
    type,
    title: { ja: '設問' },
    isRequired: false,
    choices: [],
    settings: {},
    ...overrides,
  };
}

describe('文言の鍵', () => {
  // **鍵が無ければ画面に鍵そのものが出る。** 形式を足したときの取りこぼしを機械的に見つける
  it('すべての設問の形式に文言がある', () => {
    for (const type of questionTypes) {
      expect(questionTypeKey(type) in ja, type).toBe(true);
    }
  });

  it('すべての比べ方に文言がある', () => {
    for (const operator of conditionOperators) {
      expect(conditionOperatorKey(operator) in ja, operator).toBe(true);
    }
  });

  it('アンケートの状態の文言を引ける', () => {
    for (const status of [0, 1, 2]) {
      expect(surveyStatusKey(status) in ja, `${status}`).toBe(true);
    }
  });

  it('停止の理由は無ければ null', () => {
    expect(suspendedReasonKey(null)).toBeNull();
    expect(suspendedReasonKey(undefined)).toBeNull();
  });

  it('添付を断った理由の文言を引ける', () => {
    for (const reason of [0, 1, 2]) {
      expect(attachmentRejectionReasonKey(reason) in ja, `${reason}`).toBe(true);
    }
  });

  // **サーバに符号が増えても落とさない**
  it('知らない不備の符号は null', () => {
    expect(problemKey('MadeUpCode')).toBeNull();
  });
});

describe('比べ方の性質', () => {
  it('値の要る比べ方を見分ける', () => {
    expect(needsConditionValue('Equals')).toBe(true);
    expect(needsConditionValue('Answered')).toBe(false);
    expect(needsConditionValue('NotAnswered')).toBe(false);
  });

  it('選択肢から選ばせる比べ方を見分ける', () => {
    expect(picksFromChoices('Equals')).toBe(true);
    expect(picksFromChoices('NotEquals')).toBe(true);
    expect(picksFromChoices('Contains')).toBe(false);
    expect(picksFromChoices('Answered')).toBe(false);
  });
});

describe('rowPorts', () => {
  it('グリッドは行をそのまま返す', () => {
    const target = question('Grid', {
      settings: { rows: [{ rowId: 'r1', label: { ja: '行 1' } }] },
    });

    expect(rowPorts(target).map((port) => port.rowId)).toEqual(['r1']);
  });

  // **ランキングは選択肢そのものが入力になる**（入る値は順位）
  it('ランキングは選択肢を返す', () => {
    const target = question('Ranking', {
      choices: [
        { value: 'a', label: { ja: 'A' } },
        { value: 'b', label: { ja: 'B' } },
      ],
    });

    expect(rowPorts(target).map((port) => port.rowId)).toEqual(['a', 'b']);
  });

  it('行を持たない形式は空', () => {
    expect(rowPorts(question('Text'))).toEqual([]);
    expect(hasRowPorts('Text')).toBe(false);
    expect(hasRowPorts('Grid')).toBe(true);
    expect(hasRowPorts('Ranking')).toBe(true);
  });

  it('行がまだ無いグリッドは空', () => {
    expect(rowPorts(question('Grid'))).toEqual([]);
  });
});

describe('isAttachmentColumn', () => {
  it('添付の列を見分ける', () => {
    expect(isAttachmentColumn('AttachmentsA')).toBe(true);
    expect(isAttachmentColumn('AttachmentsZ')).toBe(true);
    expect(isAttachmentColumn('Attachments001')).toBe(true);
  });

  it('添付でない列は偽', () => {
    expect(isAttachmentColumn('ClassA')).toBe(false);
    expect(isAttachmentColumn('Attachments')).toBe(false);
    expect(isAttachmentColumn('Attachments1')).toBe(false);
    expect(isAttachmentColumn('AttachmentsAA')).toBe(false);
    expect(isAttachmentColumn('attachmentsA')).toBe(false);
  });
});

describe('withText と displayText', () => {
  it('その言語の文言を入れ替える', () => {
    expect(withText({ ja: '古い' }, '新しい', 'ja')).toEqual({ ja: '新しい' });
  });

  it('他の言語はそのまま残す', () => {
    expect(withText({ ja: '日本語', en: 'English' }, 'new', 'en')).toEqual({
      ja: '日本語',
      en: 'new',
    });
  });

  // **空にしたら鍵ごと消す。** 空文字が残ると「翻訳がある」ものとして扱われる
  it('空文字を入れるとその言語を消す', () => {
    expect(withText({ ja: '日本語', en: 'English' }, '', 'en')).toEqual({ ja: '日本語' });
  });

  it('元が無くても作れる', () => {
    expect(withText(undefined, '新規', 'ja')).toEqual({ ja: '新規' });
  });

  it('元の値を書き換えない', () => {
    const original = { ja: '日本語' };
    withText(original, 'new', 'en');

    expect(original).toEqual({ ja: '日本語' });
  });

  it('無い言語は既定の言語へ落とす', () => {
    expect(displayText({ ja: '日本語' }, 'en')).toBe('日本語');
    expect(displayText(undefined, 'ja')).toBe('');
  });
});

describe('isDisplayOnly と isPublished', () => {
  it('表示専用の要素を見分ける', () => {
    expect(isDisplayOnly('Note')).toBe(true);
    expect(isDisplayOnly('Text')).toBe(false);
  });

  // **`null` ではなく「無い」で判定する。** サーバは値の無い項目を落として返す
  it('公開済みの版が無ければ未公開', () => {
    const base = {
      surveyId: 's1',
      publicId: 'p1',
      title: '見本',
      pleasanterSiteId: 1,
      status: 0,
      updatedAt: '2026-08-21T00:00:00Z',
      responseCount: 0,
    };

    expect(isPublished({ ...base })).toBe(false);
    expect(isPublished({ ...base, publishedVersion: null })).toBe(false);
    expect(isPublished({ ...base, publishedVersion: 1 })).toBe(true);
  });
});
