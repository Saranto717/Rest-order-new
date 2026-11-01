# Split payment: use the original Order Form

This change implements the requested flow:
- A table has one original Order Form (for example, 4 items x 3 EUR = 12 EUR).
- First split receipt (6 EUR) includes `correlatedInvoices` with the MARK of that original Order Form.
- `ConnectDocuments` is called using the original Order Form's `processId` and `externalSystemId`, with the already-issued payment MARKs plus the current receipt MARK.
- Final receipt (remaining 6 EUR) follows the same flow and targets the same original Order Form. The code does not create a new Order Form from the split payment method.

The original Order Form is selected as the earliest non-cancelled Order Form with a MARK for that order. If none exists, the split-payment issue is stopped before sending the receipt, to avoid issuing an uncorrelated receipt.

Important: this source patch has not been compiled or tested against the Parochos beta service in this environment. Test only in beta after backing up the database. Verify the receipt's `correlatedInvoices` in the transmitted payload and that Parochos actually links both receipt MARKs to the original Order Form MARK; HTTP 200 alone is not proof of successful linking.
