import { describe, expect, it } from 'vitest';
import { autoReplyKey, validateAutoReply } from './autoReply';
import type { AutoReplySettings, Question, SurveyDefinition } from './types';

/**
 * 自動返信の設定の検査（Issue #189）。
 *
 * ⚠️ **サーバ側の `AutoReplyValidator` と同じ規則を持っている。**
 * 片方だけ緩めないこと（対になる試験は `Core.Tests/Validation/AutoReplyValidatorTests.cs`）。
 */
const email: Question = {
  questionId: 'mail',
  type: 'Text',
  title: { ja: 'メールアドレス' },
  isRequired: false,
  choices: [],
  settings: { format: 'Email' },
};

const free: Question = {
  questionId: 'free',
  type: 'Text',
  title: { ja: 'ご意見' },
  isRequired: false,
  choices: [],
  settings: {},
};

function definition(autoReply: AutoReplySettings | null, ...questions: Question[]): SurveyDefinition {
  return {
    surveyId: 's1',
    version: 1,
    title: { ja: '検証用' },
    displayMode: 'Paged',
    showProgress: true,
    allowEditingAfterSubmit: true,
    autoReply,
    pages: [{ pageId: 'p1', questions }],
  };
}

const valid: AutoReplySettings = {
  enabled: true,
  toQuestionId: 'mail',
  subject: { ja: 'ご回答ありがとうございました' },
  body: { ja: '受け付けました。' },
};

function codes(definitionValue: SurveyDefinition): string[] {
  return validateAutoReply(definitionValue).map((problem) => problem.code);
}

describe('validateAutoReply', () => {
  it('設定していなければ何も言わない', () => {
    expect(codes(definition(null, email))).toEqual([]);
  });

  it('無効なら中身を見ない', () => {
    expect(codes(definition({ enabled: false }, email))).toEqual([]);
  });

  it('揃っていれば通る', () => {
    expect(codes(definition(valid, email))).toEqual([]);
  });

  it('宛先が未指定なら止める', () => {
    expect(codes(definition({ ...valid, toQuestionId: null }, email))).toEqual([
      'ToQuestionMissing',
    ]);
  });

  it('消した設問を指していたら止める', () => {
    expect(codes(definition(valid, free))).toEqual(['ToQuestionNotFound']);
  });

  it('メール形式でない設問は宛先にできない', () => {
    // **何を書いても通る欄を宛先にすると、送信は必ず失敗する**
    expect(codes(definition({ ...valid, toQuestionId: 'free' }, email, free))).toEqual([
      'ToQuestionNotEmail',
    ]);
  });

  it('件名と本文が空なら止める', () => {
    expect(codes(definition({ ...valid, subject: { ja: '  ' }, body: undefined }, email))).toEqual([
      'SubjectMissing',
      'BodyMissing',
    ]);
  });

  it('英語だけ書いてあっても不備にしない', () => {
    // **翻訳漏れであって不備ではない**（既定の言語へ落ちる）
    const settings = { ...valid, subject: { en: 'Thank you' }, body: { en: 'Received.' } };
    expect(codes(definition(settings, email))).toEqual([]);
  });
});

describe('再編集リンク（Issue #202）', () => {
  it('編集を許していなければ止める', () => {
    // **開いても直せないリンクを送らない**
    const settings = { ...valid, body: { ja: '{{editUrl}}' } };
    const survey = { ...definition(settings, email), allowEditingAfterSubmit: false };

    expect(validateAutoReply(survey).map((problem) => problem.code)).toEqual([
      'EditLinkKeywordUnavailable',
    ]);
  });

  it('編集を許していれば通る', () => {
    expect(codes(definition({ ...valid, body: { ja: '{{editUrl}}' } }, email))).toEqual([]);
  });

  it('範囲外の日数は止める', () => {
    // **永久に生きるリンクを作らせない**
    const settings = { ...valid, body: { ja: '{{editUrlExpiresAt}}' }, editLinkDays: 0 };

    expect(codes(definition(settings, email))).toEqual(['EditLinkDaysInvalid']);
  });

  it('リンクを付けないなら日数は見ない', () => {
    expect(codes(definition({ ...valid, editLinkDays: 0 }, email))).toEqual([]);
  });
});

describe('配布リンク（Issue #319）', () => {
  const confirmationMessage = {
    ja: '[資料](asset:11111111-1111-1111-1111-111111111111)',
  };

  it('配布物が無ければ止める', () => {
    expect(codes(definition({ ...valid, body: { ja: '{{assetsUrl}}' } }, email))).toEqual([
      'AssetsUrlKeywordUnavailable',
    ]);
  });

  it('完了時だけ配る設定なら止める', () => {
    const survey = {
      ...definition({ ...valid, body: { ja: '{{assetsUrlExpiresAt}}' } }, email),
      confirmationMessage,
      assetDelivery: { expiration: 'CompletedOnly' as const, days: 30 },
    };

    expect(codes(survey)).toEqual(['AssetsUrlKeywordUnavailable']);
  });

  it('メールで配れる資産があれば通る', () => {
    const survey = {
      ...definition({ ...valid, body: { ja: '{{assetsUrl}}' } }, email),
      confirmationMessage,
    };

    expect(codes(survey)).toEqual([]);
  });
});

describe('autoReplyKey', () => {
  it('知っている符号は文言の鍵になる', () => {
    expect(autoReplyKey('SubjectMissing')).toBe('autoReply.problem.SubjectMissing');
  });

  it('知らない符号では null を返す', () => {
    // **サーバ側に符号が増えたときに落とさない**
    expect(autoReplyKey('SomethingNew')).toBeNull();
  });
});
