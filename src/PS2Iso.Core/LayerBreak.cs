namespace PS2Iso.Core;

/// <summary>Helpers for choosing the layer break of a dual-layer (OTP) DVD-9 disc.</summary>
public static class LayerBreak
{
    /// <summary>Choose an opposite-track-path layer break for a spanning volume of
    /// <paramref name="totalSectors"/>. The break is a 16-sector ECC boundary, layer 0 gets at
    /// least as many sectors as layer 1, and neither layer exceeds the DVD+R DL per-layer maximum.
    /// Defaults to the midpoint (the common behaviour when no break is authored).</summary>
    public static long ChooseOtp(long totalSectors)
    {
        long half = Sectors.AlignUp(totalSectors / 2, Sectors.EccBlock);
        // Layer 0 must be >= layer 1 and each layer must fit a DVD+R DL layer.
        long minL0 = Sectors.AlignUp(totalSectors - Sectors.DvdPlusRDlLayerMax, Sectors.EccBlock);
        long maxL0 = Sectors.DvdPlusRDlLayerMax / Sectors.EccBlock * Sectors.EccBlock;
        long l0 = Math.Clamp(half, Math.Max(minL0, half), maxL0);
        if (l0 < minL0) l0 = minL0; // ensure layer 1 fits
        return l0;
    }

    /// <summary>Validate a chosen layer break against DVD+R DL limits. Returns null if OK, else a
    /// human-readable reason.</summary>
    public static string? Validate(long layerBreak, long totalSectors)
    {
        if (layerBreak <= 0 || layerBreak >= totalSectors)
            return $"layer break {layerBreak} must be between 1 and {totalSectors - 1}.";
        if (layerBreak % Sectors.EccBlock != 0)
            return $"layer break {layerBreak} must be a multiple of {Sectors.EccBlock} (ECC block).";
        long l1 = totalSectors - layerBreak;
        if (layerBreak > Sectors.DvdPlusRDlLayerMax)
            return $"layer 0 ({layerBreak:N0}) exceeds the DVD+R DL layer maximum " +
                   $"({Sectors.DvdPlusRDlLayerMax:N0}).";
        if (l1 > Sectors.DvdPlusRDlLayerMax)
            return $"layer 1 ({l1:N0}) exceeds the DVD+R DL layer maximum.";
        if (layerBreak < l1)
            return $"layer 0 ({layerBreak:N0}) is smaller than layer 1 ({l1:N0}); " +
                   "OTP discs require layer 0 >= layer 1.";
        return null;
    }
}
