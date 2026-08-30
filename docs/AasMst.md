# Azure Artifact Signing MST Proxy Plugin

Registers COSE Sign1 messages with Microsoft's Signing Transparency (MST) through the Azure Artifact
Signing (AAS) proxy, instead of calling an MST ledger directly.

## Overview

The [MST plugin](MST.md) talks to an MST ledger directly, which means the caller needs a credential
that the ledger recognizes. This plugin takes the other path: it posts the COSE Sign1 message to an
AAS proxy route, presenting an AAS token together with an account name and a certificate profile
name. AAS then authorizes the registration using **the same policy that governs signing** with that
account and profile, and forwards the statement to MST on the caller's behalf.

The practical benefit is that signing and transparency registration share one credential and one
authorization model:

```bash
# 1. Sign with Azure Artifact Signing
CoseSignTool sign --payload sample_payload.txt --sig sample_payload.cose \
  --cp azure-artifact-signing \
  --aas-endpoint https://wus.codesigning.azure.net \
  --aas-account-name testwus \
  --aas-cert-profile-name testWusCert1

# 2. Register the signature with MST using the same account and profile
CoseSignTool aas_mst_register \
  --endpoint https://wus.codesigning.azure.net \
  --account-name testwus \
  --cert-profile-name testWusCert1 \
  --payload sample_payload.txt \
  --signature sample_payload.cose \
  --transparent-statement sample_payload.transparent.cose
```

## Commands

### aas_mst_register

Registers a COSE Sign1 message with MST through the AAS proxy.

#### Required arguments

| Option | Description |
|--------|-------------|
| `--endpoint` | The AAS service endpoint URL, for example `https://wus.codesigning.azure.net`. |
| `--account-name` | The AAS account name used for authorization. |
| `--cert-profile-name` | The AAS certificate profile name used for authorization. |
| `--payload` | The file path to the payload that was signed. |
| `--signature` | The file path to the COSE Sign1 signature file to register. |

#### Optional arguments

| Option | Default | Description |
|--------|---------|-------------|
| `--output` | none | File path for a JSON summary of the registration. |
| `--transparent-statement` | none | File path for the transparent statement returned by MST. |
| `--register-path` | `mstregister` | The proxy register path. Supports the `{account}` and `{profile}` placeholders. |
| `--param-location` | `body` | Where the account and profile names travel: `body`, `header`, or `path`. |
| `--api-version` | none | Appended as an `api-version` query parameter. |
| `--scope` | `https://codesigning.azure.net/.default` | The OAuth 2.0 scope requested for the AAS token. |
| `--token-env` | `AAS_MST_TOKEN` | Environment variable holding a pre-acquired access token. |
| `--timeout` | `30` | Timeout in seconds for the whole operation, including polling. |

## Authentication

Authentication follows the same model as the `azure-artifact-signing` certificate provider, so no
raw secret is ever accepted on the command line.

1. If the environment variable named by `--token-env` is set, its value is used as the bearer token.
   When `--token-env` is passed explicitly and the variable is missing or empty, the command fails
   rather than silently falling back.
2. Otherwise `DefaultAzureCredential` acquires a token for `--scope`. Interactive browser
   authentication is excluded so unattended pipelines fail fast instead of hanging on a prompt.

```bash
# Local development
az login
CoseSignTool aas_mst_register --endpoint https://wus.codesigning.azure.net \
  --account-name testwus --cert-profile-name testWusCert1 \
  --payload sample_payload.txt --signature sample_payload.cose

# CI/CD with a service principal
export AZURE_TENANT_ID=...
export AZURE_CLIENT_ID=...
export AZURE_CLIENT_SECRET=...
CoseSignTool aas_mst_register --endpoint https://wus.codesigning.azure.net \
  --account-name testwus --cert-profile-name testWusCert1 \
  --payload sample_payload.txt --signature sample_payload.cose
```

## Configuring the proxy route

> **Note:** The AAS MST proxy contract is not yet finalized, and the route is not yet deployed to
> every AAS region. The route and the placement of the account and profile names are therefore
> configurable so the CLI can be pointed at the contract without a code change.

`--register-path` is appended to `--endpoint`, and the `{account}` and `{profile}` placeholders are
replaced with URL-escaped values. `--param-location` selects how those values are transmitted:

| `--param-location` | Request body | Account and profile carried in |
|--------------------|--------------|-------------------------------|
| `body` (default) | `application/json` with `accountName`, `certificateProfileName`, and a base64 `signature` | The JSON body |
| `header` | raw `application/cose` | `x-ms-codesigning-account-name` and `x-ms-codesigning-certificate-profile-name` headers |
| `path` | raw `application/cose` | The `{account}` and `{profile}` placeholders in `--register-path` |

Example targeting an account-scoped route that mirrors the existing AAS `sign` route shape:

```bash
CoseSignTool aas_mst_register \
  --endpoint https://wus.codesigning.azure.net \
  --account-name testwus \
  --cert-profile-name testWusCert1 \
  --payload sample_payload.txt \
  --signature sample_payload.cose \
  --param-location path \
  --register-path 'codesigningaccounts/{account}/certificateprofiles/{profile}/mstregister' \
  --api-version 2023-06-15-preview
```

## Responses

The plugin handles both a synchronous and an asynchronous service:

- A `2xx` response whose content type is `application/cose` or `application/cbor` is treated as the
  transparent statement and written to `--transparent-statement`.
- A `202 Accepted` response is followed to the URI in its `Operation-Location`, `Azure-AsyncOperation`,
  or `Location` header, honouring `Retry-After`, until the operation reaches a terminal state. A JSON
  operation document reporting a `running`, `pending`, `notStarted`, `inProgress`, or `accepted`
  status is also treated as non-terminal.
- Any other status is reported as a failure, with the response body included in the error output.

The whole sequence, including token acquisition, shares the single deadline set by `--timeout`.

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Registration succeeded. |
| 2 | A required option was missing. |
| 4 | An option value was invalid, or the signature file was not a COSE Sign1 message. |
| 6 | The payload or signature file was not found. |
| 10 | The service returned an error, the request failed, or the operation timed out. |

## Relationship to the MST plugin

| | `mst_register` ([MST plugin](MST.md)) | `aas_mst_register` (this plugin) |
|---|---|---|
| Endpoint | An MST ledger | An AAS proxy route |
| Credential | MST token, `DefaultAzureCredential`, or anonymous | AAS token, same as signing |
| Authorization | MST ledger policy | AAS account and certificate profile policy |
| Verification | `mst_verify` | Use `mst_verify` against the ledger |

Both plugins produce the same artifact — a transparent statement — so `mst_verify` is used to verify
the result of either.
