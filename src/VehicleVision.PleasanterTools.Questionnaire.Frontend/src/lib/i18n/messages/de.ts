import type { MessageKey } from './index';

export const de: Partial<Record<MessageKey, string>> = {
  'status.loading': 'Wird geladen…',
  'test.banner':
    'Diese Umfrage befindet sich in der Testveröffentlichung. Gesendete Antworten werden tatsächlich in Pleasanter gespeichert.',

  'rejected.notFound.title': 'Umfrage nicht gefunden',
  'rejected.closed.title': 'Keine Annahme von Antworten',
  'rejected.notStarted': 'Diese Umfrage ist noch nicht für Antworten geöffnet.',
  'rejected.closed': 'Diese Umfrage nimmt keine Antworten mehr an.',
  'rejected.suspended': 'Diese Umfrage ist derzeit pausiert.',
  'rejected.embeddingNotAllowed.title':
    'Diese Umfrage kann nicht eingebettet beantwortet werden',
  'rejected.embeddingNotAllowed':
    'Diese Umfrage kann nicht eingebettet beantwortet werden. Öffnen Sie sie in einem separaten Fenster.',
  'rejected.embeddingNotAllowed.open': 'In einem separaten Fenster öffnen',
  'rejected.notFound': 'Diese Umfrage konnte nicht gefunden werden. Bitte überprüfen Sie die URL.',
  'rejected.rejected':
    'Ihre Antwort konnte nicht angenommen werden. Öffnen Sie die Seite erneut und versuchen Sie es noch einmal.',
  'rejected.tooManyRequests': 'Der Dienst ist ausgelastet. Bitte warten Sie einen Moment und versuchen Sie es erneut.',
  'error.badUrl.title': 'Diese URL ist ungültig',
  'error.badUrl': 'Bitte überprüfen Sie die URL der Umfrage.',

  'answered.title': 'Sie haben auf diesem Gerät bereits geantwortet',
  'answered.canEdit': 'Sie können Ihre vorherige Antwort bearbeiten.',
  'answered.cannotRead': 'Ihre vorherige Antwort konnte nicht geladen werden.',
  'answered.editingNotAllowed': 'Diese Umfrage erlaubt keine Bearbeitung einer Antwort.',
  'answered.answerAgain': 'Eine neue Antwort senden',
  'answered.answerAgainNote':
    'Wenn Sie „Eine neue Antwort senden“ wählen, wird sie getrennt von Ihrer vorherigen Antwort gespeichert.',

  'completed.title': 'Ihre Antwort wurde empfangen',
  'completed.thanks': 'Vielen Dank für Ihre Zeit.',
  'completed.edit': 'Meine Antwort bearbeiten',
  'completed.answerAgain': 'Eine weitere Antwort senden',
  'assetHistory.notice':
    'Wenn Sie eine Datei erhalten, werden die Datei und die Uhrzeit aufgezeichnet und mit Ihrer Antwort verknüpft. Ihre IP-Adresse und Geräteinformationen werden nicht aufgezeichnet.',

  'form.progressLabel': 'Fortschritt',
  'form.pageCount': 'Seite {current} von {total}',
  'form.back': 'Zurück',
  'form.next': 'Weiter',
  'form.submit': 'Senden',
  'form.submitting': 'Wird gesendet…',
  'form.languageLabel': 'Sprache',
  'form.trapLabel': 'Dieses Feld leer lassen',
  'readability.group': 'Lesbarkeit',
  'readability.fontSize': 'Text',
  'readability.fontSize.standard': 'Standard',
  'readability.fontSize.large': 'Groß',
  'readability.fontSize.extraLarge': 'Sehr groß',
  'readability.colorMode': 'Farben',
  'readability.colorMode.creator': 'Thema des Erstellers',
  'readability.colorMode.highContrast': 'Hoher Kontrast',
  'readability.colorMode.dark': 'Dunkel',
  'analytics.notice':
    'Diese Seite verwendet einen externen Dienst ({provider}), um die Nutzung zu messen.',
  'captcha.notice':
    'Diese Seite verwendet einen externen Dienst ({provider}), um automatisierte Übermittlungen zu verhindern.',
  'captcha.required': 'Schließen Sie die Überprüfung vor dem Senden ab.',
  'draft.found': 'Eine unvollständige Antwort ist auf diesem Gerät gespeichert.',
  'draft.foundNote':
    'Wenn Sie dieses Gerät gemeinsam nutzen, überprüfen Sie den Inhalt, bevor Sie fortfahren. Wählen Sie „Verwerfen“, um ihn zu entfernen.',
  'draft.restore': 'Fortsetzen',
  'draft.discard': 'Verwerfen',
  'draft.restored': 'Ihre unvollständige Antwort wurde geladen.',
  'draft.discarded': 'Der auf diesem Gerät gespeicherte Entwurf wurde entfernt.',

  'question.required': 'erforderlich',
  'question.selectPlaceholder': 'Option auswählen',
  'question.otherText': 'Bitte angeben',
  'question.unsupported': 'Dieser Fragetyp wird noch nicht unterstützt ({type})',
  'question.ratingLabel': '{value} von {max}',
  'question.gridRowHeader': 'Element',
  'question.gridRowLabel': '{row}: {choice}',
  'question.rankingLead':
    'Wählen Sie Elemente aus, um sie zu bewerten. Wählen Sie erneut, um sie zu entfernen.',
  'embed.notice':
    'Diese Seite enthält Inhalte von externen Websites ({hosts}). Bei der Anzeige wird eine Verbindung zu diesen Websites hergestellt.',
  'embed.blocked': 'Dieser eingebettete Inhalt kann nicht angezeigt werden.',
  'question.rankingUnranked': 'Nicht bewertet',
  'question.rankingRank': 'Rang {rank}',
  'question.rankingUp': 'Nach oben verschieben',
  'question.rankingDown': 'Nach unten verschieben',
  'question.rankingClear': 'Rang entfernen',
  'question.rankingStatus': '{label}: Rang {rank} von {total}',
  'question.fileCountLimit': 'Bis zu {count} Dateien',
  'question.fileSizeLimitMegabytes': 'Bis zu {megabytes} MB pro Datei',
  'question.fileSizeLimitBytes': 'Bis zu {bytes} Bytes pro Datei',
  'question.selectionRange': 'Wählen Sie zwischen {minimum} und {maximum} Optionen',
  'question.selectionMinimum': 'Wählen Sie mindestens {minimum} Optionen',
  'question.selectionMaximum': 'Wählen Sie höchstens {maximum} Optionen',
  'question.normalizationNotice':
    'Nach der Eingabe werden Zeichenbreite und umgebende Leerzeichen gemäß der Konfiguration automatisch umgewandelt.',

  'submit.tooManyRequests':
    'Der Dienst ist ausgelastet. Bitte warten Sie einen Moment und senden Sie erneut.',
  'submit.rejected':
    'Ihre Antwort wurde nicht angenommen. Bitte drücken Sie erneut auf „Senden“.',
  'submit.insecureContext':
    'Bitte öffnen Sie diese Seite über eine sichere Verbindung (https). In diesem Zustand kann sie nicht gesendet werden.',
  'submit.failed':
    'Wir konnten Ihre Antwort nicht senden. Ihre Antworten sind noch vorhanden — bitte warten Sie einen Moment und versuchen Sie es erneut.',

  'attachment.defaultName': 'Anhang',
  'attachment.extensionNotAllowed': '{name}: Diese Art von Datei wird nicht angenommen',
  'attachment.contentDoesNotMatchExtension':
    '{name}: Der Inhalt stimmt nicht mit der Dateierweiterung überein',
  'attachment.tooLarge': '{name}: Die Datei ist zu groß',
  'attachment.totalTooLarge': 'Die Anhänge sind insgesamt zu groß',
  'attachment.tooMany': 'Zu viele Anhänge',
  'attachment.invalidFileName':
    '{name}: Der Dateiname enthält unzulässige Zeichen',
  'attachment.unknown': '{name}: Dieser Anhang kann nicht angenommen werden',

  'validation.required': 'Bitte beantworten Sie diese Frage',
  'validation.fileRequired': 'Bitte wählen Sie eine Datei aus',
  'validation.fileCount': 'Wählen Sie höchstens {count} Dateien',
  'validation.fileTooLarge': '{name}: Die Datei ist zu groß',
  'validation.singleValueOnly': 'Bitte wählen Sie nur eine Antwort',
  'validation.rowRequired': 'Bitte beantworten Sie jede Zeile',
  'validation.duplicateRank': 'Dasselbe Element hat mehr als einen Rang',
  'validation.tooFewSelections': 'Wählen Sie mindestens {minimum} Optionen',
  'validation.tooManySelections': 'Wählen Sie höchstens {maximum} Optionen',
  'validation.pattern': 'Geben Sie den Wert im angeforderten Format ein',
  'validation.tooLong': 'Verwenden Sie höchstens {max} Zeichen',
  'validation.email': 'Geben Sie eine gültige E-Mail-Adresse ein',
  'validation.url': 'Geben Sie eine URL ein, die mit http:// oder https:// beginnt',
  'validation.notANumber': 'Geben Sie eine Zahl ein',
  'validation.boolean': 'Der Kontrollkästchenwert ist ungültig',
  'validation.minimum': 'Geben Sie {minimum} oder mehr ein',
  'validation.maximum': 'Geben Sie {maximum} oder weniger ein',
  'validation.date': 'Bitte wählen Sie ein Datum',

  'serverValidation.Required': 'Bitte beantworten Sie diese Frage',
  'serverValidation.TooLong': 'Das ist zu lang',
  'serverValidation.UnknownChoice': 'Diese Option gehört nicht zu den Auswahlmöglichkeiten',
  'serverValidation.MultipleValuesNotAllowed': 'Bitte wählen Sie nur eine Antwort',
  'serverValidation.NotANumber': 'Geben Sie eine Zahl ein',
  'serverValidation.NotABoolean': 'Der Kontrollkästchenwert ist ungültig',
  'serverValidation.OutOfRange': 'Dieser Wert liegt außerhalb des zulässigen Bereichs',
  'serverValidation.NotADateTime': 'Dies ist kein gültiges Datum oder keine gültige Uhrzeit',
  'serverValidation.InvalidEmail': 'Geben Sie eine gültige E-Mail-Adresse ein',
  'serverValidation.InvalidUrl':
    'Geben Sie eine URL ein, die mit http:// oder https:// beginnt',
  'serverValidation.UnknownQuestion': 'Diese Frage kann nicht angenommen werden',
  'serverValidation.AnswerNotAllowed': 'Dieses Element kann nicht beantwortet werden',
  'serverValidation.OtherTextNotAllowed':
    'Freitext kann nur hinzugefügt werden, wenn „Andere“ ausgewählt ist',
  'serverValidation.TooLarge': 'Der gesendete Inhalt ist zu groß',
  'serverValidation.TooFewSelections': 'Sie haben nicht genügend Optionen gewählt',
  'serverValidation.TooManySelections': 'Sie haben zu viele Optionen gewählt',
  'serverValidation.PatternMismatch': 'Geben Sie den Wert im angeforderten Format ein',
  'serverValidation.ScannerUnavailable':
    'Anhänge können derzeit nicht geprüft werden, daher können wir dies noch nicht annehmen',
  'serverValidation.unknown': 'Bitte überprüfen Sie Ihre Eingabe',
};
