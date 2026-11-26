using System.Buffers;

namespace ReeveWorkflow.Core.Execution;

internal sealed class PooledBuffer(int initialCapacity = 4096) : IBufferWriter<byte>, IDisposable
{
    private byte[]? rented = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 16));
    private int written;

    public int Length => written;

    public ReadOnlySpan<byte> WrittenSpan => Current.AsSpan(0, written);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > Current.Length - written)
            throw new InvalidOperationException("Advanced past the end of the buffer.");
        written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return Current.AsMemory(written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return Current.AsSpan(written);
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(GetSpan(bytes.Length));
        written += bytes.Length;
    }

    public void Write(byte value)
    {
        GetSpan(1)[0] = value;
        written++;
    }

    public void Dispose()
    {
        if (rented is { } array)
        {
            rented = null;
            ArrayPool<byte>.Shared.Return(array, clearArray: true);
        }
    }

    private byte[] Current => rented ?? throw new ObjectDisposedException(nameof(PooledBuffer));

    private void Ensure(int sizeHint)
    {
        var current = Current;
        var needed = Math.Max(sizeHint, 1);
        if (current.Length - written >= needed)
            return;

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(current.Length * 2, written + needed));
        current.AsSpan(0, written).CopyTo(grown);
        rented = grown;
        ArrayPool<byte>.Shared.Return(current, clearArray: true);
    }
}
