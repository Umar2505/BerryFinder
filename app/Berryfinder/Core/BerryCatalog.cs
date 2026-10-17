// What each berry looks like, where it grows and when: the "what to look for" content. Plain words, English and Finnish.
// Whether a berry has spots on the map is not set here: it follows the models in production (PredictionPublisher.Production).
// Season windows are typical for northern Finland (the Oulu region) and move a couple of weeks with the year and the place.
public sealed record BerryInfo(
    string Id, string NameEn, string NameFi, string PluralEn, string PluralFi, string Latin, string Color,
    (int Month, int Day) SeasonStart, (int Month, int Day) SeasonEnd,
    string LookEn, string LookFi, string WhereEn, string WhereFi, string TipEn, string TipFi)
{
    public string Name(string lang) => lang == "fi" ? NameFi : NameEn;
    public string Plural(string lang) => lang == "fi" ? PluralFi : PluralEn;
    public string Look(string lang) => lang == "fi" ? LookFi : LookEn;
    public string Where(string lang) => lang == "fi" ? WhereFi : WhereEn;
    public string Tip(string lang) => lang == "fi" ? TipFi : TipEn;
    public string Art => $"/berries/{Id}.svg";
}

public static class BerryCatalog
{
    // Colours are the berry's own, deep enough for white text on top
    public static readonly IReadOnlyList<BerryInfo> All =
    [
        new("bilberry", "Bilberry", "Mustikka", "bilberries", "mustikat", "Vaccinium myrtillus", "#2D2A6E", (7, 15), (8, 31),
            "Small round blue-black berries with a dusty bloom, growing singly or in pairs on knee-high green shrubs.",
            "Pieniä pyöreitä sinimustia marjoja, joissa on vahamainen kerros. Kasvavat yksittäin tai pareittain polvenkorkuisissa vihreissä varvuissa.",
            "Mossy spruce and pine forest floors, often under tall trees.",
            "Sammaleisissa kuusi- ja mäntymetsissä, usein korkeiden puiden alla.",
            "Your fingers and lips turn blue.", "Sormet ja huulet värjäytyvät siniseksi."),
        new("lingonberry", "Lingonberry", "Puolukka", "lingonberries", "puolukat", "Vaccinium vitis-idaea", "#B3202F", (8, 15), (10, 31),
            "Glossy bright-red berries in small clusters at the tips of low shrubs with leathery, dark-green leaves.",
            "Kiiltäviä kirkkaanpunaisia marjoja pieninä tertuina matalien varpujen latvoissa. Lehdet ovat nahkeita ja tummanvihreitä.",
            "Dry pine forests, heaths and forest edges, often on sandier ground.",
            "Kuivissa mäntymetsissä, kankailla ja metsänreunoissa, usein hiekkaisemmalla maalla.",
            "The leaves stay green all winter.", "Lehdet pysyvät vihreinä talven yli."),
        new("cloudberry", "Cloudberry", "Hilla", "cloudberries", "hillat", "Rubus chamaemorus", "#B86E00", (7, 15), (8, 31),
            "A single amber-orange berry, like a raspberry, on top of a short stem with a few crinkled, maple-like leaves.",
            "Yksi meripihkanoranssi, vadelman näköinen marja varren päässä. Lehdet ovat ryppyisiä ja vaahteranlehtimäisiä.",
            "Open wet bogs. Walk on firm ground: bogs can be deep.",
            "Avosoilla ja märillä rämeillä. Kulje kantavalla maalla, suo voi olla syvä.",
            "Red means unripe; golden-amber means ripe.", "Punainen on raakaa, kullankeltainen kypsää."),
        new("raspberry", "Raspberry", "Vadelma", "raspberries", "vadelmat", "Rubus idaeus", "#B3174F", (7, 10), (8, 31),
            "Soft red berries that come off the stem hollow, on tall, thorny canes.",
            "Pehmeitä punaisia marjoja, jotka irtoavat varresta onttoina. Kasvavat pitkillä piikkisillä varsilla.",
            "Forest clearings, road verges and sunny edges.",
            "Hakkuuaukoilla, tienpientareilla ja aurinkoisilla reunoilla.",
            "Wear long sleeves: the canes are prickly.", "Pue pitkähihaiset vaatteet: varsissa on piikkejä."),
        new("bog_bilberry", "Bog bilberry", "Juolukka", "bog bilberries", "juolukat", "Vaccinium uliginosum", "#3A4A8C", (8, 1), (9, 20),
            "Blue berries like bilberries, but pale inside, on rounded blue-green leaves.",
            "Mustikan näköisiä sinisiä marjoja, jotka ovat sisältä vaaleita. Lehdet ovat pyöreähköt ja sinivihreät.",
            "Bogs, bog edges and open fell heath.",
            "Soilla, suon reunoilla ja avoimilla tunturikankailla.",
            "Cut one open: the juice is clear, not purple.", "Halkaise marja: mehu on kirkasta, ei violettia."),
        new("cranberry", "Cranberry", "Karpalo", "cranberries", "karpalot", "Vaccinium oxycoccos", "#6D1B3A", (9, 1), (10, 31),
            "Small red berries, sometimes speckled, hanging from very thin creeping stems.",
            "Pieniä punaisia, joskus pilkullisia marjoja hyvin ohuilla, maata pitkin kasvavilla varsilla.",
            "Open bogs and wet moss.",
            "Avosoilla ja märällä sammaleella.",
            "Sweeter after the first frosts.", "Makeampia ensimmäisten hallojen jälkeen."),
        new("crowberry", "Crowberry", "Variksenmarja", "crowberries", "variksenmarjat", "Empetrum nigrum", "#26263A", (8, 1), (9, 30),
            "Shiny black berries on low evergreen shrubs with needle-like leaves.",
            "Kiiltäviä mustia marjoja matalissa ikivihreissä varvuissa, joissa on neulasmaiset lehdet.",
            "Dry heaths, rocky ground and open pine forest; also bogs.",
            "Kuivilla kankailla, kallioilla ja harvoissa mäntymetsissä; myös soilla.",
            "Mild and watery: best mixed with other berries.", "Mieto ja vetinen: hyvä sekoittaa muihin marjoihin."),
        new("wild_strawberry", "Wild strawberry", "Metsämansikka", "wild strawberries", "metsämansikat", "Fragaria vesca", "#C0262D", (6, 15), (7, 31),
            "Tiny sweet red strawberries above toothed leaves that come in threes.",
            "Pieniä makeita punaisia mansikoita sahalaitaisten, kolmiosaisten lehtien yläpuolella.",
            "Sunny forest edges, road banks and meadows.",
            "Aurinkoisilla metsänreunoilla, tienvarsilla ja niityillä.",
            "Smell them: ripe ones are very fragrant.", "Haistele: kypsät tuoksuvat voimakkaasti."),
        new("arctic_bramble", "Arctic bramble", "Mesimarja", "arctic brambles", "mesimarjat", "Rubus arcticus", "#8A2A5C", (7, 10), (8, 15),
            "A deep-red, raspberry-like berry on a low plant with three-part leaves.",
            "Syvänpunainen, vadelman näköinen marja matalan kasvin päässä. Lehdet ovat kolmiosaisia.",
            "Damp meadows and the edges of bogs. It is scarce: enjoy it, don't strip it.",
            "Kosteilla niityillä ja suon reunoilla. Harvinainen: älä kerää kaikkea.",
            "The flower is bright pink in early summer.", "Kukka on kirkkaanpunainen alkukesällä."),
        new("sea_buckthorn", "Sea buckthorn", "Tyrni", "sea buckthorn", "tyrnit", "Hippophae rhamnoides", "#C25A00", (9, 1), (10, 31),
            "Bright orange berries packed along thorny branches with narrow silvery leaves.",
            "Kirkkaanoransseja marjoja tiiviisti piikkisillä oksilla. Lehdet ovat kapeita ja hopeanharmaita.",
            "Sandy and gravelly sea shores in full sun.",
            "Hiekkaisilla ja sorapohjaisilla merenrannoilla auringossa.",
            "Thorny: wear gloves. The berries are very sour.", "Piikikäs: käytä hanskoja. Marjat ovat hyvin happamia."),
    ];

    public static BerryInfo Get(string id) => All.First(b => b.Id == id);

    public static bool TryGet(string id, out BerryInfo berry)
    {
        berry = All.FirstOrDefault(b => b.Id == id)!;
        return berry is not null;
    }

    private static int Key(int month, int day) => month * 100 + day;

    public static bool InSeason(BerryInfo b, DateOnly date) =>
        Key(date.Month, date.Day) >= Key(b.SeasonStart.Month, b.SeasonStart.Day) && Key(date.Month, date.Day) <= Key(b.SeasonEnd.Month, b.SeasonEnd.Day);

    // Days until the season opens: 0 while it is open, otherwise to the next opening (this year or next)
    public static int DaysUntilStart(BerryInfo b, DateOnly date)
    {
        if (InSeason(b, date))
        {
            return 0;
        }

        var start = new DateOnly(date.Year, b.SeasonStart.Month, b.SeasonStart.Day);
        if (start < date)
        {
            start = start.AddYears(1);
        }

        return start.DayNumber - date.DayNumber;
    }

    private static readonly string[] MonthsEn = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private static readonly string[] MonthsFiGenitive = ["tammikuun", "helmikuun", "maaliskuun", "huhtikuun", "toukokuun", "kesäkuun", "heinäkuun", "elokuun", "syyskuun", "lokakuun", "marraskuun", "joulukuun"];

    // "Mid July to late August" / "Heinäkuun puoliväli – elokuun loppu"
    public static string SeasonText(BerryInfo b, string lang)
    {
        static int Third(int day) => day <= 10 ? 0 : day >= 20 ? 2 : 1;

        string Part((int Month, int Day) d) => lang == "fi"
            ? $"{MonthsFiGenitive[d.Month - 1]} {new[] { "alku", "puoliväli", "loppu" }[Third(d.Day)]}"
            : $"{new[] { "early", "mid", "late" }[Third(d.Day)]} {MonthsEn[d.Month - 1]}";

        var text = lang == "fi" ? $"{Part(b.SeasonStart)} – {Part(b.SeasonEnd)}" : $"{Part(b.SeasonStart)} to {Part(b.SeasonEnd)}";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
