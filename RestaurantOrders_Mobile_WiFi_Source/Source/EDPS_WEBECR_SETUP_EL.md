# EDPS Middleware — Chronos WebECR API v6

Η σύνδεση γίνεται μέσω του Chronos WebECR API v6. Για το UAT περιβάλλον της δικής σου σύνδεσης χρησιμοποίησε `https://webapi-uat.ecr.novidea.gr`. Η UAT Postman collection περιλαμβάνει επίσης δείγματα `http://localhost:8080/` για εγκατάσταση local Middleware Client· αυτή η διεύθυνση δουλεύει μόνο όταν υπάρχει ενεργή EDPS υπηρεσία που ακούει τοπικά στη θύρα 8080. Το εγχειρίδιο v6 προτιμά το `/terminal/txninit` χωρίς route ID· το `/terminal/{id}/txninit` παραμένει διαθέσιμο ως εναλλακτική.

## Ξεχωριστοί κωδικοί και αναγνωριστικά

- **EDPS AADE Provider Code: 127** — ο κωδικός EDPS ως NSP στην ΑΑΔΕ.
- **Parochos NSP Code: 5** — η τιμή `nspCode` για EDPS στο Parochos.
- **ProviderId: 19** — το ProviderId παρόχου ηλεκτρονικής τιμολόγησης μέσα στα υπογεγραμμένα `ProviderData`.
- **Terminal ID / TID** — το φυσικό TID του POS, που πρέπει να ταιριάζει με την υπογραφή Parochos.
- **Middleware route ID** — προαιρετικό `{id}` για τη διαδρομή `/terminal/{id}/txninit`. Αν μείνει κενό, χρησιμοποιείται το προτεινόμενο `/terminal/txninit` και το API key του redeem προσδιορίζει το POS.

Το EDPS NSP code 5 προέρχεται από το Appendix 5 του Parochos API. Το `ProviderId` 19 είναι ρύθμιση του παρόχου τιμολόγησης, όχι κωδικός EDPS.

## Ρύθμιση στην εφαρμογή

Στο **Ρυθμίσεις → Τραπεζικά POS**:

1. Δημιούργησε νέα εγγραφή και επίλεξε **EDPS → EDPS Middleware (Chronos WebECR API v6)**.
2. Για το δικό σου UAT βάλε **Base URL** `https://webapi-uat.ecr.novidea.gr`, **Auth URL** `https://webapi-uat.ecr.novidea.gr/token` και **Redeem URL** `https://webapi-uat.ecr.novidea.gr/authorization/redeem`.
3. Χρησιμοποίησε `http://localhost:8080/` μόνο αν η EDPS έχει εγκαταστήσει local Middleware Client στον ίδιο υπολογιστή με τον ASP.NET server της εφαρμογής και επιβεβαιώσεις ότι ακούει στη θύρα 8080. Διαφορετικά, η σύνδεση θα απορρίπτεται πριν φτάσει στο POS.
4. Συμπλήρωσε το φυσικό **Terminal ID**. Άφησε κενό το **Middleware route terminal ID** για να χρησιμοποιηθεί το προτεινόμενο `/terminal/txninit`. Βάλε route ID μόνο αν η EDPS έχει ορίσει συγκεκριμένο για τη σύνδεσή σου.
5. Συμπλήρωσε Username και Password που έδωσε η EDPS. Συμπλήρωσε integrator `X-Api-Key` μόνο αν απαιτείται για τον λογαριασμό σου.
6. Αποθήκευσε και πάτησε **Εξαργύρωση κωδικού EDPS (Redeem)** με τον one-time κωδικό που εμφανίζει το POS. Το API key που επιστρέφει το redeem αποθηκεύεται για τις επόμενες κλήσεις.
7. Βεβαιώσου ότι τα πεδία δείχνουν **Parochos NSP Code 5** και **ProviderId 19**. Επίλεξε EDPS στο διάλογο πληρωμής.

Για παραγωγή χρησιμοποίησε το `https://webapi.ecr.novidea.gr` και τα αντίστοιχα production credentials/API key που έχει εκδώσει η EDPS. Το UAT API key και τα UAT credentials δεν μεταφέρονται στο production.

Το `ProviderData.Signature` αποστέλλεται στο Middleware στην αρχική κωδικοποίηση Base64 του Parochos. Η ePay συνεχίζει να χρησιμοποιεί τη δική της μορφοποίηση υπογραφής.

Αν λήξει το timeout ή χαθεί η επικοινωνία κατά το `txninit`, έλεγξε πρώτα το POS/Middleware πριν επαναλάβεις τη συναλλαγή, για να αποφύγεις διπλή χρέωση.

## Απόρριψη συναλλαγής και επανάληψη

Κάθε προσπάθεια συναλλαγής χρησιμοποιεί νέα υπογραφή `ProviderData` του Parochos. Αν το EDPS επιστρέψει ρητή απόρριψη, το μήνυμα εμφανίζει την αιτία και το επόμενο πάτημα στο ίδιο κουμπί **Αποστολή ποσού στο EDPS POS** ζητά αυτόματα νέα υπογραφή και στέλνει ξανά το ποσό. Αυτό δεν εκδίδει MARK ή απόδειξη· το φορολογικό παραστατικό εκδίδεται μόνο αφού το EDPS επιβεβαιώσει έγκριση και επιστρέψει `TransactionId`.

Αν χαθεί η επικοινωνία ή το EDPS πει ότι η υπογραφή έχει ήδη χρησιμοποιηθεί, η έκβαση της προηγούμενης προσπάθειας μπορεί να είναι αβέβαιη. Έλεγξε πρώτα το ιστορικό του POS. Το ίδιο κουμπί ζητά επιβεβαίωση πριν ανανεώσει την υπογραφή και ξεκινήσει νέα προσπάθεια. Μην επιβεβαιώσεις αν η προηγούμενη συναλλαγή εγκρίθηκε.
