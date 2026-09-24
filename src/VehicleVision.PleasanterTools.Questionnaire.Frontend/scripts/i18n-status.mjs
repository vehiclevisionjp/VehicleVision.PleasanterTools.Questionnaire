import { de as adminDe } from '../src/admin/lib/i18n/messages/de.ts';
import { en as adminEn } from '../src/admin/lib/i18n/messages/en.ts';
import { es as adminEs } from '../src/admin/lib/i18n/messages/es.ts';
import { ja as adminJa } from '../src/admin/lib/i18n/messages/ja.ts';
import { ko as adminKo } from '../src/admin/lib/i18n/messages/ko.ts';
import { vi as adminVi } from '../src/admin/lib/i18n/messages/vi.ts';
import { zh as adminZh } from '../src/admin/lib/i18n/messages/zh.ts';
import { de as responseDe } from '../src/lib/i18n/messages/de.ts';
import { en as responseEn } from '../src/lib/i18n/messages/en.ts';
import { es as responseEs } from '../src/lib/i18n/messages/es.ts';
import { ja as responseJa } from '../src/lib/i18n/messages/ja.ts';
import { ko as responseKo } from '../src/lib/i18n/messages/ko.ts';
import { vi as responseVi } from '../src/lib/i18n/messages/vi.ts';
import { zh as responseZh } from '../src/lib/i18n/messages/zh.ts';

const languages = ['ja', 'en', 'zh', 'de', 'ko', 'es', 'vi'];
const bundles = [
  {
    name: 'Response screen',
    catalogs: {
      ja: responseJa,
      en: responseEn,
      zh: responseZh,
      de: responseDe,
      ko: responseKo,
      es: responseEs,
      vi: responseVi,
    },
  },
  {
    name: 'Administration screen',
    catalogs: {
      ja: adminJa,
      en: adminEn,
      zh: adminZh,
      de: adminDe,
      ko: adminKo,
      es: adminEs,
      vi: adminVi,
    },
  },
];

function count(catalog) {
  return Object.keys(catalog).length;
}

function printStatus(name, translatedByLanguage, total) {
  console.log(name);
  for (const language of languages) {
    console.log(`  ${language}: ${translatedByLanguage[language]} / ${total}`);
  }
}

for (const bundle of bundles) {
  const translated = Object.fromEntries(
    languages.map((language) => [language, count(bundle.catalogs[language])]),
  );
  printStatus(bundle.name, translated, count(bundle.catalogs.ja));
}

const totals = Object.fromEntries(
  languages.map((language) => [
    language,
    bundles.reduce((sum, bundle) => sum + count(bundle.catalogs[language]), 0),
  ]),
);
const totalMessages = bundles.reduce((sum, bundle) => sum + count(bundle.catalogs.ja), 0);

printStatus('Total', totals, totalMessages);
