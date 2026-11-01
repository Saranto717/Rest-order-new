using System.Net.Sockets;
using System.Text;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

/// <summary>
/// Στέλνει "δελτία παραγγελίας" σε δικτυακούς θερμικούς εκτυπωτές ESC/POS (Epson/Star κ.λπ.)
/// μέσω απλού TCP socket στη θύρα 9100 (RAW printing - υποστηρίζεται από σχεδόν όλους
/// τους δικτυακούς εκτυπωτές κουζίνας/μπαρ).
/// </summary>
public class PrinterService
{
    private readonly ILogger<PrinterService> _logger;

    // ESC/POS commands
    private static readonly byte[] Init = { 0x1B, 0x40 };                  // ESC @  -> reset
    private static readonly byte[] BoldOn = { 0x1B, 0x45, 0x01 };          // ESC E 1
    private static readonly byte[] BoldOff = { 0x1B, 0x45, 0x00 };         // ESC E 0
    private static readonly byte[] DoubleSizeOn = { 0x1D, 0x21, 0x11 };    // GS ! 0x11 (double W/H)
    private static readonly byte[] DoubleSizeOff = { 0x1D, 0x21, 0x00 };   // GS ! 0
    private static readonly byte[] AlignCenter = { 0x1B, 0x61, 0x01 };     // ESC a 1
    private static readonly byte[] AlignLeft = { 0x1B, 0x61, 0x00 };       // ESC a 0
    private static readonly byte[] Feed4 = { 0x0A, 0x0A, 0x0A, 0x0A };

    // Μία μόνο τυπική εντολή πλήρους κοπής (GS V 0). Πολλαπλές παραλλαγές μαζί προκαλούσαν
    // πολλαπλή κοπή σε εκτυπωτές που τις αναγνωρίζουν όλες.
    private static readonly byte[] CutSequence = { 0x1D, 0x56, 0x00 };

    // Το σύμβολο "€" δεν υπάρχει στα παλιά (προ-ευρώ) ελληνικά codepages όπως το PC737,
    // γι' αυτό βγαίνει "?". Λύση: το γράφουμε πάντα μέσω Windows-1253 (που το έχει σίγουρα
    // στη θέση 0x80), αλλάζοντας codepage στιγμιαία μόνο για αυτόν τον χαρακτήρα.
    private static readonly byte[] SelectCp1253 = { 0x1B, 0x74, 7 };
    private static readonly Encoding Cp1253Encoding;

    static PrinterService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1253Encoding = Encoding.GetEncoding(1253);
    }

    public PrinterService(ILogger<PrinterService> logger)
    {
        _logger = logger;
    }

    /// <summary>Επιστρέφει (byte κωδικού για ESC t n, .NET Encoding) ανάλογα με την επιλογή codepage του εκτυπωτή.</summary>
    private static (byte EscTValue, Encoding Encoding) ResolveCodepage(string codepage)
    {
        return codepage?.Trim().ToUpperInvariant() switch
        {
            "1253" => (7, Cp1253Encoding),
            _ => (11, Encoding.GetEncoding(737))
        };
    }

    /// <summary>Τυπώνει ένα δελτίο παραγγελίας (κουζίνα/μπαρ) σε συγκεκριμένο σταθμό.</summary>
    public Task<bool> PrintTicketAsync(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        CompanySettings? companySettings = null,
        int? orderNumber = null)
    {
        var bytes = BuildOrderTicket(station, tableName, waiterName, items, companySettings, orderNumber);
        return SendAsync(station, bytes);
    }

    /// <summary>Τυπώνει φορολογικό/ηλεκτρονικό Δελτίο Παραγγελίας σε συγκεκριμένο σταθμό.</summary>
    public Task<bool> PrintOrderFormAsync(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        long? mark,
        string? qrCode,
        CompanySettings? companySettings = null,
        string documentTitle = "ΔΕΛΤΙΟ ΠΑΡΑΓΓΕΛΙΑΣ")
    {
        var bytes = BuildOrderFormTicket(
            station,
            tableName,
            waiterName,
            items,
            mark,
            qrCode,
            companySettings,
            documentTitle);

        return SendAsync(station, bytes);
    }

    /// <summary>Τυπώνει αναλυτικό λογαριασμό (όλα τα είδη του τραπεζιού + σύνολο).</summary>
    public Task<bool> PrintBillAsync(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        decimal total,
        CompanySettings? companySettings = null,
        Order? order = null,
        string? documentLabel = null)
    {
        var bytes = BuildBillTicket(station, tableName, waiterName, items, total, companySettings, order, documentLabel);
        return SendAsync(station, bytes);
    }

    /// <summary>
    /// Τυπώνει το τελικό φορολογικό παραστατικό με MARK και πραγματικό QR code ESC/POS.
    /// </summary>
    public Task<bool> PrintFiscalReceiptAsync(
        PrinterStation station,
        string tableName,
        string waiterName,
        string documentLabel,
        string seriesName,
        long invoiceNumber,
        long? mark,
        string? qrCode,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, decimal VatPercent)> items,
        decimal total,
        CompanySettings? companySettings = null,
        Order? order = null)
    {
        var bytes = BuildFiscalReceiptTicket(
            station, tableName, waiterName, documentLabel, seriesName, invoiceNumber, mark, qrCode, items, total, companySettings, order);
        return SendAsync(station, bytes);
    }

    /// <summary>Τυπώνει τη σύνοψη ενός συγκεκριμένου κλεισίματος ταμείου σε συγκεκριμένο εκτυπωτή.</summary>
    public Task<bool> PrintDayCloseAsync(PrinterStation station, DayCloseRecord record)
    {
        var bytes = BuildDayCloseTicket(station, record);
        return SendAsync(station, bytes);
    }

    private async Task<bool> SendAsync(PrinterStation station, byte[] bytes)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(station.IpAddress, station.Port);
            if (await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(5))) != connectTask)
            {
                _logger.LogWarning("Timeout σύνδεσης στον εκτυπωτή {Name} ({Ip}:{Port})", station.Name, station.IpAddress, station.Port);
                return false;
            }

            using var stream = client.GetStream();
            var copies = Math.Max(1, station.Copies);
            for (var i = 0; i < copies; i++)
            {
                await stream.WriteAsync(bytes);
            }
            await stream.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Αποτυχία εκτύπωσης στον εκτυπωτή {Name} ({Ip}:{Port})", station.Name, station.IpAddress, station.Port);
            return false;
        }
    }

    /// <summary>Μορφοποιεί ένα ποσό ως "12.34€" (χρησιμοποιείται σαν δεξιά στήλη).</summary>
    private static string Amount(decimal value) => $"{value:0.00}€";

    /// <summary>Δύο στήλες σε μία γραμμή σταθερού πλάτους (αριστερά/δεξιά στοίχιση).</summary>
    private static string TwoColumns(string left, string right, int width)
    {
        left ??= ""; right ??= "";
        if (left.Length + right.Length + 1 >= width)
        {
            return left.Length >= width ? left[..width] : left + " " + right;
        }
        var spaces = width - left.Length - right.Length;
        return left + new string(' ', spaces) + right;
    }

    /// <summary>Τρεις στήλες σε μία γραμμή σταθερού πλάτους: περιγραφή / ΦΠΑ / ποσό.</summary>
    private static string ThreeColumns(string left, string mid, string right, int width)
    {
        left ??= ""; mid ??= ""; right ??= "";
        var rightWidth = Math.Max(right.Length, 8);
        var midWidth = Math.Max(mid.Length, 6);
        var leftWidth = Math.Max(6, width - midWidth - rightWidth - 2);

        var l = left.Length > leftWidth ? left[..leftWidth] : left.PadRight(leftWidth);
        var m = mid.PadLeft(midWidth);
        var r = right.PadLeft(rightWidth);
        return $"{l} {m} {r}";
    }

    private byte[] BuildOrderTicket(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        CompanySettings? companySettings,
        int? orderNumber)
    {
        var (escT, enc) = ResolveCodepage(station.Codepage);
        var width = station.PaperWidthChars > 0 ? station.PaperWidthChars : 42;
        using var ms = new MemoryStream();

        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void WriteText(string s) { var b = enc.GetBytes(s); ms.Write(b, 0, b.Length); }
        void WriteLine(string s = "") { WriteText(s + "\n"); }

        void WriteEuroLine(string lineEndingInEuro)
        {
            if (lineEndingInEuro.Length == 0 || lineEndingInEuro[^1] != '€')
            {
                WriteLine(lineEndingInEuro);
                return;
            }
            WriteText(lineEndingInEuro[..^1]);
            Write(SelectCp1253);
            var eb = Cp1253Encoding.GetBytes("€");
            ms.Write(eb, 0, eb.Length);
            Write(new byte[] { 0x1B, 0x74, escT });
            WriteText("\n");
        }

        Write(Init);
        Write(new byte[] { 0x1B, 0x74, escT });

        Write(AlignCenter);
        Write(BoldOn);
        if (companySettings is not null)
        {
            if (!string.IsNullOrWhiteSpace(companySettings.CompanyName)) WriteLine(companySettings.CompanyName);
            if (!string.IsNullOrWhiteSpace(companySettings.VatNumber)) WriteLine($"ΑΦΜ: {companySettings.VatNumber}");
            if (!string.IsNullOrWhiteSpace(companySettings.TaxOffice)) WriteLine($"ΔΟΥ: {companySettings.TaxOffice}");
            if (!string.IsNullOrWhiteSpace(companySettings.MainActivity)) WriteLine(companySettings.MainActivity);
            var address = string.Join(" ", new[] { companySettings.Street, companySettings.StreetNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(address) || !string.IsNullOrWhiteSpace(companySettings.PostalCode) || !string.IsNullOrWhiteSpace(companySettings.City))
                WriteLine(string.Join(" ", new[] { address, companySettings.PostalCode, companySettings.City }.Where(x => !string.IsNullOrWhiteSpace(x))));
            if (!string.IsNullOrWhiteSpace(companySettings.ContactPhone)) WriteLine($"Τηλ: {companySettings.ContactPhone}");
        }
        Write(BoldOff);
        WriteLine(new string('-', width));
        Write(AlignLeft);
        Write(DoubleSizeOn);
        Write(BoldOn);
        WriteLine($"ΤΡΑΠΕΖΙ: {tableName}");
        if (orderNumber.HasValue)
        {
            Write(DoubleSizeOn);
            Write(BoldOn);
            WriteLine($"ΠΑΡΑΓΓΕΛΙΑ #{orderNumber.Value}");
            Write(DoubleSizeOff);
            Write(BoldOff);
        }
        Write(DoubleSizeOff);
        Write(BoldOff);

        WriteLine(TwoColumns("", waiterName, width));
        WriteLine(new string('-', width));

        foreach (var item in items)
        {
            Write(BoldOn);
            WriteEuroLine(ThreeColumns($"{item.Quantity} x {item.ItemName}", $"{item.VatPercent:0.#}%", Amount(item.LineTotal), width));
            Write(BoldOff);
            if (!string.IsNullOrWhiteSpace(item.Notes))
            {
                WriteLine($"   * {item.Notes}");
            }
        }

        WriteLine(new string('-', width));
        WriteLine($"Ώρα: {DateTime.Now:HH:mm:ss} {DateTime.Now:dd/MM/yyyy}");

        Write(Feed4);
        Write(CutSequence);

        return ms.ToArray();
    }

    private byte[] BuildOrderFormTicket(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        long? mark,
        string? qrCode,
        CompanySettings? companySettings,
        string documentTitle)
    {
        var (escT, enc) = ResolveCodepage(station.Codepage);
        var width = station.PaperWidthChars > 0 ? station.PaperWidthChars : 42;
        using var ms = new MemoryStream();

        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void WriteText(string s)
        {
            var b = enc.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        void WriteLine(string s = "") => WriteText(s + "\n");

        Write(Init);
        Write(new byte[] { 0x1B, 0x74, escT });

        Write(AlignCenter);
        Write(BoldOn);
        if (companySettings is not null)
        {
            if (!string.IsNullOrWhiteSpace(companySettings.CompanyName)) WriteLine(companySettings.CompanyName);
            if (!string.IsNullOrWhiteSpace(companySettings.VatNumber)) WriteLine($"ΑΦΜ: {companySettings.VatNumber}");
            if (!string.IsNullOrWhiteSpace(companySettings.TaxOffice)) WriteLine($"ΔΟΥ: {companySettings.TaxOffice}");
            if (!string.IsNullOrWhiteSpace(companySettings.MainActivity)) WriteLine(companySettings.MainActivity);
            var address = string.Join(" ", new[] { companySettings.Street, companySettings.StreetNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(address) || !string.IsNullOrWhiteSpace(companySettings.PostalCode) || !string.IsNullOrWhiteSpace(companySettings.City))
                WriteLine(string.Join(" ", new[] { address, companySettings.PostalCode, companySettings.City }.Where(x => !string.IsNullOrWhiteSpace(x))));
            if (!string.IsNullOrWhiteSpace(companySettings.ContactPhone)) WriteLine($"Τηλ: {companySettings.ContactPhone}");
        }
        Write(BoldOff);
        WriteLine(new string('-', width));
        Write(DoubleSizeOn);
        Write(BoldOn);
        WriteLine(documentTitle);
        Write(DoubleSizeOff);
        Write(BoldOff);

        WriteLine($"ΤΡΑΠΕΖΙ: {tableName}");
        WriteLine(TwoColumns("Σερβιτόρος", waiterName, width));
        WriteLine($"Ώρα: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        WriteLine(new string('-', width));

        Write(AlignLeft);

        foreach (var item in items)
        {
            Write(BoldOn);
            WriteLine($"{item.Quantity} x {item.ItemName}");
            Write(BoldOff);

            if (!string.IsNullOrWhiteSpace(item.Notes))
                WriteLine($"   * {item.Notes}");
        }

        WriteLine(new string('-', width));

        if (mark.HasValue)
        {
            Write(AlignCenter);
            Write(BoldOn);
            WriteLine($"ΜΑΡΚ: {mark.Value}");
            Write(BoldOff);
        }

        if (!string.IsNullOrWhiteSpace(qrCode))
        {
            WriteLine();
            Write(AlignCenter);
            WriteLine("QR ΑΑΔΕ");

            var qrData = Encoding.UTF8.GetBytes(qrCode.Trim());

            if (qrData.Length <= 65532)
            {
                // QR Model 2
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 });

                // Module size = 6
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x06 });

                // Error correction = M
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31 });

                // Store QR data
                var storeLen = qrData.Length + 3;
                var pL = (byte)(storeLen & 0xFF);
                var pH = (byte)((storeLen >> 8) & 0xFF);

                Write(new byte[]
                {
                    0x1D, 0x28, 0x6B,
                    pL, pH,
                    0x31, 0x50, 0x30
                });

                Write(qrData);

                // Print QR
                Write(new byte[]
                {
                    0x1D, 0x28, 0x6B,
                    0x03, 0x00,
                    0x31, 0x51, 0x30
                });
            }
            else
            {
                _logger.LogWarning(
                    "Το QR code του Order Form είναι πολύ μεγάλο: {Length} bytes",
                    qrData.Length);

                WriteLine(qrCode);
            }

            WriteLine();
        }

        Write(Feed4);
        Write(CutSequence);

        return ms.ToArray();
    }

    private byte[] BuildBillTicket(
        PrinterStation station,
        string tableName,
        string waiterName,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, string? Notes, decimal VatPercent)> items,
        decimal total,
        CompanySettings? companySettings,
        Order? order,
        string? documentLabel)
    {
        var (escT, enc) = ResolveCodepage(station.Codepage);
        var width = station.PaperWidthChars > 0 ? station.PaperWidthChars : 42;
        using var ms = new MemoryStream();

        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void WriteText(string s) { var b = enc.GetBytes(s); ms.Write(b, 0, b.Length); }
        void WriteLine(string s = "") { WriteText(s + "\n"); }

        void WriteEuroLine(string lineEndingInEuro)
        {
            if (lineEndingInEuro.Length == 0 || lineEndingInEuro[^1] != '€')
            {
                WriteLine(lineEndingInEuro);
                return;
            }
            WriteText(lineEndingInEuro[..^1]);
            Write(SelectCp1253);
            var eb = Cp1253Encoding.GetBytes("€");
            ms.Write(eb, 0, eb.Length);
            Write(new byte[] { 0x1B, 0x74, escT });
            WriteText("\n");
        }

        Write(Init);
        Write(new byte[] { 0x1B, 0x74, escT });

        Write(AlignCenter);
        Write(BoldOn);
        if (companySettings is not null)
        {
            if (!string.IsNullOrWhiteSpace(companySettings.CompanyName)) WriteLine(companySettings.CompanyName);
            if (!string.IsNullOrWhiteSpace(companySettings.VatNumber)) WriteLine($"ΑΦΜ: {companySettings.VatNumber}");
            if (!string.IsNullOrWhiteSpace(companySettings.TaxOffice)) WriteLine($"ΔΟΥ: {companySettings.TaxOffice}");
            if (!string.IsNullOrWhiteSpace(companySettings.MainActivity)) WriteLine(companySettings.MainActivity);
            var address = string.Join(" ", new[] { companySettings.Street, companySettings.StreetNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(address) || !string.IsNullOrWhiteSpace(companySettings.PostalCode) || !string.IsNullOrWhiteSpace(companySettings.City))
                WriteLine(string.Join(" ", new[] { address, companySettings.PostalCode, companySettings.City }.Where(x => !string.IsNullOrWhiteSpace(x))));
            if (!string.IsNullOrWhiteSpace(companySettings.ContactPhone)) WriteLine($"Τηλ: {companySettings.ContactPhone}");
        }
        Write(BoldOff);
        WriteLine(new string('-', width));
        Write(DoubleSizeOn);
        Write(BoldOn);
        WriteLine(documentLabel ?? "ΛΟΓΑΡΙΑΣΜΟΣ");
        Write(DoubleSizeOff);
        Write(BoldOff);
        Write(AlignLeft);

        WriteLine($"ΤΡΑΠΕΖΙ: {tableName}");
        WriteLine(TwoColumns("", waiterName, width));
        WriteLine($"Ώρα: {DateTime.Now:HH:mm:ss} {DateTime.Now:dd/MM/yyyy}");
        if (order is not null && !string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
        {
            WriteLine(new string('-', width));
            Write(BoldOn);
            WriteLine("ΣΤΟΙΧΕΙΑ ΠΕΛΑΤΗ");
            Write(BoldOff);
            if (!string.IsNullOrWhiteSpace(order.CounterpartName)) WriteLine($"Επωνυμία: {order.CounterpartName}");
            if (!string.IsNullOrWhiteSpace(order.CounterpartVatNumber)) WriteLine($"ΑΦΜ: {order.CounterpartVatNumber}");
            if (!string.IsNullOrWhiteSpace(order.CounterpartTaxOffice)) WriteLine($"ΔΟΥ: {order.CounterpartTaxOffice}");
            if (!string.IsNullOrWhiteSpace(order.CounterpartMainActivity)) WriteLine($"Δραστηριότητα: {order.CounterpartMainActivity}");
            var customerAddress = string.Join(" ", new[] { order.CounterpartStreet, order.CounterpartStreetNumber, order.CounterpartPostalCode, order.CounterpartCity }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(customerAddress)) WriteLine($"Διεύθυνση: {customerAddress}");
        }
        WriteLine(new string('-', width));

        foreach (var item in items)
        {
            WriteEuroLine(ThreeColumns($"{item.Quantity} x {item.ItemName}", $"{item.VatPercent:0.#}%", Amount(item.LineTotal), width));
            if (!string.IsNullOrWhiteSpace(item.Notes))
            {
                WriteLine($"   * {item.Notes}");
            }
        }

        WriteLine(new string('-', width));
        Write(BoldOn);
        WriteEuroLine(TwoColumns("ΣΥΝΟΛΟ", Amount(total), width));
        Write(BoldOff);
        WriteLine(new string('-', width));

        Write(Feed4);
        Write(CutSequence);

        return ms.ToArray();
    }

    private byte[] BuildFiscalReceiptTicket(
        PrinterStation station,
        string tableName,
        string waiterName,
        string documentLabel,
        string seriesName,
        long invoiceNumber,
        long? mark,
        string? qrCode,
        IEnumerable<(string ItemName, int Quantity, decimal LineTotal, decimal VatPercent)> items,
        decimal total,
        CompanySettings? companySettings,
        Order? order)
    {
        var (escT, enc) = ResolveCodepage(station.Codepage);
        var width = station.PaperWidthChars > 0 ? station.PaperWidthChars : 42;
        using var ms = new MemoryStream();

        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void WriteText(string s)
        {
            var b = enc.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        void WriteLine(string s = "") => WriteText(s + "\n");

        void WriteEuroLine(string line)
        {
            if (line.Length == 0 || line[^1] != '€')
            {
                WriteLine(line);
                return;
            }
            WriteText(line[..^1]);
            Write(SelectCp1253);
            var eb = Cp1253Encoding.GetBytes("€");
            ms.Write(eb, 0, eb.Length);
            Write(new byte[] { 0x1B, 0x74, escT });
            WriteText("\n");
        }

        Write(Init);
        Write(new byte[] { 0x1B, 0x74, escT });

        Write(AlignCenter);
        Write(BoldOn);
        if (companySettings is not null)
        {
            if (!string.IsNullOrWhiteSpace(companySettings.CompanyName)) WriteLine(companySettings.CompanyName);
            if (!string.IsNullOrWhiteSpace(companySettings.VatNumber)) WriteLine($"ΑΦΜ: {companySettings.VatNumber}");
            if (!string.IsNullOrWhiteSpace(companySettings.TaxOffice)) WriteLine($"ΔΟΥ: {companySettings.TaxOffice}");
            if (!string.IsNullOrWhiteSpace(companySettings.MainActivity)) WriteLine(companySettings.MainActivity);
            var address = string.Join(" ", new[] { companySettings.Street, companySettings.StreetNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(address) || !string.IsNullOrWhiteSpace(companySettings.PostalCode) || !string.IsNullOrWhiteSpace(companySettings.City))
                WriteLine(string.Join(" ", new[] { address, companySettings.PostalCode, companySettings.City }.Where(x => !string.IsNullOrWhiteSpace(x))));
            if (!string.IsNullOrWhiteSpace(companySettings.ContactPhone)) WriteLine($"Τηλ: {companySettings.ContactPhone}");
        }
        Write(BoldOff);
        WriteLine(new string('-', width));
        Write(DoubleSizeOn);
        Write(BoldOn);
        WriteLine(documentLabel.ToUpperInvariant());
        Write(DoubleSizeOff);
        Write(BoldOff);
        WriteLine($"Σειρά: {seriesName}    ΑΑ: {invoiceNumber}");
        WriteLine($"Τραπέζι: {tableName}");
        WriteLine($"Ώρα: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        WriteLine(TwoColumns("Χειριστής", waiterName, width));
        if (order is not null && !string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
        {
            WriteLine(new string('-', width));
            Write(BoldOn);
            WriteLine("ΣΤΟΙΧΕΙΑ ΠΕΛΑΤΗ");
            Write(BoldOff);
            if (!string.IsNullOrWhiteSpace(order.CounterpartName)) WriteLine($"Επωνυμία: {order.CounterpartName}");
            WriteLine($"ΑΦΜ: {order.CounterpartVatNumber}");
            if (!string.IsNullOrWhiteSpace(order.CounterpartTaxOffice)) WriteLine($"ΔΟΥ: {order.CounterpartTaxOffice}");
            if (!string.IsNullOrWhiteSpace(order.CounterpartMainActivity)) WriteLine($"Δραστηριότητα: {order.CounterpartMainActivity}");
            var customerAddress = string.Join(" ", new[] { order.CounterpartStreet, order.CounterpartStreetNumber, order.CounterpartPostalCode, order.CounterpartCity }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(customerAddress)) WriteLine($"Διεύθυνση: {customerAddress}");
        }
        Write(AlignLeft);
        WriteLine(new string('-', width));

        foreach (var item in items)
        {
            WriteEuroLine(ThreeColumns(
                $"{item.Quantity} x {item.ItemName}",
                $"{item.VatPercent:0.#}%",
                Amount(item.LineTotal),
                width));
        }

        WriteLine(new string('-', width));
        Write(BoldOn);
        WriteEuroLine(TwoColumns("ΣΥΝΟΛΟ", Amount(total), width));
        Write(BoldOff);
        WriteLine(new string('-', width));

        if (mark.HasValue)
        {
            Write(AlignCenter);
            Write(BoldOn);
            WriteLine($"ΜΑΡΚ: {mark.Value}");
            Write(BoldOff);
        }

        if (!string.IsNullOrWhiteSpace(qrCode))
        {
            WriteLine();
            Write(AlignCenter);
            WriteLine("QR ΑΑΔΕ");
            var qrData = Encoding.UTF8.GetBytes(qrCode.Trim());
            if (qrData.Length <= 65532)
            {
                // Model 2
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 });
                // Module size = 6
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x06 });
                // Error correction = M (48)
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31 });
                // Store QR data
                var storeLen = qrData.Length + 3;
                var pL = (byte)(storeLen & 0xFF);
                var pH = (byte)((storeLen >> 8) & 0xFF);
                Write(new byte[] { 0x1D, 0x28, 0x6B, pL, pH, 0x31, 0x50, 0x30 });
                Write(qrData);
                // Print QR
                Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 });
            }
            else
            {
                _logger.LogWarning("Το QR code είναι πολύ μεγάλο για ESC/POS: {Length} bytes", qrData.Length);
                WriteLine(qrCode);
            }
            WriteLine();
        }

        Write(Feed4);
        Write(CutSequence);
        return ms.ToArray();
    }


    private byte[] BuildDayCloseTicket(PrinterStation station, DayCloseRecord record)
    {
        var rows = record.Employees;
        var (escT, enc) = ResolveCodepage(station.Codepage);
        var width = station.PaperWidthChars > 0 ? station.PaperWidthChars : 42;
        using var ms = new MemoryStream();

        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void WriteText(string s) { var b = enc.GetBytes(s); ms.Write(b, 0, b.Length); }
        void WriteLine(string s = "") { WriteText(s + "\n"); }

        void WriteEuroLine(string lineEndingInEuro)
        {
            if (lineEndingInEuro.Length == 0 || lineEndingInEuro[^1] != '€')
            {
                WriteLine(lineEndingInEuro);
                return;
            }
            WriteText(lineEndingInEuro[..^1]);
            Write(SelectCp1253);
            var eb = Cp1253Encoding.GetBytes("€");
            ms.Write(eb, 0, eb.Length);
            Write(new byte[] { 0x1B, 0x74, escT });
            WriteText("\n");
        }

        Write(Init);
        Write(new byte[] { 0x1B, 0x74, escT });

        Write(AlignCenter);
        Write(DoubleSizeOn);
        Write(BoldOn);
        WriteLine(rows.Count == 1 ? $"ΚΛΕΙΣΙΜΟ - {rows[0].EmployeeName.ToUpperInvariant()}" : "ΚΛΕΙΣΙΜΟ ΤΑΜΕΙΟΥ");
        Write(DoubleSizeOff);
        Write(BoldOff);
        WriteLine(record.ClosedAt.ToString("dd/MM/yyyy HH:mm"));
        WriteLine($"Από: {record.ClosedByName}");
        Write(AlignLeft);
        WriteLine(new string('-', width));

        foreach (var row in rows)
        {
            Write(BoldOn);
            WriteEuroLine(TwoColumns(row.EmployeeName, Amount(row.GrandTotal), width));
            Write(BoldOff);
            WriteLine($"   Μετρητά: {row.CashTotal:0.00}€   Κάρτα: {row.CardTotal:0.00}€   ({row.OrderCount} παραγγ.)");
        }

        WriteLine(new string('-', width));
        WriteEuroLine(TwoColumns("Σύνολο Μετρητών:", Amount(rows.Sum(r => r.CashTotal)), width));
        WriteEuroLine(TwoColumns("Σύνολο Καρτών:", Amount(rows.Sum(r => r.CardTotal)), width));
        Write(BoldOn);
        WriteEuroLine(TwoColumns("ΣΥΝΟΛΟ", Amount(rows.Sum(r => r.GrandTotal)), width));
        Write(BoldOff);
        WriteLine(new string('-', width));
        WriteLine($"Εκτυπώθηκε: {DateTime.Now:HH:mm:ss dd/MM/yyyy}");

        Write(Feed4);
        Write(CutSequence);

        return ms.ToArray();
    }
}
