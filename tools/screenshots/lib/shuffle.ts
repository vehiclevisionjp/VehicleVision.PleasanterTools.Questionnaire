import type { APIRequestContext } from '@playwright/test';

/**
 * **選択肢と設問の並べ替え**を確かめる見本を作って公開する（Issue #103 / #126）。
 *
 * **形はこう。**
 *
 * ```text
 * ページ1  q-color「好きな色」 選択肢01〜08 ＋ その他
 * ページ2  q-note（説明文ブロック）
 *          q-a〜q-e「設問A」〜「設問E」
 * ```
 *
 * **並べ替えの有無だけが違う 2 つを作る。**
 * 「並びが変わった」ことは、**変わらない側と見比べないと**確かめたことにならない。
 *
 * ⚠️ **選択肢は 8 つ、動かせる設問は 5 つ用意する。**
 * 数が少ないと「たまたま同じ並びになった」が現実的な確率で起き、試験が揺れる。
 * 8 つなら 40320 通り、5 つなら 120 通りあるので、数回読み直せば必ず散る。
 *
 * @param shuffle 並べ替えを効かせるか。**`false` の側が対照。**
 */
export async function prepareShuffleSurvey(
  request: APIRequestContext,
  pleasanterSiteId: number,
  shuffle: boolean,
  title: string,
): Promise<{ surveyId: string; publicId: string }> {
  const created = await request.post('/api/admin/surveys', {
    data: { title, pleasanterSiteId },
  });
  if (!created.ok()) {
    throw new Error(`アンケートを作れなかった: ${created.status()} ${await created.text()}`);
  }

  const { surveyId, publicId } = (await created.json()) as {
    surveyId: string;
    publicId: string;
  };

  const definition = {
    surveyId,
    version: 1,
    title: { ja: title },
    confirmationMessage: { ja: 'ありがとうございました。' },
    displayMode: 'Paged',
    allowEditingAfterSubmit: true,
    pages: [
      {
        pageId: 'page-1',
        title: { ja: '選択肢の並び' },
        questions: [
          {
            questionId: 'q-color',
            type: 'Radio',
            title: { ja: '好きな色' },
            // **必須にしない。** 答えずに次のページへ進みたい
            isRequired: false,
            choices: [
              ...choiceLabels.map((label, index) => ({
                value: `c${index + 1}`,
                label: { ja: label },
              })),
              // **「その他」は並べ替えの対象から外れ、必ず末尾に残る**
              { value: 'other', label: { ja: 'その他' }, isOther: true },
            ],
            settings: { shuffleChoices: shuffle },
          },
        ],
      },
      {
        pageId: 'page-2',
        title: { ja: '設問の並び' },
        // ⚠️ **出し分けの条件を持つ設問は置かない。** 置くと公開の時点で弾かれる
        shuffleQuestions: shuffle,
        questions: [
          {
            questionId: 'q-note',
            type: 'Note',
            // **説明文ブロックは動かないこと**を見るための目印
            title: { ja: 'はじめにお読みください' },
            isRequired: false,
            choices: [],
            description: { ja: '以下の設問についてお答えください。' },
            settings: {},
          },
          ...questionTitles.map((questionTitle, index) => ({
            questionId: `q-${index + 1}`,
            type: 'Text',
            title: { ja: questionTitle },
            isRequired: false,
            choices: [],
            settings: { maxLength: 100 },
          })),
        ],
      },
    ],
  };

  const saved = await request.put(`/api/admin/surveys/${surveyId}`, {
    data: { revision: 0, definition, mapping: { assignments: [] } },
  });
  if (!saved.ok()) {
    throw new Error(`下書きを保存できなかった: ${saved.status()} ${await saved.text()}`);
  }

  const published = await request.post(`/api/admin/surveys/${surveyId}/publish`, { data: {} });
  if (!published.ok()) {
    throw new Error(`公開できなかった: ${published.status()} ${await published.text()}`);
  }

  return { surveyId, publicId };
}

/** 選択肢の名前。**元の並びはこの通り。** */
export const choiceLabels = [
  '選択肢01',
  '選択肢02',
  '選択肢03',
  '選択肢04',
  '選択肢05',
  '選択肢06',
  '選択肢07',
  '選択肢08',
];

/** 動かせる設問の名前。**元の並びはこの通り。** */
export const questionTitles = ['設問A', '設問B', '設問C', '設問D', '設問E'];

/** 説明文ブロックの見出し。**並べ替えても先頭に残るはずのもの。** */
export const noteHeading = 'はじめにお読みください';
