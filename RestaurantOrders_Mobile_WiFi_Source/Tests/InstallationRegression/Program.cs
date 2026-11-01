using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RestaurantOrders.Infrastructure;

// Used only by the Windows installer smoke test on its disposable fresh database.
if (args is ["--installed-database", var installedDatabase, var operation])
{
    using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = installedDatabase, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
    database.Open();
    using var command = database.CreateCommand();
    if (operation == "write")
    {
        command.CommandText = "UPDATE Users SET FirstName='Installer smoke sentinel' WHERE Id=(SELECT MIN(Id) FROM Users)";
        if (command.ExecuteNonQuery() != 1) throw new Exception("Missing initial admin row.");
    }
    else if (operation == "check")
    {
        command.CommandText = "SELECT FirstName FROM Users ORDER BY Id LIMIT 1";
        if (command.ExecuteScalar()?.ToString() != "Installer smoke sentinel") throw new Exception("Installed database marker was not preserved.");
    }
    else throw new ArgumentException("Unknown smoke-test operation.");
    return;
}

var assertions = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
var temporary = Path.Combine(Path.GetTempPath(), "RestaurantOrders-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var development = WebApplication.CreateBuilder(Array.Empty<string>());
    development.Configuration["ConnectionStrings:Default"] = "Data Source=development.db";
    Check(InstalledApplication.Configure(development) is null, "Development does not activate installed mode");
    Check(development.Configuration.GetConnectionString("Default") == "Data Source=development.db", "Development database remains unchanged");
    Check(!InstalledApplication.HandleImport(Array.Empty<string>()), "Normal startup is not an import operation");

    var dataDirectory = Path.Combine(temporary, "persistent data");
    Directory.CreateDirectory(dataDirectory);
    File.WriteAllText(Path.Combine(dataDirectory, "appsettings.Installed.json"), """{"Urls":"http://127.0.0.1:54021"}""");
    string[] installedArgs = { "--Installation:Enabled=true", "--Installation:DataDirectory=" + dataDirectory };
    var first = WebApplication.CreateBuilder(installedArgs);
    Check(InstalledApplication.Configure(first) == dataDirectory, "Explicit persistent data path selected");
    var connection = new SqliteConnectionStringBuilder(first.Configuration.GetConnectionString("Default"));
    Check(connection.DataSource == Path.Combine(dataDirectory, "restaurant.db"), "Installed database is outside the application directory");
    Check(first.Configuration["Urls"] == "http://127.0.0.1:54021", "Persistent installed URL settings loaded");
    string protectedText;
    await using (var app = first.Build())
        protectedText = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("installer-test").Protect("persistent authentication test");
    Check(Directory.GetFiles(Path.Combine(dataDirectory, "DataProtection-Keys")).Length > 0, "Authentication keys persisted");
    var next = WebApplication.CreateBuilder(installedArgs);
    InstalledApplication.Configure(next);
    await using (var app = next.Build())
        Check(app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("installer-test").Unprotect(protectedText) == "persistent authentication test", "Authentication keys survive application restart/update");

    var sourcePath = Path.Combine(temporary, "existing.db");
    using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Pooling = false }.ToString());
    source.Open();
    using (var command = source.CreateCommand())
    {
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE Users(Id INTEGER PRIMARY KEY, FirstName TEXT); CREATE TABLE Orders(Id INTEGER PRIMARY KEY); CREATE TABLE MenuItems(Id INTEGER PRIMARY KEY); INSERT INTO Users VALUES(1,'Existing customer');";
        command.ExecuteNonQuery();
    }
    Check(File.Exists(sourcePath + "-wal"), "Import fixture includes a live SQLite WAL");
    var destination = Path.Combine(temporary, "imported");
    InstalledApplication.ImportDatabase(sourcePath, destination);
    var targetPath = Path.Combine(destination, "restaurant.db");
    using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = targetPath, Pooling = false }.ToString()))
    {
        target.Open();
        using var command = target.CreateCommand();
        command.CommandText = "SELECT FirstName FROM Users WHERE Id=1";
        Check(command.ExecuteScalar()?.ToString() == "Existing customer", "Import includes committed WAL data");
    }
    using (var command = source.CreateCommand())
    {
        command.CommandText = "SELECT FirstName FROM Users WHERE Id=1";
        Check(command.ExecuteScalar()?.ToString() == "Existing customer", "Original database left unchanged");
    }
    var originalTargetHash = SHA256.HashData(File.ReadAllBytes(targetPath));
    var rejectedOverwrite = false;
    try { InstalledApplication.ImportDatabase(sourcePath, destination); }
    catch (IOException) { rejectedOverwrite = true; }
    Check(rejectedOverwrite, "Existing installed database cannot be overwritten");
    Check(SHA256.HashData(File.ReadAllBytes(targetPath)).SequenceEqual(originalTargetHash), "Rejected import preserves installed database bytes");
    var invalidSource = Path.Combine(temporary, "unrelated.db");
    using (var unrelated = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = invalidSource, Pooling = false }.ToString()))
    {
        unrelated.Open();
        using var command = unrelated.CreateCommand(); command.CommandText = "CREATE TABLE Unrelated(Id INTEGER)"; command.ExecuteNonQuery();
    }
    var invalidDestination = Path.Combine(temporary, "invalid-destination");
    var rejectedInvalid = false;
    try { InstalledApplication.ImportDatabase(invalidSource, invalidDestination); }
    catch (InvalidDataException) { rejectedInvalid = true; }
    Check(rejectedInvalid && !File.Exists(Path.Combine(invalidDestination, "restaurant.db")), "Unrelated SQLite databases are rejected before installation");
    var missingDestination = Path.Combine(temporary, "missing-destination");
    var rejectedMissing = false;
    try { InstalledApplication.ImportDatabase(Path.Combine(temporary, "does-not-exist.db"), missingDestination); }
    catch (SqliteException) { rejectedMissing = true; }
    Check(rejectedMissing && !File.Exists(Path.Combine(missingDestination, "restaurant.db")), "A missing source cannot silently create an empty installed database");
    Console.WriteLine($"PASS: {assertions} installation/data preservation assertions.");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(temporary, recursive: true);
}
