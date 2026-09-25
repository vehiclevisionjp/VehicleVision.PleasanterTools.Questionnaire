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

{{/* サブパス。空と "/" は根として扱い、空文字を返す。 */}}
{{- define "questionnaire.pathBase" -}}
{{- $pathBase := .Values.config.pathBase | default "" }}
{{- if ne $pathBase "/" }}{{ $pathBase }}{{ end }}
{{- end }}

{{/* Ingress のパス。未指定なら サブパス、それも無ければ "/"。パスの区切りまで一致させ、サブパスの外を指していたら止める。 */}}
{{- define "questionnaire.ingressPath" -}}
{{- $pathBase := include "questionnaire.pathBase" . }}
{{- $path := .Values.ingress.path | default (default "/" $pathBase) }}
{{- if and $pathBase (not (or (eq $pathBase $path) (hasPrefix (printf "%s/" $pathBase) $path))) }}
{{- fail (printf "ingress.path (%s) must start with config.pathBase (%s). The prefix must not be stripped." $path $pathBase) }}
{{- end }}
{{- $path }}
{{- end }}
