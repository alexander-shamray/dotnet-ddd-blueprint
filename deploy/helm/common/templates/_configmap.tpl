{{- define "commerce.configmap" -}}
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ include "commerce.name" . }}-config
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
data:
  {{- include "commerce.config" . | nindent 2 }}
{{- end -}}
