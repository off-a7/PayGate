# Kashier fixtures

These are real traffic from Kashier's **test** environment, recorded on 2026-11-22 and 2026-11-23 with the PayGate playground: a separate app that calls Kashier directly and stores every request, redirect and webhook. What they prove is written up in [docs/providers/kashier.md](../../../docs/providers/kashier.md).

## They are sanitized and re-signed
Before a capture was copied here, the playground's fixture exporter made two passes.

First, it replaced the merchant's identity and anything personal:
- the merchant id, which becomes `MID-0000-000`
- the merchant number Kashier embeds in every transaction id: `TX-<8 digits><n>` becomes `TX-00000000<n>`
- the Payment API Key Kashier echoes in `apiKey`
- `merchantDetails`, the `metaData.kashier_user` block and `history[].performedBy`
- card and CVV tokens, names, emails, IPs and user agents
- the public tunnel host, which becomes `playground.example`

Second, it **signed the result again with a dummy key**, using the scheme Kashier was seen to use. The key is in each file's `signingKey` (`playground-fixture-key-not-a-secret`).

As a result:
- **Webhook and return signatures verify with `signingKey`.** They don't verify with any real key.
- The original signatures were checked against the real keys before sanitizing. `schemeObserved: true` records that.
- Amounts, statuses, events, ids (other than the merchant number) and field order are untouched.

## Layout
| Folder | What | Shape |
|---|---|---|
| `webhooks/` | Server webhooks (`POST serverWebhook`) | `{ kind, label, signingKey, scheme, headers: { x-kashier-signature }, body }`. `body` is the raw JSON Kashier posted. |
| `returns/` | Browser redirects to `merchantRedirect` | `{ kind, label, signingKey, scheme, query: [[name, value], …], queryString }`. `query` keeps Kashier's order. |
| `api/` | Calls PayGate makes to Kashier | `{ kind, label, method, path, status, request, response }` |

| File | Shows |
|---|---|
| `webhooks/pay-success-card.json` | `pay` / SUCCESS, card captured at once (50 EGP) |
| `webhooks/pay-success-wallet.json` | `pay` / SUCCESS by wallet. The payment page then failed, so there was no redirect. |
| `webhooks/pay-failure.json` | `pay` / FAILURE, code `AA` (declined card) |
| `webhooks/retry-1-pay-failure.json`, `retry-2-pay-success.json` | A failed try and a successful retry in one session: **two different `kashierOrderId`s** for one `merchantOrderId` |
| `webhooks/authorize-success.json` | `authorize` / SUCCESS: 3-D Secure card, funds held but not captured |
| `webhooks/capture-success.json` | `capture` / SUCCESS after an API capture |
| `webhooks/refund-partial.json` | `refund` / SUCCESS, `amount: 20` (the refund itself, not a running total) |
| `webhooks/refund-remainder.json` | `refund` / SUCCESS, `amount: 63.34`: a refund without an amount refunds what is left |
| `webhooks/void-success.json` | `void` / SUCCESS on a same-day captured payment |
| `returns/success.json`, `failed.json`, `authorized.json` | Redirects with `paymentStatus` SUCCESS, FAILED (not FAILURE) and AUTHORIZED |
| `api/create-session.json` | `POST /v3/payment/sessions`, 200 |
| `api/create-session-localhost-redirect-400.json` | 400 `"merchantRedirect" must be a valid URL` for a localhost redirect |
| `api/search-orders-retried-session.json` | `GET /v3/payment/orders?search=` returning **two orders** for one reference (CAPTURED and FAILED) |
| `api/capture.json`, `refund-partial.json`, `refund-remainder.json`, `void.json` | `PUT {fep}/v3/orders/{orderId}`, all synchronous 200s |
| `api/session-*.json` (10 files, 2026-11-23) | `GET /v3/payment/sessions/{id}/payment`: `paid`, `3ds-paid`, `wallet-paid`, `retried-paid` (points at the successful order), `partially-refunded`, `refunded`, `voided`, `unpaid-expired` (`orderId: NA`), `failed-expired` (the decline is in `history`), `not-found-404` |
| `api/refund-over-refund-401.json` | Refunding more than is left gives **401** `Authorization error`, not 400 |

Don't edit these files by hand. If you change a signed field, the signature no longer verifies. Record new traffic with the playground instead.
