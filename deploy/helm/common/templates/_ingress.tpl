{{- /*
The platform's one route in from outside, and only the gateway has it (§10.1).

`ingress.enabled` carries two meanings at once and that is deliberate rather
than an overload to untangle: it says a Kubernetes Ingress object exists, and
it says the host behind it is behind a proxy — which is what `Ingress__Enabled`
tells the gateway's forwarded-headers block (§15.4). The two are the same fact
about topology, which is why §14.1's Compose sets the same key false while the
gateway there IS the edge.

TLS terminates here (§10.1), so this object owns the certificate and every hop
past it is plain http — which is the premise PricingHop.cs states for using
`http://` on the BFF's one synchronous hop.
*/}}
{{- define "commerce.ingress" -}}
{{- if .Values.ingress.enabled -}}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ include "commerce.name" . }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
  {{- with .Values.ingress.annotations }}
  annotations:
    {{- toYaml . | nindent 4 }}
  {{- end }}
spec:
  ingressClassName: {{ required "ingress.className is required when ingress.enabled: an Ingress with no class is picked up by whichever controller claims the default, which is not a deployment decision to leave to a cluster." .Values.ingress.className | quote }}
  {{- $host := required "ingress.host is required when ingress.enabled." .Values.ingress.host }}
  {{- with .Values.ingress.tls }}
  tls:
    - hosts: [{{ $host | quote }}]
      secretName: {{ required "ingress.tls.secretName is required when ingress.tls is set." .secretName | quote }}
  {{- end }}
  rules:
    - host: {{ $host | quote }}
      http:
        paths:
          {{- /*
          One rule to the whole gateway, not a rule per service. §10.2's route
          file is the platform's routing table and it lives in the gateway's
          appsettings.json; splitting it across Ingress paths would create a
          second one, and the two would disagree the first time a route moved.
          */}}
          - path: /
            pathType: Prefix
            backend:
              service:
                name: {{ include "commerce.name" . }}
                port:
                  name: http
{{- end -}}
{{- end -}}
