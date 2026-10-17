// The look of the app in one place: sizes, weights and surfaces borrowed from Apple's Human Interface Guidelines.
// Large titles, generous whitespace, soft shadows, 44 pt minimum touch targets, one accent colour.
public static class Design
{
    public const string LargeTitle = "text-[34px] leading-[1.1] font-bold tracking-tight text-foreground";
    public const string Title = "text-[22px] leading-tight font-semibold tracking-tight text-foreground";
    public const string Headline = "text-[17px] leading-snug font-semibold text-foreground";
    public const string Body = "text-[17px] leading-snug text-foreground";
    public const string Subhead = "text-[15px] leading-snug text-muted-foreground";
    public const string Caption = "text-[13px] leading-snug text-muted-foreground";
    public const string Overline = "text-[12px] leading-none font-semibold uppercase tracking-[0.08em]";

    // Interaction: everything tappable shrinks slightly under the finger
    public const string Press = "transition-transform duration-150 active:scale-[0.97] cursor-pointer";

    // Surfaces
    public const string Card = "bg-card rounded-[24px] shadow-[0_1px_2px_rgba(0,0,0,0.04),0_8px_24px_rgba(0,0,0,0.06)]";
    public const string Sheet = "bg-muted rounded-[20px]";
    public const string Frosted = "bg-background/70 backdrop-blur-xl";

    // Pills on a coloured card
    public const string GlassChip = "h-8 px-3 rounded-full bg-white/15 text-white text-[13px] font-medium flex items-center gap-1.5";

    // Entrance: content eases up a few pixels as it appears
    public const string Appear = "motion-[0:opacity-0_translate-y-[10px],100:opacity-100_translate-y-0] motion-duration-300ms";

    public static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // A lighter tint of a #rrggbb colour (amount 0 = unchanged, 1 = white); the map draws the same ramp
    public static string MixWithWhite(string hex, double amount)
    {
        var n = Convert.ToInt32(hex.TrimStart('#'), 16);
        int Channel(int shift) => (int)Math.Round(((n >> shift) & 255) + (255 - ((n >> shift) & 255)) * amount);
        return $"#{Channel(16):x2}{Channel(8):x2}{Channel(0):x2}";
    }
}
