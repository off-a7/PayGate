# Kashier

> **Status:** these are the sandbox findings that `PayGate.Kashier` is built on (Phase 3 spike, items P3-01 to P3-06). Setup and usage instructions will be added when the package ships in `0.3.0-preview`.

The rules below were seen in Kashier's **test** environment on **2026-11-22** and **2026-11-23**. Each one is checked against both the developer docs (developers.kashier.io, read the same day) and recorded traffic. The traffic is in [tests/fixtures/kashier](../../tests/fixtures/kashier/README.md), sanitized and re-signed with a dummy key. Where the docs and the traffic disagree, the traffic wins, and the table at the end lists the differences.

## Hosts, keys and headers

| | Test | Live |
|---|---|---|
| Payment API (sessions, order inquiry) | `https://test-api.kashier.io` | `https://api.kashier.io` |
| FEP (refund, void, capture, transfers) | `https://test-fep.kashier.io` | `https://fep.kashier.io` |
| Payment page | `https://payments.kashier.io/session/{id}?mode=test` | `…?mode=live` |

| Key | Used for |
|---|---|
| **Merchant ID** (`MID-…`) | the `merchantId` field of a session |
| **Secret Key** | the `Authorization` header, raw, with **no** `Bearer` scheme, on every API call |
| **Payment API Key** | the `api-key` header on session create, and the **signing key for webhooks and redirects** |
| **Transfer API Key** | payouts only, and the signing key for payout webhooks |

- Test and live keys only work on their own hosts.
- Kashier's test host drops HTTP/2 connections, so PayGate pins HTTP/1.1.
- **Kashier echoes secrets in responses.** The Payment API Key comes back as `apiKey` in the session-create response and as `merchantDetails.apiKey` in webhooks and order inquiries. Inquiries also return card and CVV tokens. **Never log or store a raw Kashier body.**

## Creating a payment
`POST {api}/v3/payment/sessions` returns `_id` (the session id), `status: CREATED` and `sessionUrl`.

- **`merchantRedirect` must be a public URL.** `http://localhost:…` is rejected with `400 "merchantRedirect" must be a valid URL`.
- `serverWebhook` can be set per request, so the dashboard needs no webhook URL.
- `metaData` round-trips: it comes back in webhooks and inquiries, with Kashier's own keys added.
- **A reused merchant reference is not rejected at create.** A second session for an existing `order` returns 200. The failure only appears when the customer pays: the payment page gets `400 "The order has already been paid before"` (`isPaidBefore: true`) and shows an error, while **the merchant gets no webhook and no redirect**.
  - `ERR_ORD_02` never appeared.
  - Use a new merchant reference for every payment attempt.
- A session that is never paid stays `CREATED` and expires at `expireAt`.

## One reference, several Kashier orders
With `failureRedirect: false`, a customer whose card is declined can try again on the same page. **Every try is a new Kashier order**: a new `orderId` (sent as `kashierOrderId` in webhooks) under the same `merchantOrderId`.

So:
- `GET {api}/v3/payment/orders?search=<ref>` can return several orders for one reference, newest first. It is also a **partial** match, so filter `merchantOrderId` exactly.
- **The payment is the order that got past `FAILED`** (`CAPTURED`, `AUTHORIZED`, `PARTIALLY_REFUNDED`, `REFUNDED` or `CANCELLED`). Only when every order failed is the newest one the answer. Never take `data[0]` blindly.
- Refund, void and capture go to **that** order's `orderId`, not to the `kashierOrderId` of the first webhook.
- A `Failed` → `Succeeded` change for one reference is normal.

## Reading one payment: the session lookup
`GET {api}/v3/payment/sessions/{sessionId}/payment` (Secret Key only) returns `{message, data}`, where `data` is **the session's single outcome**. That makes it what `GetPaymentAsync` reads, with the session id as PayGate's provider payment id. Recorded on 2026-11-23:

| `data.status` | When | PayGate |
|---|---|---|
| `CREATED`, `OPENED`, `PENDING` | not paid yet (seen in `history`) | Pending |
| `PAID` | paid and captured, by card or wallet | Succeeded |
| `PARTIALLY_REFUNDED` / `REFUNDED` | after refunds | PartiallyRefunded / Refunded |
| `VOIDED` | after a void | Canceled |
| `EXPIRED` | the session ended unpaid, **including after a declined try** | Expired |

- **A retried session points at the order that went through.** With a decline then a success, `orderId` is the successful order, so there's nothing to choose. Refunds and voids use this `orderId`. Until someone tries to pay it is the literal `NA`.
- `capturedAmount` and `refundedAmount` are the session's totals, and `targetTransactionId` is the pay or capture transaction.
- `sourceOfFunds` is flat here (`maskedCard`, `cardBrand`), unlike the order inquiry's `sourceOfFunds.cardInfo`.
- `fees`, `vat` and `settlementAmount` describe the last operation. They're negative after a refund or void.
- `history[]` lists every step, newest first, with `operation`, `status`, `transactionResponseCode` and a bilingual message. The newest `FAILURE` explains a declined session.
- An unknown session id answers `404 {"messages":{"en":"Payment session not found"}}`.
- It also echoes the dashboard user behind refunds and voids (`history[].performedBy`, `metaData.kashier_user`). Don't log it.

## Order inquiry
Each order has `status`, `totalCapturedAmount`, `totalRefundedAmount`, `totalAuthorizedAmount`, `method`, `sourceOfFunds.cardInfo` (`maskedCard`, `cardBrand`) and `transactions[]`.

- `transactions[]` lists every step, including `3dsecure_verify`, `authenticate_payer`, `3dsecure_process_acs_result` and `initiate_r2p`. **The payment transaction is the one whose `operation` is `pay`, `authorize` or `capture`, not the last row.**
- Fees sit in each transaction's `pccFees`. In the test account: 5 EGP flat plus VAT, so 113.34 settled as 107.64.
- `reconcilation` really is spelled that way.

## Statuses

| Order `status` | PayGate |
|---|---|
| `CREATED`, `PENDING` | Pending |
| `AUTHORIZED` | Authorized |
| `CAPTURED` | Succeeded |
| `PARTIALLY_REFUNDED` | PartiallyRefunded |
| `REFUNDED` | Refunded |
| `CANCELLED` | Canceled |
| `FAILED` | Failed |
| anything else | Unknown (kept raw) |

**A payment can stop at `AUTHORIZED` without asking for it.** The first sandbox payment (3-D Secure Visa, 2026-11-22) ended as `authorize` / `AUTHORIZED` even though `manualCapture: false` was sent. The same card on 2026-11-23, and every other card, went straight to `pay` / `CAPTURED`, so the cause is not the card alone. PayGate reports `Authorized` whenever Kashier does:
- an `AUTHORIZED` payment needs a capture before it can be refunded, and a void releases it instead
- the authorization expires after 7 days (`authExpiryDate`)

## Capture, refund and void
All three are `PUT {fep}/v3/orders/{orderId}` with the Secret Key only, and `apiOperation` set to `CAPTURE`, `REFUND` or `VOID`.

The `transaction` object is optional and holds `amount` and `targetTransactionId`. Without it, the call acts on the whole remainder.

| Call | Result |
|---|---|
| `CAPTURE` | 200, `status: CAPTURED` |
| `REFUND`, amount 20 | 200, `PARTIALLY_REFUNDED`, refunded 20 |
| `REFUND`, amount 30 | 200, `PARTIALLY_REFUNDED`, refunded 50 (**several refunds work**) |
| `REFUND`, no amount | 200, `REFUNDED`: refunds **what is left** (63.34), not the original amount |
| `REFUND` beyond what is left | **401** `{"error":"Authorization error","message":"Unauthorized"}` |
| `VOID` on a same-day `pay` | 200, `CANCELLED`, captured back to 0 |

- Every response was synchronous. The `PENDING` refund result in the docs was never seen.
- Each response carries the order's new totals and the new `transactionId`.
- **A 401 from a refund doesn't always mean a bad key.** An over-refund returns 401 too.
- Voiding a `pay` or `capture` only works on the same day (docs). Voiding an `AUTHORIZED` payment has no such limit.

## Webhooks
- The header is `x-kashier-signature`, signed with **HMAC-SHA256 (lowercase hex) using the Payment API Key**. The signed string is built from `data.signatureKeys`:
  - sorted ordinally
  - each pair written as `key=value`, joined with `&`
  - **only the values** RFC 3986-encoded (a space becomes `%20`)
  - values taken as their raw JSON text, so `1` stays `1` and `113.34` stays `113.34`
- Every event signs the same keys: `amount, channel, currency, kashierOrderId, merchantOrderId, method, orderReference, status, transactionId, transactionResponseCode`.
- `event` is one of `pay`, `authorize`, `capture`, `refund`, `void`, and per the docs also `reject`, `reversal`, `partial_refund` and `idempotency`. `data.status` is `SUCCESS` or `FAILURE`.
  - A refund reports `SUCCESS` too, so `event` decides what happened and `status` decides whether it worked.
- **`data.amount` is in major units** (`113.34`, not `11334`), and it is the amount of **this** operation:
  - a refund webhook carries only that refund
  - there is no running total, no partial/full flag and no target transaction
  - **partial refunds arrive as `refund`**; `partial_refund` was never seen
  - totals come from the order inquiry
- A declined card sends `pay` / `FAILURE` with `transactionResponseCode` (`AA` = unspecified failure) and a bilingual `transactionResponseMessage`.
- Each operation has its own `transactionId`, so `transactionId + event + status` identifies an event.
- Kashier accepts 200 or 409 as an acknowledgement, and retries other answers at 2 min, 10 min, 30 min, 1 h, 2 h and 4 h, then every 4 h, up to 10 times (docs).

## Redirect back to the merchant
The browser comes back to `merchantRedirect` with `paymentStatus`, `cardDataToken`, `maskedCard`, `merchantOrderId`, `orderId`, `cardBrand`, `orderReference`, `transactionId`, `amount`, `currency`, `mode` and `signature`.

The signature is **HMAC-SHA256 (lowercase hex) with the Payment API Key**, built from this list:
- the fixed list `paymentStatus, cardDataToken, maskedCard, merchantOrderId, orderId, cardBrand, orderReference, transactionId, amount, currency`, in that order
- each pair written as `key=value`, joined with `&`
- the **decoded** values, **not** URL-encoded
- a missing field signed as the literal `null`

`signature` and `mode` are not part of the list.

- `paymentStatus` was seen as `SUCCESS`, `FAILED` (while the webhook says `FAILURE`) and `AUTHORIZED`.
- `amount` is a major-unit decimal string, without trailing zeros (`50`, `75.5`).
- **The redirect is informational and is not guaranteed.** A wallet payment succeeded (webhook and inquiry agree), but the payment page then failed and never redirected. Only a verified webhook or an inquiry may move an order.

## Test cards
These come from the docs; the expiry date picks the outcome.

| Card | Expiry | Seen |
|---|---|---|
| Mastercard `5111 1111 1111 1118` | `06/27` | approved and captured at once |
| Mastercard `5111 1111 1111 1118` | `02/37` | declined, `UNSPECIFIED FAILURE` (`AA`) |
| Visa `4508 7500 1574 1019` (3-D Secure) | `06/27` | approved after the ACS emulator page (submit its default "(Y) Authentication Successful"); once stopped at `AUTHORIZED`, later captured at once |

- The docs' `06/25` (approved) and `05/25` (declined) are in the past, so the payment page refuses them.
- CVV `100` = match.
- Test wallet: `01010101010`.

## Where the docs and the traffic disagree

| Topic | Docs | Seen |
|---|---|---|
| Duplicate `order` | `ERR_ORD_02` at create | Accepted at create; fails only in the payment page, silently for the merchant |
| Redirect `paymentStatus` | `SUCCESS` / `FAILURE` | `SUCCESS` / `FAILED` / `AUTHORIZED` |
| Partial refund webhook event | `partial_refund` | `refund` |
| Refund result | `SUCCESS`, `FAILURE` or `PENDING` | always synchronous `SUCCESS`; an over-refund is a 401 |
| Webhook amount unit | not stated (example `11334`) | major units |
| `manualCapture: false` | captured | captured, but once `AUTHORIZED` with no clear cause |

The older integration this replaces signed redirects with the Secret Key over the sorted query. **That scheme does not match Kashier's signatures.** The docs' fixed-list scheme with the Payment API Key does.

## Still open
- The `idempotency` replay event: needs a resend from the dashboard.
- Voiding an `AUTHORIZED` payment.
- Payouts and the payout webhook (P3-05): waiting for a Transfer API Key.
