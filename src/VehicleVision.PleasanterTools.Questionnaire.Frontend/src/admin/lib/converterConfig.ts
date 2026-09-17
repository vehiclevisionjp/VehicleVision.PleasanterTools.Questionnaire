import type { MessageKey } from './i18n/messages';
import type { MappingConverter } from './types';

export interface ConverterConfigField {
  key: string;
  label: MessageKey;
  placeholder?: MessageKey;
  defaultValue?: string;
  multiline?: boolean;
}

export interface MapConfigRow {
  source: string;
  target: string;
}

/** 変換ごとの固定設定欄。動的な組を持つ map だけは専用の行エディタで扱う。 */
export const converterConfigFields: Readonly<Record<string, readonly ConverterConfigField[]>> = {
  join: [
    {
      key: 'separator',
      label: 'mapping.config.separator',
      placeholder: 'mapping.config.separatorPlaceholder',
      defaultValue: ',',
    },
  ],
  map: [{ key: 'default', label: 'mapping.config.mapDefault' }],
  toNumber: [
    { key: 'default', label: 'mapping.config.numberDefault' },
    { key: 'decimals', label: 'mapping.config.numberDecimals' },
  ],
  toCheck: [{ key: 'value', label: 'mapping.config.checkValue' }],
  contains: [{ key: 'keyword', label: 'mapping.config.keyword' }],
  constant: [{ key: 'value', label: 'mapping.config.constantValue' }],
  coalesce: [],
  when: [
    { key: 'when', label: 'mapping.config.whenValue' },
    { key: 'then', label: 'mapping.config.thenValue' },
    { key: 'else', label: 'mapping.config.elseValue' },
  ],
  script: [{ key: 'script', label: 'mapping.config.script', multiline: true }],
  identity: [],
};

export function setConfigValue(
  config: Readonly<Record<string, string>>,
  key: string,
  value: string,
): Record<string, string> {
  return { ...config, [key]: value };
}

/** 変換を切り替えても、画面に出していない保存済み設定は捨てない。 */
export function converterForOperation(
  operation: string,
  current: MappingConverter | null | undefined,
): MappingConverter | null {
  if (operation === '') return null;

  const config = current?.config ?? {};
  return {
    operation,
    config:
      (operation === 'map' || operation === 'toNumber') && !('default' in config)
        ? { ...config, default: '' }
        : config,
  };
}

export function mapRowsFromConfig(config: Readonly<Record<string, string>>): MapConfigRow[] {
  return Object.entries(config)
    .filter(([key]) => key.startsWith('map.') && key.length > 'map.'.length)
    .map(([key, target]) => ({ source: key.slice('map.'.length), target }));
}

/**
 * map の画面上の行を設定へ戻す。
 *
 * map 以外の未知の鍵は保存済みデータを壊さないため保持し、map の鍵だけを行から作り直す。
 */
export function configWithMapRows(
  config: Readonly<Record<string, string>>,
  rows: readonly MapConfigRow[],
): Record<string, string> {
  const next = Object.fromEntries(
    Object.entries(config).filter(([key]) => !key.startsWith('map.')),
  );

  for (const row of rows) {
    if (row.source.trim() !== '') {
      next[`map.${row.source}`] = row.target;
    }
  }

  return next;
}
