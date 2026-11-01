using System.Data;
using System.Text;
using ExcelDataReader;

namespace RestaurantOrders.Services;

public static class ExcelImportService
{
    static ExcelImportService()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // ήδη καταχωρημένο, δεν πειράζει
        }
    }

    /// <summary>
    /// Διαβάζει ένα .xlsx αρχείο και επιστρέφει μία λίστα γραμμών, όπου κάθε γραμμή
    /// είναι ένα dictionary επικεφαλίδα-στήλης -> τιμή κελιού (η πρώτη γραμμή του
    /// αρχείου θεωρείται επικεφαλίδες).
    /// </summary>
    public static List<Dictionary<string, string>> ReadRows(Stream fileStream)
    {
        using var reader = ExcelReaderFactory.CreateReader(fileStream);
        var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration
            {
                UseHeaderRow = true
            }
        });

        var rows = new List<Dictionary<string, string>>();
        if (dataSet.Tables.Count == 0) return rows;

        var table = dataSet.Tables[0];
        foreach (DataRow row in table.Rows)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataColumn col in table.Columns)
            {
                dict[col.ColumnName.Trim()] = row[col]?.ToString()?.Trim() ?? "";
            }
            rows.Add(dict);
        }
        return rows;
    }

    /// <summary>
    /// Ψάχνει σε μια γραμμή για την πρώτη στήλη που ταιριάζει (χωρίς να πειράζει η
    /// διαφορά κεφαλαίων/πεζών) με οποιοδήποτε από τα δοθέντα ονόματα-υποψήφια.
    /// </summary>
    public static string GetField(Dictionary<string, string> row, params string[] candidateHeaders)
    {
        foreach (var candidate in candidateHeaders)
        {
            if (row.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return "";
    }
}
