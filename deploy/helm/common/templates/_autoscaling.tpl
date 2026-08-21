{{- /*
Not on the canary release, and for a reason beyond the shared object name: the
served weight IS the replica ratio, so an autoscaler on either track moves the
blast radius underneath the analysis that is judging it.

**The rollout ALSO passes `--set autoscaling.enabled=false`, and that flag is
load-bearing rather than belt and braces** — this comment said the second and
taught the opposite of what the flag does. Suppressing the HPA object is only
half of what `autoscaling.enabled` controls: `_deployment.tpl` omits
`replicas` entirely whenever it is true, deliberately, so the autoscaler owns
the field. The canary installs with `-f` from the STABLE release's values,
where the HPA is on — so without the flag the canary Deployment carries no
replica count at all and the API server defaults it to **one**. Every rung of
the ladder would then be a single pod, and `--set replicaCount=6` at 50% would
be a no-op that reports success.

`smoke.sh` asserts the rendered canary carries `replicas:` for that reason;
removing the flag "because the template already suppresses the HPA" is the
change that assertion exists to stop.
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

**What it does NOT protect is the canary's weight**, and that is worth saying
where the selector is. Matching both tracks means it constrains the TOTAL, so
during §15.5's ladder a voluntary disruption can evict stable pods and leave
the canary serving more than its rung asked for — the ceiling `canary.py plan`
enforces is an arithmetic one, not one Kubernetes maintains. ADR-022 records
the residual, the reason a temporary stable-track budget is deferred, and why
the verdict is unaffected: `analyse` reads what each track actually did.
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
