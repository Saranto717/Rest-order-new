# Viva Cloud Terminal — RestaurantOrders

## Τι προστέθηκε

- Ξεχωριστός `VivaCloudTerminalService` για το επίσημο Viva Cloud Terminal REST API.
- OAuth2 Client Credentials με τα **POS APIs credentials** του εμπόρου.
- Αναζήτηση POS με `statusId = 1` (Live).
- Αποστολή πώλησης στο `POST /ecr/v1/transactions:sale`.
- Polling του `GET /ecr/v1/sessions/{sessionId}` μέχρι να επιστραφεί τελικό αποτέλεσμα.
- Προώθηση της υπογραφής Parochos στα πεδία `aadeProviderId`, `aadeProviderSignatureData` και `aadeProviderSignature`.
- Σύνδεση της πληρωμής με την υφιστάμενη ροή έκδοσης παραστατικού, τόσο για πλήρη όσο και για τμηματική πληρωμή.
- Σε timeout/αμφίβολο αποτέλεσμα δεν χαρακτηρίζεται η πληρωμή επιτυχής και δεν γίνεται αυτόματη επανάληψη. Απαιτείται έλεγχος ιστορικού Viva πριν από νέα προσπάθεια.

## Ρυθμίσεις διαχειριστή

Στο **Διαχείριση → Τραπεζικά POS** επίλεξε:

- Πάροχος: `Viva`
- Πρωτόκολλο: `Viva Cloud`
- Περιβάλλον: Demo/Test αρχικά, Παραγωγικό μόνο μετά από επιτυχημένες δοκιμές
- Terminal ID: μπορεί να συμπληρωθεί με την «Αναζήτηση ενεργών Viva POS»
- Viva Cash Register ID (ECR ID): μοναδικό ID του ταμείου που ορίζει ο έμπορος
- Viva POS APIs Client ID / Client Secret: από Viva → Ρυθμίσεις → Πρόσβαση API → POS APIs Credentials
- Parochos NSP Code: από τον Parochos/λογιστή/σύμβαση. Δεν επινοείται ούτε θεωρείται ότι είναι το Viva Provider ID.
- ProviderId υπογραφής Parochos: διατηρείται η τιμή που χρησιμοποιεί η εγκατάσταση για την υπογραφή.

Τα `Merchant ID & API Key` και τα `Smart Checkout credentials` δεν είναι τα POS APIs credentials.

## Σημαντικές λεπτομέρειες API

- Token URL demo: `https://demo-accounts.vivapayments.com/connect/token`
- Token URL production: `https://accounts.vivapayments.com/connect/token`
- API base demo: `https://demo-api.vivapayments.com/`
- API base production: `https://api.vivapayments.com/`
- OAuth scope: `urn:viva:payments:ecr:api`
- Το ποσό αποστέλλεται σε λεπτά ευρώ (integer), π.χ. 12,34 € → 1234.
- Το `cashRegisterId` είναι διαφορετικό από το φυσικό `terminalId`.
- Το HTTP 200 κατά την έναρξη σημαίνει ότι δημιουργήθηκε session, όχι ότι εγκρίθηκε η κάρτα. Επιτυχία θεωρείται μόνο όταν το session επιστρέψει `success = true` και `transactionId`.

## Ενσωμάτωση στη v17.2 και έλεγχοι

Η υλοποίηση Viva μεταφέρθηκε στη v17.1 Mellon RedeemFix. Ο `VivaCloudTerminalService.cs` διατηρήθηκε ακριβώς όπως στο δοκιμασμένο project που δόθηκε.

- Ο ProviderId παραμένει αριθμητικά `19` στη ρύθμιση και αποστέλλεται προς Viva ως τριψήφιο string `019`.
- Η υπογραφή προς Viva μεταφέρεται σε Base64 ASN.1/DER. Η αρχική υπογραφή Parochos διατηρείται για την τελική αποστολή παραστατικού.
- Οι ολικές και μερικές εξοφλήσεις συνδέονται με την υπάρχουσα έκδοση απόδειξης/τιμολογίου μέσω Parochos.
- Μετά από εγκεκριμένη συναλλαγή, επανάληψη λόγω αποτυχίας φορολογικής έκδοσης από την ίδια οθόνη προχωρά στην έκδοση, χωρίς νέα αποστολή ποσού στη Viva.
- Η Viva εμφανίζεται στην πληρωμή όταν είναι ενεργή εγγραφή `Viva Cloud` με TID, ECR ID, Client ID/Secret και NSP Code. Η Live κατάσταση ελέγχεται από τον connector πριν από τη χρέωση.
- Ο NSP Code συμπληρώνεται με την τιμή της δοκιμασμένης σύνδεσής σου. Δεν επιβάλλεται συγκεκριμένος κωδικός βάσει μόνο του ονόματος της τράπεζας.

Το build με .NET 8 ολοκληρώθηκε με 0 errors και τις 11 προϋπάρχουσες προειδοποιήσεις. Πέρασαν 72 έλεγχοι Viva και 83 έλεγχοι Mellon/regression, με προσωρινή SQLite στη μνήμη και προσομοιωμένες HTTP απαντήσεις.

Οι έλεγχοι Viva περιλαμβάνουν OAuth, Live POS, επιτυχή/απορριφθείσα συναλλαγή, αβέβαιο session, μορφή `019`, Base64 υπογραφή, custom/default endpoints, αποθήκευση ρυθμίσεων και τέσσερις πλήρεις ροές: ολική/μερική πληρωμή επί απόδειξης/τιμολογίου από υπογραφή Parochos έως τελική διαβίβαση.

Δεν έγινε εδώ πραγματική συναλλαγή σε Viva/POS. Μετά την αναβάθμιση, δοκίμασε τη συνδυασμένη έκδοση με τα ίδια demo στοιχεία της δικής σου λειτουργικής εγκατάστασης.

Η υποστήριξη της Viva Cloud Terminal εφαρμογής εξαρτάται από το λογισμικό/firmware του τερματικού. Το API δεν πρέπει να θεωρηθεί συμβατό με κάθε συσκευή Viva ανεξαρτήτως λογισμικού.
