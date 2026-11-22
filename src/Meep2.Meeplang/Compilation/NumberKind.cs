namespace Meep2.Meeplang.Compilation;

/// <summary>The kinds of number Meeplang works with.</summary>
internal enum NumberKind
{
    /// <summary>Not a number.</summary>
    None,

    /// <summary>decimal and the whole-number types up to 64 bits: worked out as decimals.</summary>
    Exact,

    /// <summary>BigDecimal, BigInteger, Int128 and UInt128: exact, but they may not fit in a decimal.</summary>
    Big,

    /// <summary>float, double and Half from C# types: approximate already, so sums with them are done in double.</summary>
    Approximate,
}
