namespace Proxylity.RaptorQ;

internal enum SymbolOpKind : byte
{
    AddAssign,  // dest ^= src
    MulAssign,  // dest *= scalar
    FMA,        // dest ^= scalar * src
    Reorder,    // set logical→physical mapping
}

/// <summary>
/// A single symbol-vector operation recorded during a solver run.
/// Stored as a value type to avoid per-operation heap allocation.
/// </summary>
internal readonly struct SymbolOp
{
    public readonly SymbolOpKind Kind;
    public readonly int  Dest;
    public readonly int  Src;
    public readonly byte Scalar;
    public readonly int[]? Mapping; // only for Reorder

    private SymbolOp(SymbolOpKind kind, int dest, int src, byte scalar, int[]? mapping)
    {
        Kind = kind; Dest = dest; Src = src; Scalar = scalar; Mapping = mapping;
    }

    public static SymbolOp AddAssign(int dest, int src) =>
        new(SymbolOpKind.AddAssign, dest, src, 0, null);

    public static SymbolOp MulAssign(int dest, byte scalar) =>
        new(SymbolOpKind.MulAssign, dest, 0, scalar, null);

    public static SymbolOp FMA(int dest, int src, byte scalar) =>
        new(SymbolOpKind.FMA, dest, src, scalar, null);

    public static SymbolOp Reorder(int[] mapping) =>
        new(SymbolOpKind.Reorder, 0, 0, 0, mapping);
}
