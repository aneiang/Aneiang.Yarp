{{/*
Chart name and fullname. `fullname` is what every object is named after, so it must stay stable
across upgrades — renaming it orphans the PVC and, with it, every route the cluster was serving.
*/}}
{{- define "aneiang-yarp.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "aneiang-yarp.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "aneiang-yarp.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "aneiang-yarp.labels" -}}
helm.sh/chart: {{ include "aneiang-yarp.chart" . }}
{{ include "aneiang-yarp.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "aneiang-yarp.selectorLabels" -}}
app.kubernetes.io/name: {{ include "aneiang-yarp.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "aneiang-yarp.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "aneiang-yarp.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/*
Name of the Secret holding the credentials. Either the user's or ours.
*/}}
{{- define "aneiang-yarp.secretName" -}}
{{- if .Values.auth.existingSecret -}}
{{- .Values.auth.existingSecret -}}
{{- else -}}
{{- printf "%s-auth" (include "aneiang-yarp.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/*
The password actually used, always base64-encoded so the Secret can take it verbatim in `data:`.
Generation happens here rather than in values.yaml so a fresh install never starts with a password
that is written down in a public chart. `lookup` keeps it stable across upgrades — regenerating on
every `helm upgrade` would silently log everyone out.
*/}}
{{- define "aneiang-yarp.jwtPassword" -}}
{{- if .Values.auth.jwtPassword -}}
{{- .Values.auth.jwtPassword | b64enc -}}
{{- else -}}
{{- $secretName := printf "%s-auth" (include "aneiang-yarp.fullname" .) -}}
{{- $existing := lookup "v1" "Secret" .Release.Namespace $secretName -}}
{{- if and $existing (hasKey $existing.data "jwt-password") -}}
{{- $existing.data.jwt-password -}}
{{- else -}}
{{- randAlphaNum 24 | b64enc -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "aneiang-yarp.jwtSecret" -}}
{{- if .Values.auth.jwtSecret -}}
{{- .Values.auth.jwtSecret -}}
{{- else -}}
{{- $secretName := printf "%s-auth" (include "aneiang-yarp.fullname" .) -}}
{{- $existing := lookup "v1" "Secret" .Release.Namespace $secretName -}}
{{- if and $existing (hasKey $existing.data "jwt-secret") -}}
{{- $existing.data.jwt-secret -}}
{{- else -}}
{{- randAlphaNum 48 | b64enc -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "aneiang-yarp.pvcName" -}}
{{- if .Values.persistence.existingClaim -}}
{{- .Values.persistence.existingClaim -}}
{{- else -}}
{{- printf "%s-data" (include "aneiang-yarp.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/*
Refuse to render a configuration that cannot work, instead of letting it half-work in production.
Two replicas over a per-pod SQLite file means two divergent control planes and a dashboard that
shows a different answer depending on which pod the load balancer picked.
*/}}
{{- define "aneiang-yarp.validate" -}}
{{- if and (gt (int .Values.replicaCount) 1) (not .Values.allowMultipleReplicas) -}}
{{- fail "replicaCount > 1 requires allowMultipleReplicas=true: the config store is a single SQLite file, so each replica would keep its own copy of the routes (see ADR-001). Set allowMultipleReplicas=true only if the store is a shared ReadWriteMany volume and last-write-wins between control planes is acceptable." -}}
{{- end -}}
{{- if and (gt (int .Values.replicaCount) 1) (eq .Values.persistence.accessMode "ReadWriteOnce") -}}
{{- fail "replicaCount > 1 with accessMode ReadWriteOnce cannot share the store: the second pod will fail to mount. Use a ReadWriteMany volume or a single replica." -}}
{{- end -}}
{{- if and .Values.ingress.enabled (not .Values.ingress.hosts) -}}
{{- fail "ingress.enabled is true but ingress.hosts is empty." -}}
{{- end -}}
{{- end -}}
