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
{{- /*
An Ingress whose backend does not exist renders, installs and reports success,
and answers every request with a 503 from the controller. `service.enabled` is
what creates that backend, so the two keys are not independent — and the
combination is exactly what a copied values file produces when someone turns a
worker's Service off and leaves the edge's Ingress on.
*/}}
{{- if not .Values.service.enabled }}
{{- fail "ingress.enabled requires service.enabled: the Ingress backend is this workload's Service, so with no Service the release installs cleanly and the controller answers 503 for every request (§15.3)." }}
{{- end }}
{{- /*
TLS terminates HERE (§10.1), and every argument downstream depends on it: the
gateway rewrites Request.Scheme from the ingress's header, ADR-020's
compression decision reads that scheme, and PricingHop.cs uses plain `http://`
on the one synchronous hop *because* the encrypted hop ended at this object.
An overlay setting `ingress.tls: null` renders a valid plaintext Ingress and
falsifies all three silently. Optional was the wrong shape.
*/}}
{{- if not .Values.ingress.tls }}
{{- fail "ingress.tls is required when ingress.enabled: TLS terminates at the Ingress (§10.1), and every hop past it is plain http on that premise. Rendering without it publishes the platform's only external route in cleartext." }}
{{- end }}
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
  {{- /* Unconditional: the guard above refuses to render without it. */}}
  tls:
    - hosts: [{{ $host | quote }}]
      secretName: {{ required "ingress.tls.secretName is required when ingress.enabled." .Values.ingress.tls.secretName | quote }}
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
