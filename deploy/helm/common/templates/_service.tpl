{{- /*
A ClusterIP Service, rendered only where something dials this workload by name.

`service.enabled: false` is what Shipping and Notifications will set (§15.3):
they consume from the broker and expose no API, so their only listener is the
health endpoint §13.5 requires — and the probes reach a container port
directly, without a Service in front of it. Telemetry is pushed to the
collector rather than scraped (§13.2), so nothing else wants a stable name for
those pods either.

The key is `false` rather than absent on purpose, and the callout in §15.3 is
about this exact line: a worker's safety comes from having no route, so the
absence of a route is the thing to assert. A missing key looks the same whether
it was considered or forgotten.
*/}}
{{- define "commerce.service" -}}
{{- if .Values.service.enabled -}}
apiVersion: v1
kind: Service
metadata:
  name: {{ include "commerce.name" . }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
spec:
  type: ClusterIP
  selector:
    {{- include "commerce.selectorLabels" . | nindent 4 }}
  ports:
    {{- /*
    The Service port is the container port, not a remapping. Callers dial
    `http://catalog-api:8080/` (§10.2's route file) and `http://catalog-api:8081`
    (PricingHop), and both numbers are literals in source — so a Service that
    renumbered its front door would be a 502 nothing in this chart could see.
    */}}
    {{- range .Values.ports }}
    - name: {{ .name }}
      port: {{ .containerPort }}
      targetPort: {{ .name }}
      protocol: TCP
    {{- end }}
{{- end -}}
{{- end -}}
