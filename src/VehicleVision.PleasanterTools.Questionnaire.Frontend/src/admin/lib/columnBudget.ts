import type { MappingDefinition, Question, SurveyDefinition } from './types';
import { hasRowPorts, isDisplayOnly, rowPorts } from './types';

/**
 * Pleasanter の列がいくつ要るかを数える。
 *
 * ⚠️ **サーバ側（`Core/Mapping/ColumnBudget.cs`）が正。**
 * ここはその写しで、**保存や公開を待たずに編集画面へ出すためだけ**にある。
 * **片方だけ直さないこと。** 食い違うと、画面では収まっているのに公開で弾かれる。
 *
 * **列は型ごとに 26 本しかない**（`A`〜`Z`。`_documents/実機検証結果.md`）。
 * 項目拡張で増やせるが、既定はこれ。
 *
 * ⚠️ **英字と数字の両方を落とさないこと。** 落とすと `Class012` が `Clas` になり、
 * `ClassA` と別の型として数えられて、型ごとの上限がすり抜ける。
 */
export const STANDARD_COLUMNS_PER_TYPE = 26;

/**
 * 列名から型の接頭辞を取り出す。
 *
 * `ClassA` → `Class`、`Class012` → `Class`、`Class` → `Class`。
 *
 * **Pleasanter の列名は「接頭辞 ＋ `A`〜`Z` 1 文字」か「接頭辞 ＋ 数字」の 2 通りだけ**
 * （`_documents/実機検証結果.md`）。**どちらでもなければ、そのまま返す。**
 */
export function prefixOf(columnName: string): string {
  const trimmed = columnName.trim();
  if (trimmed === '') return '';

  // 項目拡張で増えた列（Class001 など）
  const withoutDigits = trimmed.replace(/[0-9]+$/, '');
  if (withoutDigits !== trimmed) {
    return withoutDigits === '' ? trimmed : withoutDigits;
  }

  // 標準の列（ClassA 〜 ClassZ）。**大文字 1 文字だけ**
  const withoutLetter = trimmed.replace(/[A-Z]$/, '');
  return withoutLetter === '' ? trimmed : withoutLetter;
}

/** ある型の列を、いくつ使っていて、いくつ残っているか。 */
export interface ColumnUsage {
  prefix: string;
  used: number;
  available: number;
  remaining: number;
  /**
   * 標準の本数に収まっているか。
   *
   * ⚠️ **超えていても公開は通る。** 実際に使える本数は Pleasanter サイト側の
   * 設定（項目拡張）で決まり、こちらからは分からない。
   * **超えたことを知らせるだけで、止めはしない。**
   */
  fits: boolean;
}

/** 今の割り当てで、型ごとに何本使っているか。 */
export function measure(
  mapping: MappingDefinition,
  availablePerType: number = STANDARD_COLUMNS_PER_TYPE,
): ColumnUsage[] {
  const byPrefix = new Map<string, Set<string>>();

  for (const assignment of mapping.assignments) {
    const column = assignment.targetColumn?.trim() ?? '';
    if (column === '') continue;

    const prefix = prefixOf(column);
    const columns = byPrefix.get(prefix) ?? new Set<string>();
    // **同じ列を 2 回書いても 1 本。** 重複は別の警告で拾う
    columns.add(column.toLowerCase());
    byPrefix.set(prefix, columns);
  }

  return [...byPrefix.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([prefix, columns]) => ({
      prefix,
      used: columns.size,
      available: availablePerType,
      remaining: availablePerType - columns.size,
      fits: columns.size <= availablePerType,
    }));
}

/**
 * この定義を「行ごとに 1 列」で写すと、いくつ入力が要るか。
 *
 * **見積もりであって、実際の消費ではない。** まとめて 1 列へ入れる人もいる。
 * **「このままだと何本要るか」を作る前に見せるための数**（Issue #74）。
 */
export function requiredPortCount(definition: SurveyDefinition): number {
  return definition.pages
    .flatMap((page) => page.questions)
    .filter((question) => !isDisplayOnly(question.type))
    .reduce((total, question) => total + portsOf(question), 0);
}

function portsOf(question: Question): number {
  if (!hasRowPorts(question.type)) return 1;

  // **行が 0 本でも 1 とみなす。** これから足すものとして数える
  return Math.max(rowPorts(question).length, 1);
}
