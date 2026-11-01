# Parochos API v3 integration notes

This project uses Parochos API v3 (`X-Version: 3.0`) and the API documentation supplied with the project.

## 1. Configure the provider

Open `/admin/fiscal-settings` and set:

- Subscription Key
- API User
- API Password
- Beta/Production
- Transmission Type (`1` = transmission + provider PDF, `5` = transmission without provider PDF)
- Special Invoice Category (`12` for restaurant/catering `requestPayment` according to the supplied API documentation)
- Receipt type
- Invoice type

POS settings are maintained separately, per terminal, under `/admin/bank-pos` (**Τραπεζικά POS**). Set the physical Terminal ID, Parochos NSP Code, and signing ProviderId on the POS record; do not use the old EFT-POS fields in Fiscal Settings. For this installation, the provider confirmed ePay NSP Code `2`; EDPS uses `5`. ProviderId is normally `19` for both.

For Order Forms, enable the option and keep `8.6` unless the provider/account configuration tells you otherwise.

## 2. Authentication

The application calls:

`/api/account/loginToSubscription`

and stores the returned JWT, refresh token and dynamic `url1` in memory. Provider calls use the returned `url1` and the `Authorization: Bearer` header.

The application never hard-codes the provider API server URL returned by login.

## 3. Final accounting document

The application calls `/api/send` with:

- `identifier = "Parochos"`
- configured `transmissionType`
- the generated `source`

If the response is `status = 0`, `/api/get` is polled until the document reaches a final status.

The order stores the resulting MARK, UID, authentication code, QR URL and PDF URL.

## 4. Card / EFT-POS

The provider flow is:

1. `/api/requestPayment`
2. Provider returns payment signature
3. The signature must be committed to the actual EFT-POS terminal by the POS/NSP integration
4. The terminal returns a transaction ID
5. The application calls `/api/send` with payment method `type = 7`, signature, transactionId, terminalId and nspCode

The Parochos HTTP API itself does not provide a generic command that physically executes the bank transaction on an arbitrary terminal. When no supported terminal connector is selected, the application still requires the Transaction ID after the terminal transaction. ePay WebECR and the EDPS Middleware Chronos WebECR API v6 connector start the sale and return the Transaction ID.

## 5. Order Forms

When enabled:

- first Order Form opens the Point of Service
- each Order Form is sent as invoice type `8.6`
- the transmitted Order Form is assigned to the Point of Service
- on final receipt/invoice, previous Order Form MARKs are sent as `correlatedInvoices`
- `/api/OrderForms/ConnectDocuments` is called for each Order Form
- the final document closes the Point of Service

## 6. Existing SQLite database

The project still uses `EnsureCreated()`. `DataSeeder` now checks the existing SQLite schema and adds the newly required Parochos columns with `ALTER TABLE` when they are missing. Existing orders and settings are therefore preserved.


## ePay WebECR: distinguish the two provider identifiers

For ePay, the Parochos API field `nspCode` is `2`, as confirmed by the provider for this installation. The ePay WebECR `ProviderId` used in the `ProviderData` signature payload is `19`. These values serve different APIs and must never be copied into each other's fields. The ePay payment-preparation path therefore sends/stores Parochos `nspCode = "2"`, while leaving WebECR `ProviderId = 19` inside `ProviderData`.

The receipt is still issued only after the WebECR transaction returns success and a transaction ID. If the POS transaction fails, is cancelled, or times out, the receipt-issuing path is not called and the fiscal series counter is not advanced.

## EDPS Middleware — Chronos WebECR API v6

EDPS uses Parochos `nspCode = "5"`. The signed `ProviderData.ProviderId` identifies the electronic invoicing provider and defaults to `19`; it is not the EDPS AADE provider code (`127`). The connector redeems the POS one-time code, stores the returned POS API key, and sends the original Parochos signature encoding (Base64) through the EDPS Middleware HTTP API. It uses the preferred `terminal/txninit` route by default, with `terminal/{id}/txninit` available as an explicit optional override. See `EDPS_WEBECR_SETUP_EL.md` for the Middleware URL and POS settings.

For this installation, the EDPS UAT API base URL is `https://webapi-uat.ecr.novidea.gr` (token and redeem endpoints are `/token` and `/authorization/redeem`). The Postman collection's `http://localhost:8080/` examples apply only when the EDPS local Middleware Client is installed and listening on that machine. The connector sends HTTP requests to the configured API endpoint; it does not open a direct TCP/IP connection to the physical POS.
