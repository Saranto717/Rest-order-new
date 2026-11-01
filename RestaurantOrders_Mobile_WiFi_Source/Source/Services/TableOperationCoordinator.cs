using System.Collections.Concurrent;

namespace RestaurantOrders.Services;

/// <summary>Coordinates payment/printing operations with moves of the same tables.</summary>
public static class TableOperationCoordinator
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Gates = new();
    public static async Task<IDisposable> EnterAsync(params int[] tableIds)
    {
        var held = new List<SemaphoreSlim>();
        try
        {
            foreach (var id in tableIds.Distinct().Order())
            {
                var gate = Gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(); held.Add(gate);
            }
            return new Lease(held);
        }
        catch { foreach (var gate in held) gate.Release(); throw; }
    }
    private sealed class Lease(List<SemaphoreSlim> gates) : IDisposable
    {
        public void Dispose() { foreach (var gate in gates.AsEnumerable().Reverse()) gate.Release(); }
    }
}
