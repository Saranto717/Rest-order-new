# RestaurantOrders v17.2 — ενσωμάτωση Viva

Ξεκίνα από το [VIVA_INTEGRATION_V17_2_EL.md](VIVA_INTEGRATION_V17_2_EL.md) για αναβάθμιση και ρύθμιση. Παρακάτω διατηρούνται οι σημειώσεις προηγούμενων εκδόσεων.

# RestaurantOrders v17.1 — Mellon ανά POS

Για τις αλλαγές και τις οδηγίες της νέας έκδοσης διάβασε το [MELLON_MULTIBANK_PATCH_EL.md](MELLON_MULTIBANK_PATCH_EL.md). Οι παρακάτω σημειώσεις αφορούν την προηγούμενη παράδοση epay/EDPS.

# RestaurantOrders EDPS/ePay WebECR v15 — ePay NSP Code 2

This source package preserves the project folder structure. Open `RestaurantOrders_Mobile_WiFi_Source/Source/RestaurantOrders.csproj` in Visual Studio or build it with the .NET SDK.

## POS configuration change

All POS-specific data now comes from the selected record in **Ρυθμίσεις → Τραπεζικά POS**: physical Terminal ID, Parochos NSP Code, and signing ProviderId. Fiscal Settings keeps the Parochos account/company and invoice settings only. Set ePay NSP Code to `2` (confirmed by the provider) and EDPS NSP Code to `5` on the corresponding POS record; ProviderId is normally `19`. The WebECR backend/route ID remains separate from the physical TID.

## Changes based on the successful Parochos invoice JSON

- ePay card payments keep the Parochos signature in ASN.1/DER HEX for the subsequent Parochos invoice submission, matching the successful request sample.
- The final invoice `paymentMethods` sends `type`, `amount`, `signature`, `terminalId`, `transactionId`, and `tipAmount`; it omits the extra `nspCode` field seen in the previous implementation.
- Invoice detail line numbering now starts at `0`, matching the supplied sample.
- EDPS signature persistence remains on its existing path.

The ePay `txninit` ProviderData JSON uses the physical TID, NSP Code, and ProviderId from the selected Bank POS record. Keep the test and production POS records and credentials aligned with their respective Parochos/ePay environments.

The included `restaurant.db` has the ePay POS NSP Code set to `2`. Generated `bin` and `obj` folders are excluded from the ZIP; open the solution/project in Visual Studio and build it there.
