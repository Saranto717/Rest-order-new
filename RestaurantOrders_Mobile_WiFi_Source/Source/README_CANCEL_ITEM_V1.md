# RestOrder – Ακύρωση Είδους v1

## Τι προστέθηκε
- `OrderItem.IsCancelled`, `CancelledAt`, `CancelReason` για audit-safe ακύρωση.
- Κουμπί `✕` σε κάθε ενεργό είδος στην οθόνη τραπεζιού.
- Για μη απεσταλμένο είδος: local cancellation χωρίς Parochos.
- Για ήδη απεσταλμένο είδος: Parochos `CancelConnected=1`, καταγραφή του ακυρωτικού, μαρκάρισμα των παλιών Order Forms ως cancelled και επανέκδοση του ενεργού υπολοίπου.
- Εκτύπωση `ΑΚΥΡΩΤΙΚΟ ΔΕΛΤΙΟ ΠΑΡΑΓΓΕΛΙΑΣ` με το MARK του ακυρωτικού.
- Τα ακυρωμένα είδη δεν συμμετέχουν πλέον σε λογαριασμό, τελικό fiscal document ή τελικό fiscal print.
- `FiscalOrderCancellations` για audit history.
- Migration compatibility μέσω `DataSeeder` για υπάρχον SQLite DB.
- Διορθωμένα issuer contact keys σύμφωνα με Parochos v3: `name`, `telephone`, `electronicMail`.

## Σημαντικό για δοκιμή
Το Parochos τεκμηριώνει `CancelConnected=1` ως ακύρωση όλων των ανοιχτών Order Tickets του Point of Service. Γι' αυτό η εφαρμογή προειδοποιεί τον χρήστη και μετά επανεκδίδει φορολογικά το ενεργό υπόλοιπο.

## Εγκατάσταση
1. Κράτησε backup του υπάρχοντος project και της `restaurant.db`.
2. Αντέγραψε τα περιεχόμενα αυτού του ZIP πάνω στο υπάρχον project.
3. ΜΗΝ αντικαταστήσεις `appsettings.json` ή `restaurant.db` από άλλο project.
4. Από τον φάκελο του project:
   `dotnet build`
5. Εκκίνησε κανονικά με `dotnet run`.

Το ZIP δεν περιέχει `restaurant.db`, `appsettings.json`, `bin`, `obj` ή `.git`.
