import type { MessageKey } from './index';

export const es: Partial<Record<MessageKey, string>> = {
  'status.loading': 'Cargando…',
  'test.banner': 'Esta encuesta está publicada como prueba. Las respuestas enviadas se registrarán realmente en Pleasanter.',

  'rejected.notFound.title': 'No se encontró la encuesta',
  'rejected.closed.title': 'No se aceptan respuestas',
  'rejected.notStarted': 'Esta encuesta aún no está abierta para recibir respuestas.',
  'rejected.closed': 'Esta encuesta ya no acepta respuestas.',
  'rejected.suspended': 'Esta encuesta está suspendida actualmente.',
  'rejected.embeddingNotAllowed.title': 'No se puede responder a esta encuesta incrustada',
  'rejected.embeddingNotAllowed':
    'No se puede responder a esta encuesta incrustada. Ábrala en una ventana independiente.',
  'rejected.embeddingNotAllowed.open': 'Abrir en una ventana independiente',
  'rejected.notFound': 'No se pudo encontrar esta encuesta. Compruebe la URL.',
  'rejected.rejected': 'No se pudo aceptar su respuesta. Vuelva a abrir la página e inténtelo de nuevo.',
  'rejected.tooManyRequests': 'El servicio está ocupado. Espere un momento e inténtelo de nuevo.',
  'error.badUrl.title': 'La URL no es válida',
  'error.badUrl': 'Compruebe la URL de la encuesta.',

  'answered.title': 'Ya ha respondido en este dispositivo',
  'answered.canEdit': 'Puede editar su respuesta anterior.',
  'answered.cannotRead': 'No se pudo cargar su respuesta anterior.',
  'answered.editingNotAllowed': 'Esta encuesta no permite editar respuestas.',
  'answered.answerAgain': 'Enviar una nueva respuesta',
  'answered.answerAgainNote':
    'Al elegir «Enviar una nueva respuesta», se registrará por separado de su respuesta anterior.',

  'completed.title': 'Hemos recibido su respuesta',
  'completed.thanks': 'Gracias por su tiempo.',
  'completed.edit': 'Editar mi respuesta',
  'completed.answerAgain': 'Enviar otra respuesta',
  'assetHistory.notice':
    'Cuando recibe un archivo, el archivo y la hora se registran y se vinculan con su respuesta. No se registran su dirección IP ni la información de su dispositivo.',

  'form.progressLabel': 'Progreso',
  'form.pageCount': 'Página {current} de {total}',
  'form.back': 'Atrás',
  'form.next': 'Siguiente',
  'form.submit': 'Enviar',
  'form.submitting': 'Enviando…',
  'form.languageLabel': 'Idioma',
  'form.trapLabel': 'Deje este campo vacío',
  'readability.group': 'Legibilidad',
  'readability.fontSize': 'Texto',
  'readability.fontSize.standard': 'Estándar',
  'readability.fontSize.large': 'Grande',
  'readability.fontSize.extraLarge': 'Muy grande',
  'readability.colorMode': 'Colores',
  'readability.colorMode.creator': 'Tema del creador',
  'readability.colorMode.highContrast': 'Alto contraste',
  'readability.colorMode.dark': 'Oscuro',
  'analytics.notice': 'Esta página usa un servicio externo ({provider}) para medir el uso.',
  'captcha.notice': 'Esta página usa un servicio externo ({provider}) para bloquear envíos automatizados.',
  'captcha.required': 'Complete la verificación antes de enviar.',
  'draft.found': 'Hay una respuesta sin terminar guardada en este dispositivo.',
  'draft.foundNote':
    'Si comparte este dispositivo, compruebe el contenido antes de continuar. Elija Descartar para eliminarla.',
  'draft.restore': 'Reanudar',
  'draft.discard': 'Descartar',
  'draft.restored': 'Se ha cargado su respuesta sin terminar.',
  'draft.discarded': 'Se ha eliminado el borrador guardado en este dispositivo.',
};
