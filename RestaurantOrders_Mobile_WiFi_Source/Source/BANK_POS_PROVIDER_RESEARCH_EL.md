# Τραπεζικά POS — τεχνική έρευνα και σχέδιο υλοποίησης

## Σημαντικό
Η Α.1155/2023 δεν είναι ένα ενιαίο API για όλες τις τράπεζες. Η υλοποίηση εξαρτάται από τον πραγματικό NSP/πάροχο, το πρωτόκολλο και το μοντέλο/firmware του τερματικού. Το ότι ένα API ηλεκτρονικού εμπορίου είναι δημόσιο δεν σημαίνει ότι μπορεί να ξεκινήσει συναλλαγή σε φυσικό EFT/POS.

## Επίσημες πηγές
- ΑΑΔΕ — ετοιμότητα ERP και επίσημες ονομασίες πρωτοκόλλων: https://www.aade.gr/etoimotita-erps
- ΑΑΔΕ — τεχνικά εγχειρίδια ανά NSP και μοντέλο POS: https://www.aade.gr/diasyndesi-pos-tameiakon-systimaton/tehnika-egheiridia-pos-tameiakon-mihanon-router
- ΑΑΔΕ — πρωτόκολλα/τεκμηρίωση Α.1155/2023 και πολιτικές δοκιμών: https://www.aade.gr/diasyndesi-pos-tameiakon-systimaton/protokolla-tekmiriosi-gia-diasyndesi-me-basi-tin-a11552023
- ΑΑΔΕ — πίνακας API keys παρόχων και στοιχεία επικοινωνίας: https://aade.gr/api-keys-parohon-meson-pliromon-npss-gia-tin-dilosi-blabis-pos-diasyndedemenoy-me-basi-tin-a11552023
- Viva — πρωτόκολλο ΑΑΔΕ: https://developer.viva.com/aade/
- Viva — Cloud Terminal API: https://developer.viva.com/apis-for-point-of-sale/card-terminals-devices/rest-api/eft-pos-api-documentation/
- Viva — POS API credentials: https://developer.viva.com/getting-started/find-your-account-credentials/pos-apis-credentials/
- NEXI — FAQ διασύνδεσης ERP: https://www.nexi.gr/el/faqs/faqs-erp/
- NBG developer portal: https://developer.nbg.gr/
- ePay API documentation: https://docs.epay.eu/
- Cardlink developer portal: https://developer.cardlink.gr/api-products/

## Πρωτόκολλα που αναγνωρίζει ο επίσημος πίνακας ΑΑΔΕ
- CARDLINK: Common WebECR, Cardlink Web, Cardlink TCP Socket (DLL)
- EURONET: Common WebECR, Common TCP-Socket
- MELLON: Common WebECR, Common TCP-Socket
- EDPS: Common TCP Socket, EDPS JSON Protocol
- VIVA: Viva Cloud, Viva Peer-to-Peer, Viva App-to-App
- NEXI: Common WebECR, Common TCP-Socket
- MYPOS: Common TCP-Socket
- Επιπλέον πάροχοι εμφανίζονται στον ίδιο πίνακα, με διαφορετικές επιλογές πρωτοκόλλου.

## Τι χρειάζεται πριν από κάθε πραγματικό connector
1. Να επιβεβαιωθεί ο NSP που είναι δηλωμένος για το συγκεκριμένο POS — η τράπεζα/Acquirer και ο NSP δεν είναι πάντοτε το ίδιο πράγμα.
2. Να επιβεβαιωθεί το μοντέλο/λειτουργικό του POS και το πρωτόκολλο που έχει ενεργοποιηθεί από τον πάροχο.
3. Να ληφθεί το αντίστοιχο εγχειρίδιο και το demo/sandbox endpoint/credentials από τον πάροχο.
4. Να υλοποιηθούν ξεχωριστά Connect/Validate, Redeem (όπου υπάρχει), Sale, Cancel/Void, Refund και Transaction Status, με idempotency/χειρισμό timeout ώστε να αποφεύγονται διπλές χρεώσεις.
5. Να δοκιμαστούν έγκριση, απόρριψη, timeout, ακύρωση, επιστροφή, split payments και επανεκκίνηση εφαρμογής σε sandbox.
6. Μόνο μετά τις δοκιμές να ενεργοποιηθεί παραγωγική λειτουργία και να επιβεβαιωθούν οι απαιτήσεις συμμόρφωσης.

## Τρόποι πληρωμής και Parochos
Το RestaurantOrders αποθηκεύει τον τρόπο πληρωμής σε επίπεδο OrderPayment. Στη διαβίβαση προς Parochos, ο τύπος EFT-POS είναι `7` και για μετρητά χρησιμοποιείται ο ρυθμιζόμενος κωδικός με προεπιλογή `3`. Η τελική λογική πρέπει να παραμείνει ανά πραγματικό payment entry — όχι να ορίζει όλο το παραστατικό ως μετρητά ή κάρτα επειδή υπάρχει μία γενική ρύθμιση. Τα split payments πρέπει να ελεγχθούν end-to-end με Parochos πριν από χρήση σε παραγωγή.

## Κατάσταση αυτής της έκδοσης
Η καρτέλα αποθηκεύει ρυθμίσεις τερματικών και πλέον έχει πεδία ID URL, Provider Code, Redeem code και Viva App-to-App flag. Δεν ενεργοποιείται ακόμη πραγματικό Redeem ή χρέωση από αυτά τα πεδία. Κάθε λειτουργία θα συνδεθεί με τον αντίστοιχο τεκμηριωμένο connector και δεν θα εμφανίζεται επιτυχία χωρίς πραγματική απάντηση παρόχου.

## Ασφάλεια
Τα demo credentials που εμφανίστηκαν σε screenshots πρέπει να ανακληθούν/αλλαχθούν εάν είναι ενεργά. Πριν από παραγωγική χρήση, τα secrets πρέπει να αποθηκεύονται με προστασία στο επίπεδο του λειτουργικού/DPAPI ή αντίστοιχο ασφαλές secret store και να μην εμφανίζονται ξανά σε plain text ή logs.
