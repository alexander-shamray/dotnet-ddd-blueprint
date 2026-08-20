{{- /*
Not on the canary release, and for a reason beyond the shared object name: the
served weight IS the replica ratio, so an autoscaler on either track moves the
blast radius underneath the analysis that is judging it. The rollout sets
`autoscaling.enabled=false` on the canary explicitly as well — belt and braces,
because a canary that scaled itself would be a step whose weight nobody chose.
*/}}
{{- define "commerce.hpa" -}}
{{- if and .Values.autoscaling.enabled (not .Values.canary.enabled) -}}
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

{{- /*
Also stable-only, and here the shared NAME is the whole reason. The budget's
selector is `commerce.selectorLabels`, which matches both tracks — so the one
the stable release owns already protects the canary's pods, and that is
correct: they serve the same Service and a drain that took them all is the same
outage either way.
*/}}
{{- define "commerce.pdb" -}}
{{- if and .Values.podDisruptionBudget.enabled (not .Values.canary.enabled) -}}
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
