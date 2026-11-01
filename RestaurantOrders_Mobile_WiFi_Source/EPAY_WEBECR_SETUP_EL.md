# ePay WebECR REST integration

This source implements the REST integration based on `EFTPOS-WebECR v2.5.13.pdf`.

## Configure
1. Open **Ρυθμίσεις → Τραπεζικά POS** (`/admin/bank-pos`).
2. Create a terminal with provider **ePay / Euronet** and protocol **ePay WebECR REST API v2.5.13**.
3. Set **API Endpoint** to the exact Base URL provided by ePay (for example, the test URL shown in your ePay documentation/account). Do not add `/token/` at the end; the connector appends endpoints itself.
4. Enter ePay WebECR username and password.
5. Enter the terminal's physical TID in **Terminal ID**. Leave **WebECR terminal backend ID** blank initially; the connector searches `/terminal/` by TID. Only fill it if ePay has supplied the WebECR backend terminal `id`.
 6. Set **Parochos NSP Code** to `2` (the value supplied by your provider) and **ProviderId υπογραφής Parochos** to `19` on this same POS record. These values and the physical TID are used to request the payment signature. The backend ID is only for the WebECR transaction route. POS values are not read from Fiscal Settings.
7. If this is the third-party permissions workflow, enter the one-time code from the terminal in **Redeem Code** for the first payment. On successful redeem, the resulting API key is saved to the terminal settings and the one-time code is cleared. Or enter an existing API key in **API Key / Client ID**.
8. Enable the terminal and save.
9. Start with the ePay test environment and a test POS/account. During card payment, press **Πληρωμή μέσω ePay WebECR**. The app proceeds to issue the document only after WebECR returns completed/approved and a transaction identifier.

## Important limitations for testing
 - The ePay `txninit` request includes Parochos ProviderData from the selected POS record. If ePay rejects the provider signature, check that the POS record has the correct physical TID, NSP Code `2`, and ProviderId `19`, and that the Parochos credentials/signature belong to the same environment and terminal.
- If a request times out, the result may be unknown. Check the terminal/WebECR before retrying to avoid a duplicate charge.
- The app's existing manual Transaction ID path remains available as fallback.
- The source project compiled successfully in the workspace. A live POS/card transaction was not run as part of this change; validate first with the test environment and a test card/POS.

## Νέα ροή σύνδεσης POS από τη φόρμα Bank POS

1. Αποθήκευσε πρώτα το τερματικό με Provider `ePay / Euronet`, Base URL, Username, Password και τον one-time κωδικό που εμφανίζει το POS.
2. Πάτησε **Εξαργύρωση API Key (Redeem)**. Η εφαρμογή κάνει `POST token/` και μετά `POST authorization/redeem/` με `{ "Type": "webecr", "Code": "..." }`. Το `Id` της απάντησης αποθηκεύεται ως API Key και ο one-time κωδικός καθαρίζεται.
3. Πάτησε **Ανάκτηση ενεργών τερματικών**. Η εφαρμογή κάνει `GET terminal/` με Bearer token και `X-Api-Key`.
4. Επίλεξε το σωστό POS και πάτησε **Χρήση επιλεγμένου ID**. Το `TerminalID` είναι το φυσικό TID που εμφανίζει το POS. Το `id` είναι το WebECR backend ID που απαιτείται στο `terminal/{id}/txninit/`. Είναι διαφορετικά πεδία.
5. Πάτησε **Αποθήκευση ρυθμίσεων**.

Η ανάκτηση λίστας επιβεβαιώνει ότι το WebECR επιστρέφει τερματικά για αυτά τα δικαιώματα· δεν ξεκινά χρέωση. Χρησιμοποίησε δοκιμαστικό περιβάλλον και κάρτα δοκιμών πριν από πραγματικές συναλλαγές.
