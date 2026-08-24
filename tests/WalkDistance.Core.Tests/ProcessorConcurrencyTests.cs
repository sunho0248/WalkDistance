using System.Buffers.Binary;

namespace WalkDistance.Core.Tests;

public class ProcessorConcurrencyTests
{
    [Fact]
    public void CountPhysicalCores_ParsesVariableSizedProcessorCoreRecords()
    {
        byte[] topology = Records((0, 16), (3, 24), (0, 12));

        Assert.Equal(2, PhysicalCoreDetector.CountPhysicalCores(topology));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    public void CountPhysicalCores_RejectsTruncatedOrInvalidRecords(int declaredSize)
    {
        byte[] topology = Records((0, 16));
        BinaryPrimitives.WriteInt32LittleEndian(topology.AsSpan(4), declaredSize);

        Assert.Equal(0, PhysicalCoreDetector.CountPhysicalCores(topology));
    }

    [Fact]
    public void ResolveWorkerCount_UsesPhysicalCoresAndClampsToProcessAvailability()
    {
        Assert.Equal(4, PhysicalCoreDetector.ResolveWorkerCount(8,
            Records((0, 8), (0, 8), (0, 8), (0, 8))));
        Assert.Equal(6, PhysicalCoreDetector.ResolveWorkerCount(6, Records((0, 8), (0, 8),
            (0, 8), (0, 8), (0, 8), (0, 8), (0, 8), (0, 8))));
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(0, 1)]
    public void ResolveWorkerCount_FallsBackSafelyWhenTopologyIsUnavailable(
        int availableProcessorCount,
        int expected)
    {
        Assert.Equal(expected,
            PhysicalCoreDetector.ResolveWorkerCount(availableProcessorCount, topology: null));
        Assert.Equal(expected,
            PhysicalCoreDetector.ResolveWorkerCount(availableProcessorCount, [0, 0, 0, 0]));
    }

    [Fact]
    public void SharedBudget_CapsIndependentCallersWithoutTiming()
    {
        var budget = new FieldConcurrencyBudget(2);
        using var mapSlot = budget.Enter();
        using var bodySlot = budget.Enter();

        Assert.Equal(2, budget.Capacity);
        Assert.False(budget.TryEnter(out var rejected));
        Assert.Null(rejected);

        bodySlot.Dispose();
        Assert.True(budget.TryEnter(out var replacement));
        replacement!.Dispose();
    }

    private static byte[] Records(params (int Relationship, int Size)[] records)
    {
        var result = new byte[records.Sum(record => record.Size)];
        int offset = 0;
        foreach (var record in records)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), record.Relationship);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 4), record.Size);
            offset += record.Size;
        }
        return result;
    }
}
