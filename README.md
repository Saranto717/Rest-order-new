# RestaurantOrders v17.2

Εφαρμογή παραγγελιοληψίας με διασύνδεση Parochos και ανεξάρτητους connectors POS για Mellon WebECR, Viva Cloud Terminal, epay και EDPS.

Η έκδοση συνδυάζει τη v17.1 Mellon RedeemFix με τη λειτουργική υλοποίηση Viva που δόθηκε. Περιλαμβάνει επιλογή αποθηκευμένου ενεργού POS, ολικές/μερικές πληρωμές, έκδοση απόδειξης/τιμολογίου και ProviderId `019` προς Viva.

## Build και εκτέλεση

Για εγκατάσταση πελάτη χωρίς κώδικα ή εντολές, δες τον [οδηγό Setup.exe για Windows](installer/README_EL.md). Το GitHub Actions παράγει self-contained installer και ελέγχει εγκατάσταση, αναβάθμιση και διατήρηση δεδομένων σε Windows.

Απαιτείται .NET SDK 8.

```sh
dotnet build RestaurantOrders_Mobile_WiFi_Source/Source/RestaurantOrders.csproj
cd RestaurantOrders_Mobile_WiFi_Source/Source
dotnet run
```

Η εφαρμογή δημιουργεί την τοπική SQLite βάση κατά την πρώτη εκκίνηση. Η βάση και τα διαπιστευτήρια της εγκατάστασης δεν περιλαμβάνονται στο repository. Σε υπάρχουσα εγκατάσταση διατήρησε τη δική σου `restaurant.db` κατά την αναβάθμιση.

## Έλεγχοι

```sh
dotnet run --project RestaurantOrders_Mobile_WiFi_Source/Tests/MellonRegression/MellonRegression.csproj
dotnet run --project RestaurantOrders_Mobile_WiFi_Source/Tests/VivaRegression/VivaRegression.csproj
```

Έχουν περάσει 83 Mellon/regression assertions και 72 Viva integration assertions με SQLite στη μνήμη και προσομοιωμένα APIs. Δεν έγινε πραγματική τραπεζική συναλλαγή από το περιβάλλον ελέγχων.

## Οδηγίες

- [Ενσωμάτωση Viva και αναβάθμιση v17.2](VIVA_INTEGRATION_V17_2_EL.md)
- [Ρυθμίσεις Viva Cloud Terminal](RestaurantOrders_Mobile_WiFi_Source/Source/VIVA_CLOUD_TERMINAL_SETUP_EL.md)
- [Ρυθμίσεις Mellon ανά POS](MELLON_MULTIBANK_PATCH_EL.md)
- [Διασύνδεση Parochos](RestaurantOrders_Mobile_WiFi_Source/Source/PAROCHOS_INTEGRATION.md)

Ο NSP Code ρυθμίζεται ανά εγγραφή POS. Διατήρησε την τιμή που έχει επαληθευτεί για τη συγκεκριμένη σύνδεσή σου.
