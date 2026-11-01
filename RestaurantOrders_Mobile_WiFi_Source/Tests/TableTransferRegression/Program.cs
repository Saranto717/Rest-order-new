using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using RestaurantOrders.Components.Pages.Waiter;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using RestaurantOrders.Services;

int assertions = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }

// Whole party moves to a free table using Transfer=2; the original legal document is retained.
await using (var f = await Fixture.Create()) {
    var result = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    Check(result.Success, "Whole-table transfer: " + result.Message);
    using var db = f.Factory.CreateDbContext();
    var source = await db.Orders.Include(o => o.Items).SingleAsync(o => o.Id == f.SourceId);
    Check(source.RestaurantTableId == f.TargetTableId && source.Status == OrderStatus.Open, "Party/order identity moved to free destination");
    Check(source.Items.Single().Quantity == 2 && source.Items.Single().IsPrinted, "Two beers and kitchen state preserved");
    Check(!await db.Orders.AnyAsync(o => o.RestaurantTableId == f.SourceTableId && o.Status == OrderStatus.Open), "Source table has no open order");
    Check((await db.FiscalOrderDocuments.SingleAsync()).Mark == 100 && !(await db.FiscalOrderDocuments.SingleAsync()).IsCancelled, "Original MARK is retained, not cancelled");
    Check(f.Api.Transfers == 1 && f.Api.NewDocuments == 0, "One atomic provider transfer, no duplicate/cancellation order ticket");
    Check(!f.Api.Tables["T1"].Open && f.Api.Tables["T2"].Marks.SetEquals(new long[] { 100 }), "Provider source closed and ticket at destination");
    var request = f.Api.Requests.Single(r => r.Path.EndsWith("SetState") && r.Json!["actionKind"]!.GetValue<int>() == 2);
    Check(request.Json!["pointOfService"]!.GetValue<string>() == "T1" && request.Json["targetPointOfService"]!.GetValue<string>() == "T2", "Documented SetState fields");
    Check(request.Json["companyBranchCode"]!.GetValue<string>() == "0" && request.Version == "3.0", "Parochos branch and X-Version");
    Check((await db.TableTransfers.SingleAsync()).Stage == TableTransferStage.Completed, "Durable completed journal");
    var repeat = await f.Service.RecheckAsync(result.TransferId!);
    Check(repeat.Success && f.Api.Transfers == 1, "Completed recheck is idempotent");
}

// Merge keeps item IDs, fiscal-document mappings and print batches; it is not a payment.
await using (var f = await Fixture.Create(occupied: true)) {
    var result = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    Check(result.Success, "Merge: " + result.Message);
    using var db = f.Factory.CreateDbContext();
    Check((await db.Orders.FindAsync(f.SourceId))!.Status == OrderStatus.Transferred, "Merged order is marked transferred, not paid/closed");
    var target = await db.Orders.Include(o => o.Items).ThenInclude(i => i.MenuItem).ThenInclude(m => m!.MenuCategory)
        .Include(o => o.RestaurantTable).SingleAsync(o => o.Id == f.TargetOrderId);
    Check(target.Items.Count == 2 && target.Items.Sum(i => i.Quantity) == 3, "All original lines/quantities merged without coalescing");
    Check((await db.FiscalOrderDocuments.Where(x => x.OrderId == target.Id).Select(x => x.Mark).ToListAsync()).Order().SequenceEqual(new long?[] { 100, 200 }), "Both original tickets belong to the merged order");
    Check(await db.FiscalOrderDocumentItems.CountAsync() == 2 && target.Items.Select(i => i.Id).ToHashSet().SetEquals(f.ItemIds), "Fiscal item IDs and mappings preserved");
    var batches = await db.OrderPrintBatches.Where(b => b.OrderId == target.Id).ToListAsync();
    Check(batches.Count == 2 && batches.Select(b => b.Number).Distinct().Count() == 2, "Print batches merged with unique sequence numbers");
    Check(!await db.OrderPayments.AnyAsync(), "Move/merge does not create a payment");
    Check(f.Api.Tables["T2"].Balance == 15m && f.Api.Tables["T2"].Marks.SetEquals(new long[] { 100, 200 }), "Provider balances and MARKs merged");
    // Receipt after merge must correlate both transferred original forms.
    var fiscal = f.Fiscal;
    var issued = await fiscal.IssueSelectedItemsPaymentAsync(db, target, target.Items, PaymentMethod.Cash, FiscalDocumentKind.Receipt);
    Check(issued.Success, "Receipt after merge: " + issued.ErrorMessage);
    var invoice = f.Api.Requests.Last(r => r.Path.EndsWith("/api/send")).Json!["source"]!["invoice"]!;
    var correlated = invoice["correlatedInvoices"]!.AsArray().Select(x => x!["mark"]!.GetValue<long>()).ToHashSet();
    Check(correlated.SetEquals(new long[] { 100, 200 }), "Post-merge receipt correlates every original table's order form");
    Check(invoice["invoiceHeader"]!["PointOfService"]!.GetValue<string>()=="T2", "Receipt after merge is issued against the new table");
    Check(f.Api.Requests.Count(r=>r.Path.EndsWith("ConnectDocuments"))==2, "Each original form is linked to the receipt after merge");
    var audit=await db.TableTransfers.SingleAsync();
    Check(JsonSerializer.Deserialize<int[]>(audit.SourceItemIdsJson)!.Length==1,"Merge journal preserves original item IDs for audit");
}

// A provider can leave an empty open source; close only after confirming it has no documents/balance.
await using (var f = await Fixture.Create()) {
    f.Api.KeepEmptySourceOpen = true;
    var result = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    Check(result.Success && f.Api.Closes == 1 && !f.Api.Tables["T1"].Open, "Explicitly close empty source if required");
}

await using (var f=await Fixture.Create()) {
    f.Api.KeepEmptySourceOpen=true; f.Api.RefuseClose=true;
    var first=await f.Service.MoveAllAsync(f.SourceId,f.TargetTableId,f.UserId);
    Check(first.Pending && !first.Success,"An unclosed source prevents reporting a completed move");
    f.Api.RefuseClose=false;
    var recovered=await f.NewService().RecheckAsync(first.TransferId!);
    Check(recovered.Success && f.Api.Transfers==1 && f.Api.Closes==2,"Source close can be reconciled without repeating the transfer");
}

// A timeout can mean the provider applied the move. New service instance recovers without resending it.
await using (var f = await Fixture.Create()) {
    f.Api.Mode = "timeout-applied";
    var first = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    Check(!first.Success && first.Pending, "Lost response is pending, not a local success");
    using (var db = f.Factory.CreateDbContext()) {
        Check((await db.Orders.FindAsync(f.SourceId))!.RestaurantTableId == f.SourceTableId, "Local order remains at source until verified");
        var item = await db.OrderItems.FirstAsync(i => i.OrderId == f.SourceId); item.Quantity++;
        bool blocked = false; try { await db.SaveChangesAsync(); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "Pending transfer locks edits");
    }
    var recovered = await f.NewService().RecheckAsync(first.TransferId!);
    Check(recovered.Success && f.Api.Transfers == 1, "Process-restart reconciliation does not resend Transfer=2");
}

await using (var f = await Fixture.Create()) {
    f.Api.Mode = "timeout-unapplied";
    var first = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    var second = await f.NewService().RecheckAsync(first.TransferId!);
    Check(first.Pending && second.Pending && f.Api.Transfers == 1, "Uncertain unapplied operation is not blindly retried");
    using (var db = f.Factory.CreateDbContext()) {
        var source = await db.Orders.FindAsync(f.SourceId);
        var preparation = await f.Fiscal.PrepareCardPaymentAsync(db, source!, FiscalDocumentKind.Receipt);
        Check(!preparation.Success && !f.Api.Requests.Any(r => r.Path.EndsWith("requestPayment")), "Pending transfer blocks provider signatures before HTTP");
        var draft = new Order { RestaurantTableId = f.TargetTableId, WaiterUserId = f.UserId }; db.Add(draft);
        bool blocked = false; try { await db.SaveChangesAsync(); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "Pending destination cannot be occupied by a new order");
    }
    f.Api.ApplyTransfer();
    var recovered = await f.NewService().RecheckAsync(first.TransferId!);
    Check(recovered.Success && f.Api.Transfers == 1, "Delayed provider completion reconciles safely");
}

await using (var f = await Fixture.Create()) {
    f.Api.Mode = "reject";
    var result = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    Check(!result.Success && !result.Pending, "Definitive provider rejection is reported");
    using var db = f.Factory.CreateDbContext();
    Check((await db.Orders.FindAsync(f.SourceId))!.RestaurantTableId == f.SourceTableId && (await db.TableTransfers.SingleAsync()).Stage == TableTransferStage.Rejected, "Rejection keeps source and releases journal lock");
    var item = await db.OrderItems.FirstAsync(); item.Notes = "still editable"; await db.SaveChangesAsync();
    Check(f.Api.NewDocuments == 0, "Rejection does not cancel or reissue documents");
}

// Preflight cannot transfer unrelated provider tickets.
foreach (var name in new[] { "T1", "T2" }) await using (var f = await Fixture.Create()) {
    f.Api.Tables[name].Open = true; f.Api.Tables[name].Marks.Add(999); f.Api.Tables[name].Balance += 1;
    var result = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    Check(!result.Success && !result.Pending && f.Api.Transfers == 0, "Unknown provider ticket on " + name + " blocks transfer");
}

await using(var f=await Fixture.Create()) {
    f.Api.MalformedDetails=true;
    var result=await f.Service.MoveAllAsync(f.SourceId,f.TargetTableId,f.UserId);
    Check(!result.Success && !result.Pending && f.Api.Transfers==0,"Malformed provider details cannot prove an empty table");
}

// Existing partial/approved/pending fiscal activity must not be merged or moved.
foreach (var kind in new[] { "source-paid", "target-paid", "source-signature", "target-failed" }) await using (var f = await Fixture.Create(occupied: true)) {
    using (var db = f.Factory.CreateDbContext()) {
        var o = await db.Orders.FindAsync(kind.StartsWith("target") ? f.TargetOrderId : f.SourceId);
        if (kind.EndsWith("paid")) db.OrderPayments.Add(new OrderPayment { OrderId = o!.Id, Amount = 1m, FiscalStatus = FiscalStatus.Issued });
        if (kind.EndsWith("signature")) o!.ParochosPaymentSignature = "held-token";
        if (kind.EndsWith("failed")) o!.FiscalStatus = FiscalStatus.Failed;
        await db.SaveChangesAsync();
    }
    var result = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    Check(!result.Success && f.Api.Transfers == 0, kind + " blocks move before provider calls");
}

await using (var f = await Fixture.Create(localOnly: true)) {
    var result = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    Check(result.Success && f.Api.Requests.Count == 0, "Local-only transfer does not call Parochos");
}

await using (var f = await Fixture.Create(emptyDraft: true)) {
    var result = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    using var db = f.Factory.CreateDbContext();
    Check(result.Success && await db.Orders.CountAsync(o => o.RestaurantTableId == f.TargetTableId && o.Status == OrderStatus.Open) == 1, "Previously visited empty destination does not create duplicate open orders");
}

await using (var f = await Fixture.Create()) {
    f.Api.Mode = "timeout-applied";
    var first = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    using (var db = f.Factory.CreateDbContext()) { (await db.CompanySettings.FirstAsync()).ParochosUseProduction = true; await db.SaveChangesAsync(); }
    var count = f.Api.Requests.Count;
    var changed = await f.NewService().RecheckAsync(first.TransferId!);
    Check(changed.Pending && f.Api.Requests.Count == count, "Reconciliation cannot use a different provider account/environment");
}

// Concurrent clicks and writes are serialized while the provider response is in flight.
await using (var f = await Fixture.Create()) {
    f.Api.Mode = "pause";
    var first = f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    await f.Api.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var duplicate = f.NewService().MoveAllAsync(f.SourceId, f.ThirdTableId, f.UserId);
    Check(!duplicate.IsCompleted, "Second transfer of same party waits on table coordinator");
    using (var db = f.Factory.CreateDbContext()) {
        var other = new Order { RestaurantTableId = f.ThirdTableId, WaiterUserId = f.UserId }; db.Add(other); await db.SaveChangesAsync();
        Check(other.Id > 0, "Unrelated table remains usable while transfer is pending");
    }
    f.Api.Release.TrySetResult(true);
    var result = await first; var repeated = await duplicate;
    Check(result.Success && !repeated.Success && f.Api.Transfers == 1, "Concurrent stale transfer cannot move a different table or duplicate provider request");
}

// A failed local final write is retried from the confirmed journal, not from Transfer=2.
await using (var f = await Fixture.Create()) {
    f.Factory.FailCompletion = true;
    var first = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    Check(first.Pending && !first.Success, "Local final-write failure remains pending");
    using (var db = f.Factory.CreateDbContext()) {
        Check((await db.Orders.FindAsync(f.SourceId))!.RestaurantTableId == f.SourceTableId &&
            (await db.TableTransfers.SingleAsync()).Stage == TableTransferStage.ProviderConfirmed,
            "Failed local commit rolls back order changes and keeps provider confirmation");
    }
    var resumed = await f.NewService().RecheckAsync(first.TransferId!);
    Check(resumed.Success && f.Api.Transfers == 1, "Confirmed journal recovers local finalization without duplicate provider command");
}

// The details parser must select the requested table, not another first row.
await using (var f = await Fixture.Create()) {
    f.Api.IncludeUnrelatedRow = true;
    var result = await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    Check(result.Success && f.Api.Transfers == 1, "Provider response with unrelated first row is matched by table name");
}

// UI path: table picker supports occupied destinations, navigation follows the moved party,
// and a previously opened source page cannot edit the transferred order.
await using (var f = await Fixture.Create(occupied: true)) {
    var page = new TableOrder(); var nav = new TestNavigation();
    SetProperty(page, "DbFactory", f.Factory); SetProperty(page, "Transfers", f.Service); SetProperty(page, "Nav", nav); SetProperty(page, "TableId", f.SourceTableId);
    SetField(page, "currentUserId", f.UserId);
    using (var db = f.Factory.CreateDbContext()) SetField(page, "order", (await db.Orders.FindAsync(f.SourceId))!);
    await InvokeAsync(page, "BeginTableTransfer");
    Check(GetField<HashSet<int>>(page, "occupiedTransferTargets").Contains(f.TargetTableId) &&
        !GetField<List<RestaurantTable>>(page,"tableTransferTargets").Any(t=>t.Id==f.SourceTableId), "UI picker distinguishes merge destination and excludes current table");
    SetField(page, "tableTransferTargetId", f.TargetTableId);
    await InvokeAsync(page, "ConfirmTableTransfer");
    Check(nav.LastUri == $"/waiter/table/{f.TargetTableId}", "UI navigates to merged party after success");
    nav.LastUri = null;
    using (var db = f.Factory.CreateDbContext()) await InvokeAsync(page, "SelectItem", await db.MenuItems.FirstAsync());
    Check(nav.LastUri == "/waiter", "Stale source page is redirected before adding items");
}

await using (var f = await Fixture.Create()) {
    f.Api.Mode = "timeout-unapplied";
    await f.Service.MoveAllAsync(f.SourceId, f.TargetTableId, f.UserId);
    var page = new TableOrder(); SetProperty(page, "DbFactory", f.Factory); SetProperty(page,"TableId", f.TargetTableId);
    SetField(page,"currentUserId",f.UserId);
    await InvokeAsync(page,"LoadAsync");
    Check(GetField<bool>(page,"showTableTransferModal"),"Pending transfer recovery UI opens on destination after reload");
    using var db=f.Factory.CreateDbContext();
    Check(!await db.Orders.AnyAsync(o=>o.RestaurantTableId==f.TargetTableId),"Loading pending empty target does not create an unlocked draft order");
}

await using (var f = await Fixture.Create()) {
    var result=await f.Service.MoveAllAsync(f.SourceId,f.TargetTableId,0);
    Check(!result.Success && f.Api.Transfers==0,"Move requires an active authenticated operator");
}

// Schema is added to an existing installation without replacing its customer database.
await using (var f = await Fixture.Create(localOnly: true)) {
    using var db = f.Factory.CreateDbContext();
    db.Database.ExecuteSqlRaw("DROP TABLE TableTransfers");
    DataSeeder.Seed(db);
    Check(await db.TableTransfers.CountAsync() == 0 && await db.OrderItems.CountAsync() == 1, "Legacy schema upgrade preserves restaurant data");
}
Console.WriteLine($"PASS: {assertions} table-transfer assertions. All Parochos requests intercepted; no real provider mutation.");

static void SetProperty(object obj,string name,object value)=>obj.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.SetValue(obj,value);
static T GetField<T>(object obj,string name)=>(T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(obj)!;
static void SetField(object obj,string name,object value)=>obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(obj,value);
static async Task InvokeAsync(object obj,string name,params object[] args)=>await (Task)obj.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(obj,args)!;
sealed class TestNavigation : NavigationManager {
    public string? LastUri;
    public TestNavigation(){Initialize("http://localhost/","http://localhost/");}
    protected override void NavigateToCore(string uri,bool forceLoad)=>LastUri=uri;
}

sealed class DbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext> {
    public bool FailCompletion;
    public AppDbContext CreateDbContext() => new FailureContext(options,this);
    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
}
sealed class FailureContext(DbContextOptions<AppDbContext> options,DbFactory factory):AppDbContext(options) {
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,CancellationToken cancellationToken=default) {
        if(factory.FailCompletion && ChangeTracker.Entries<TableTransfer>().Any(e=>e.Entity.Stage==TableTransferStage.Completed)) {
            factory.FailCompletion=false;
            throw new DbUpdateException("Simulated local final-write failure");
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess,cancellationToken);
    }
}
sealed class HttpFactory(ProviderApi api) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(api, false); }
sealed class RemoteTable { public bool Open; public decimal Balance; public HashSet<long> Marks = new(); }
sealed record Seen(string Path, JsonNode? Json, string? Version);
sealed class ProviderApi : HttpMessageHandler {
    public Dictionary<string,RemoteTable> Tables = new() { ["T1"] = new(), ["T2"] = new() };
    public List<Seen> Requests = new(); public int Transfers, Closes, NewDocuments;
    public string Mode = "normal"; public bool KeepEmptySourceOpen; public bool IncludeUnrelatedRow; public bool RefuseClose; public bool MalformedDetails;
    public TaskCompletionSource<bool> Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void ApplyTransfer() {
        Tables["T2"].Open = true; Tables["T2"].Balance += Tables["T1"].Balance;
        Tables["T2"].Marks.UnionWith(Tables["T1"].Marks); Tables["T1"].Marks.Clear(); Tables["T1"].Balance = 0; Tables["T1"].Open = KeepEmptySourceOpen;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var path = request.RequestUri!.AbsolutePath;
        var json = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
        Requests.Add(new(path, json, request.Headers.TryGetValues("X-Version", out var v) ? v.Single() : null));
        if (path.Contains("loginToSubscription")) return Reply(new { jwt="fake", jwtExpiration=DateTime.UtcNow.AddHours(1), jwtRefreshToken="fake", jwtRefreshTokenExpiration=DateTime.UtcNow.AddHours(2), url1="https://fake-provider.invalid" });
        if (path.EndsWith("ListDetails")) {
            if(MalformedDetails) return Reply(new {success=true});
            var name = json!["pointOfService"]!.GetValue<string>(); var t = Tables[name];
            var rows = new List<object>();
            if (IncludeUnrelatedRow && t.Open) rows.Add(new { pointOfService="T9", balance=999m, refNumber=9, details=new[]{ new { mark=999L,invoiceType="8.6" } } });
            if (t.Open) rows.Add(new { pointOfService=name, balance=t.Balance, refNumber=1, details=t.Marks.Select(m => new { mark=m, invoiceType="8.6", totalAmount=t.Balance, transactionAmount=t.Balance, orderFormStatus=0 }).ToArray() });
            return Reply(new { success=true, data=rows });
        }
        if (path.EndsWith("SetState")) {
            var action = json!["actionKind"]!.GetValue<int>();
            if (action == 2) {
                Transfers++;
                if (Mode == "reject") return Reply(new { success=false, errorCategory="BED", errorCode="test-rejected", errorMessage="Rejected by provider" });
                if (Mode == "pause") { Arrived.TrySetResult(true); await Release.Task; }
                if (Mode != "timeout-unapplied") ApplyTransfer();
                if (Mode.StartsWith("timeout")) throw new HttpRequestException("Simulated lost response");
            } else if (action == 1) { Closes++; if(RefuseClose)return Reply(new {success=false,errorCategory="BED",errorMessage="Close rejected"}); Tables[json["pointOfService"]!.GetValue<string>()].Open=false; }
            return Reply(new { success=true });
        }
        if (path.EndsWith("/api/send")) { NewDocuments++; return Reply(new { status=1, processId="receipt-process", signing=new { mark=10000+NewDocuments, uid="receipt-uid" } }); }
        if (path.EndsWith("ConnectDocuments")) return Reply(new { status=1, connectedCount=1 });
        if (path.EndsWith("AssignDocument")) return Reply(new { success=true });
        throw new Exception("Unexpected fake provider route: " + path);
    }
    static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK) { Content=new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8,"application/json") };
}
sealed class Fixture : IAsyncDisposable {
    public required DbFactory Factory; public required ProviderApi Api; public required ParochosService Parochos;
    public required string DirectoryPath; public int SourceId, SourceTableId, TargetTableId, ThirdTableId, TargetOrderId, UserId; public HashSet<int> ItemIds = new();
    public TableTransferService Service => NewService();
    public FiscalDocumentService Fiscal => new(Parochos, NullLogger<FiscalDocumentService>.Instance);
    public TableTransferService NewService() => new(Factory,Parochos);
    public static async Task<Fixture> Create(bool occupied=false, bool localOnly=false, bool emptyDraft=false) {
        var dir=Path.Combine(Path.GetTempPath(),"table-transfer-"+Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(dir);
        var factory=new DbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(new SqliteConnectionStringBuilder { DataSource=Path.Combine(dir,"test.db"), Pooling=false }.ToString()).Options);
        var api=new ProviderApi(); var parochos=new ParochosService(new HttpFactory(api),NullLogger<ParochosService>.Instance);
        using var db=factory.CreateDbContext(); await db.Database.EnsureCreatedAsync();
        var t1=new RestaurantTable { Name="T1" };var t2=new RestaurantTable { Name="T2" };var t3=new RestaurantTable { Name="T3" };
        var user=new User { FirstName="Test",Role=Role.Waiter };var menu=new MenuItem { Name="Beer",Price=5m,MenuCategory=new MenuCategory { Name="Drinks",VatPercent=24 } };
        db.AddRange(t1,t2,t3,user,menu); db.Add(new CompanySettings { UseParochosProvider=!localOnly,IssueOrderFormsToAade=!localOnly,VatNumber="000000000",ParochosSubscriptionKey="fake",ParochosUsername="fake",ParochosPassword="fake",OrderFormInvoiceType="8.6" });await db.SaveChangesAsync();
        var source=new Order { RestaurantTableId=t1.Id,WaiterUserId=user.Id,Items=new() { new OrderItem { MenuItemId=menu.Id,Quantity=2,IsPrinted=true } } }; db.Add(source);await db.SaveChangesAsync();
        var batch=new OrderPrintBatch { OrderId=source.Id,Number=1 };db.Add(batch);await db.SaveChangesAsync();source.Items[0].OrderPrintBatchId=batch.Id;
        if (!localOnly) { db.Add(new FiscalOrderDocument { OrderId=source.Id,Mark=100,Number=1,SeriesName="FORM",ExternalSystemId="source-form",ProcessId="source-process",Items=new() { new FiscalOrderDocumentItem { OrderItemId=source.Items[0].Id,Quantity=2 } } });api.Tables["T1"].Open=true;api.Tables["T1"].Balance=10;api.Tables["T1"].Marks.Add(100); }
        int targetId=0;
        if (occupied || emptyDraft) {
            var target=new Order { RestaurantTableId=t2.Id,WaiterUserId=user.Id }; if (occupied) target.Items.Add(new OrderItem { MenuItemId=menu.Id,Quantity=1,IsPrinted=true });
            db.Add(target);await db.SaveChangesAsync();targetId=target.Id;
            if (occupied) {
                db.Add(new OrderPrintBatch { OrderId=target.Id,Number=1 });
                db.Add(new FiscalOrderDocument { OrderId=target.Id,Mark=200,Number=2,SeriesName="FORM",ExternalSystemId="target-form",ProcessId="target-process",Items=new() { new FiscalOrderDocumentItem { OrderItemId=target.Items[0].Id,Quantity=1 } } });
                api.Tables["T2"].Open=true;api.Tables["T2"].Balance=5;api.Tables["T2"].Marks.Add(200);
            }
        }
        await db.SaveChangesAsync();
        return new Fixture { Factory=factory,Api=api,Parochos=parochos,DirectoryPath=dir,SourceId=source.Id,SourceTableId=t1.Id,TargetTableId=t2.Id,ThirdTableId=t3.Id,TargetOrderId=targetId,UserId=user.Id,ItemIds=(await db.OrderItems.Select(i=>i.Id).ToListAsync()).ToHashSet() };
    }
    public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); System.IO.Directory.Delete(DirectoryPath,true);return ValueTask.CompletedTask; }
}
