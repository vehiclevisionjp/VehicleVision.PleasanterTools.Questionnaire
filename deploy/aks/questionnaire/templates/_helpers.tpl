{{- define "questionnaire.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- define "questionnaire.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name (include "questionnaire.name" .) | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}

{{- define "questionnaire.labels" -}}
app.kubernetes.io/name: {{ include "questionnaire.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
{{- end }}

{{- define "questionnaire.selectorLabels" -}}
app.kubernetes.io/name: {{ include "questionnaire.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}
