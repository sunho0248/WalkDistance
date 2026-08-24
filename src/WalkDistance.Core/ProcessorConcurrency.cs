using System.Buffers.Binary;
using System.Runtime.InteropServices;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("WalkDistance.Core.Tests")]

namespace WalkDistance.Core;

internal static class PhysicalCoreDetector
{
    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    internal static int GetWorkerCount() =>
        ResolveWorkerCount(Environment.ProcessorCount, ReadTopology());

    internal static int ResolveWorkerCount(int availableProcessorCount, byte[]? topology)
    {
        int available = Math.Max(1, availableProcessorCount);
        int physicalCores = topology is null ? 0 : CountPhysicalCores(topology);
        return physicalCores > 0 ? Math.Min(physicalCores, available) : available;
    }

    internal static int CountPhysicalCores(ReadOnlySpan<byte> topology)
    {
        int count = 0;
        int offset = 0;
        while (offset < topology.Length)
        {
            var remaining = topology[offset..];
            if (remaining.Length < 8)
            {
                return 0;
            }

            int relationship = BinaryPrimitives.ReadInt32LittleEndian(remaining);
            int size = BinaryPrimitives.ReadInt32LittleEndian(remaining[4..]);
            if (size < 8 || size > remaining.Length)
            {
                return 0;
            }

            if (relationship == RelationProcessorCore)
            {
                count++;
            }
            offset += size;
        }
        return count;
    }

    private static byte[]? ReadTopology()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            uint length = 0;
            if (GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length) ||
                Marshal.GetLastWin32Error() != ErrorInsufficientBuffer ||
                length < 8 || length > Array.MaxLength)
            {
                return null;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var topology = new byte[(int)length];
                uint returnedLength = (uint)topology.Length;
                if (GetLogicalProcessorInformationEx(
                        RelationProcessorCore, topology, ref returnedLength))
                {
                    return returnedLength >= 8 && returnedLength <= topology.Length
                        ? topology[..(int)returnedLength]
                        : null;
                }

                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer ||
                    returnedLength <= topology.Length || returnedLength > Array.MaxLength)
                {
                    return null;
                }
                length = returnedLength;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (BadImageFormatException)
        {
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        [Out] byte[]? buffer,
        ref uint returnedLength);
}

internal sealed class FieldConcurrencyBudget
{
    private readonly SemaphoreSlim _slots;

    internal int Capacity { get; }

    internal FieldConcurrencyBudget(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _slots = new SemaphoreSlim(capacity, capacity);
    }

    internal IDisposable Enter(CancellationToken cancellationToken = default)
    {
        _slots.Wait(cancellationToken);
        return new Lease(this);
    }

    internal bool TryEnter(out IDisposable? lease)
    {
        if (!_slots.Wait(0))
        {
            lease = null;
            return false;
        }

        lease = new Lease(this);
        return true;
    }

    private void Release() => _slots.Release();

    private sealed class Lease(FieldConcurrencyBudget owner) : IDisposable
    {
        private FieldConcurrencyBudget? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
