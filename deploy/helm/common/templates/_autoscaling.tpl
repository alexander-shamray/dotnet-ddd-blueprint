{{- define "commerce.hpa" -}}
{{- if .Values.autoscaling.enabled -}}
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: {{ include "commerce.name" . }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: {{ include "commerce.name" . }}
  minReplicas: {{ .Values.autoscaling.minReplicas }}
  maxReplicas: {{ .Values.autoscaling.maxReplicas }}
  metrics:
    {{- /*
    CPU utilisation is a percentage of the REQUEST, which is why §15.3's
    resources block sets one and sets no CPU limit: the request is what the
    scheduler reserves and what this ratio is taken against, and the missing
    limit is what keeps a busy pod from being throttled below the utilisation
    that would have scaled it out.
    */}}
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: {{ .Values.autoscaling.targetCPUUtilizationPercentage }}
{{- end -}}
{{- end -}}

{{- define "commerce.pdb" -}}
{{- if .Values.podDisruptionBudget.enabled -}}
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: {{ include "commerce.name" . }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
spec:
  minAvailable: {{ .Values.podDisruptionBudget.minAvailable }}
  selector:
    matchLabels:
      {{- include "commerce.selectorLabels" . | nindent 6 }}
{{- end -}}
{{- end -}}
