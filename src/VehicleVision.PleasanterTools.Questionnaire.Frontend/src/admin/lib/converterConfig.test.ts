import { describe, expect, it } from 'vitest';
import {
  configWithMapRows,
  converterForOperation,
  converterConfigFields,
  mapRowsFromConfig,
  setConfigValue,
} from './converterConfig';

describe('変換の固定設定', () => {
  it('変換を切り替えても保存済みの設定を保持する', () => {
    expect(
      converterForOperation('contains', {
        operation: 'script',
        config: { script: 'return value;', future: 'keep' },
      }),
    ).toEqual({
      operation: 'contains',
      config: { script: 'return value;', future: 'keep' },
    });
    expect(converterForOperation('', { operation: 'join', config: { separator: '、' } })).toBeNull();
  });

  it('mapとtoNumberを選んだ時点で空の既定値を設定する', () => {
    expect(converterForOperation('map', null)).toEqual({
      operation: 'map',
      config: { default: '' },
    });
    expect(converterForOperation('toNumber', null)).toEqual({
      operation: 'toNumber',
      config: { default: '' },
    });
  });

  it('設定値を書き換えても保存済みの別の鍵を保持する', () => {
    expect(setConfigValue({ script: 'return value;', future: 'keep' }, 'script', 'return 1;')).toEqual(
      {
        script: 'return 1;',
        future: 'keep',
      },
    );
  });

  it('script だけを複数行入力として定義する', () => {
    expect(converterConfigFields.script).toEqual([
      expect.objectContaining({ key: 'script', multiline: true }),
    ]);
    expect(
      Object.entries(converterConfigFields)
        .filter(([, fields]) => fields.some((field) => field.multiline))
        .map(([operation]) => operation),
    ).toEqual(['script']);
  });

  it('すべての変換の設定欄を一つの表で定義する', () => {
    expect(Object.keys(converterConfigFields)).toEqual([
      'join',
      'map',
      'toNumber',
      'toCheck',
      'contains',
      'constant',
      'coalesce',
      'when',
      'script',
      'identity',
    ]);
    expect(converterConfigFields.when?.map((field) => field.key)).toEqual([
      'when',
      'then',
      'else',
    ]);
    expect(converterConfigFields.map?.map((field) => field.key)).toEqual(['default']);
    expect(converterConfigFields.toNumber?.map((field) => field.key)).toEqual([
      'default',
      'decimals',
    ]);
  });
});

describe('map の設定', () => {
  it('保存済みの map の鍵を行として読み出す', () => {
    expect(mapRowsFromConfig({ 'map.未処理': '10', 'map.完了': '90', future: 'keep' })).toEqual([
      { source: '未処理', target: '10' },
      { source: '完了', target: '90' },
    ]);
  });

  it('行を追加・削除しても map 以外の設定を保持する', () => {
    const config = configWithMapRows(
      { 'map.削除する値': 'old', future: 'keep' },
      [
        { source: '継続', target: '1' },
        { source: '追加', target: '2' },
      ],
    );

    expect(config).toEqual({ future: 'keep', 'map.継続': '1', 'map.追加': '2' });
  });

  it('元の値を変えたら古い鍵を消し空の元の値は保存しない', () => {
    const config = configWithMapRows(
      { 'map.変更前': '値', 'map.': '不正' },
      [
        { source: '変更後', target: '値' },
        { source: '   ', target: '保存しない' },
      ],
    );

    expect(config).toEqual({ 'map.変更後': '値' });
  });
});
