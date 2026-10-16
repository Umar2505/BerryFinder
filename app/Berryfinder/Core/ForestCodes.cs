// Code lists of the Finnish forest data standard (metsätietostandardi) as used by the Finnish Forest
// Centre's open stand data. Verified against Tapio's "Metsäsuunnittelun maastotyöopas" (2023), which
// documents standard message version 1.9. Labels are (English, Finnish).
public static class ForestCodes
{
    public static readonly IReadOnlyDictionary<string, (string En, string Fi)> LandClass = new Dictionary<string, (string, string)>
    {
        ["1"] = ("Forest land", "Metsämaa"),
        ["2"] = ("Poorly productive forest land", "Kitumaa"),
        ["3"] = ("Unproductive land", "Joutomaa"),
        ["4"] = ("Other forestry land", "Muu metsätalousmaa"),
        ["5"] = ("Building plot", "Tontti"),
        ["6"] = ("Agricultural land", "Maatalousmaa"),
        ["7"] = ("Other land", "Muu maa"),
        ["8"] = ("Water", "Vesistö"),
        ["9"] = ("Road", "Tie"),
        ["10"] = ("Power line", "Linja"),
    };

    public static readonly IReadOnlyDictionary<string, (string En, string Fi)> SiteMainClass = new Dictionary<string, (string, string)>
    {
        ["1"] = ("Mineral soil heath", "Kangas"),
        ["2"] = ("Spruce mire", "Korpi"),
        ["3"] = ("Pine mire", "Räme"),
        ["4"] = ("Open bog", "Neva"),
        ["5"] = ("Rich fen", "Letto"),
    };

    public static readonly IReadOnlyDictionary<int, (string En, string Fi)> Fertility = new Dictionary<int, (string, string)>
    {
        [1] = ("Herb-rich forest", "Lehto"),
        [2] = ("Herb-rich heath (OMT)", "Lehtomainen kangas"),
        [3] = ("Mesic heath (MT)", "Tuore kangas"),
        [4] = ("Sub-xeric heath (VT)", "Kuivahko kangas"),
        [5] = ("Xeric heath (CT)", "Kuiva kangas"),
        [6] = ("Barren heath (ClT)", "Karukkokangas"),
        [7] = ("Rocky or sandy land", "Kalliomaa ja hietikko"),
        [8] = ("Summit forest or fell", "Lakimetsä ja tunturi"),
    };

    public static readonly IReadOnlyDictionary<int, (string En, string Fi)> SoilType = new Dictionary<int, (string, string)>
    {
        [10] = ("Medium or coarse mineral soil", "Keskikarkea tai karkea kangasmaa"),
        [11] = ("Coarse till", "Karkea moreeni"),
        [12] = ("Coarse sorted soil", "Karkea lajittunut maalaji"),
        [20] = ("Fine mineral soil", "Hienojakoinen kangasmaa"),
        [21] = ("Fine till", "Hienoainesmoreeni"),
        [22] = ("Fine sorted soil", "Hienojakoinen lajittunut maalaji"),
        [23] = ("Silty soil", "Silttipitoinen maalaji"),
        [24] = ("Clay", "Savimaa"),
        [30] = ("Stony coarse mineral soil", "Kivinen karkea kangasmaa"),
        [31] = ("Stony coarse till", "Kivinen karkea moreeni"),
        [32] = ("Stony coarse sorted soil", "Kivinen karkea lajittunut maalaji"),
        [40] = ("Stony fine mineral soil", "Kivinen hienojakoinen kangasmaa"),
        [50] = ("Bedrock or boulders", "Kallio tai kivikko"),
        [60] = ("Peat", "Turvemaa"),
        [61] = ("Sedge peat", "Saraturve"),
        [62] = ("Sphagnum peat", "Rahkaturve"),
        [63] = ("Woody peat", "Puuvaltainen turve"),
        [64] = ("Erodible sedge peat", "Eroosioherkkä saraturve"),
        [65] = ("Erodible sphagnum peat", "Eroosioherkkä rahkaturve"),
        [66] = ("Undecomposed sedge peat", "Maatumaton saraturve"),
        [67] = ("Undecomposed sphagnum peat", "Maatumaton rahkaturve"),
        [70] = ("Mull soil", "Multamaa"),
        [80] = ("Gyttja soil", "Liejumaa"),
    };

    public static readonly IReadOnlyDictionary<string, (string En, string Fi)> Drainage = new Dictionary<string, (string, string)>
    {
        ["1"] = ("Undrained mineral soil", "Ojittamaton kangas"),
        ["2"] = ("Paludified mineral soil", "Soistunut kangas"),
        ["3"] = ("Drained mineral soil", "Ojitettu kangas"),
        ["6"] = ("Natural mire", "Luonnontilainen suo"),
        ["7"] = ("Recently drained mire", "Ojikko"),
        ["8"] = ("Transforming drained mire", "Muuttuma"),
        ["9"] = ("Drained peatland forest", "Turvekangas"),
    };

    public static readonly IReadOnlyDictionary<string, (string En, string Fi)> Development = new Dictionary<string, (string, string)>
    {
        ["A0"] = ("Open regeneration area", "Aukea"),
        ["S0"] = ("Seed-tree stand", "Siemenpuumetsikkö"),
        ["T1"] = ("Young seedling stand", "Pieni taimikko"),
        ["T2"] = ("Advanced seedling stand", "Varttunut taimikko"),
        ["Y1"] = ("Seedling stand with overstorey", "Ylispuustoinen taimikko"),
        ["02"] = ("Young thinning stand", "Nuori kasvatusmetsikkö"),
        ["03"] = ("Advanced thinning stand", "Varttunut kasvatusmetsikkö"),
        ["04"] = ("Mature stand", "Uudistuskypsä metsikkö"),
        ["05"] = ("Shelterwood stand", "Suojuspuumetsikkö"),
        ["ER"] = ("Uneven-aged stand", "Eri-ikäisrakenteinen metsikkö"),
    };

    public static readonly IReadOnlyDictionary<int, (string En, string Fi)> TreeSpecies = new Dictionary<int, (string, string)>
    {
        [1] = ("Scots pine", "Mänty"), [2] = ("Norway spruce", "Kuusi"), [3] = ("Silver birch", "Rauduskoivu"),
        [4] = ("Downy birch", "Hieskoivu"), [5] = ("Aspen", "Haapa"), [6] = ("Grey alder", "Harmaaleppä"),
        [7] = ("Black alder", "Tervaleppä"), [8] = ("Other conifer", "Muu havupuu"), [9] = ("Other broadleaf", "Muu lehtipuu"),
        [10] = ("Douglas fir", "Douglaskuusi"), [11] = ("Juniper", "Kataja"), [12] = ("Lodgepole pine", "Kontortamänty"),
        [14] = ("Larch", "Lehtikuusi"), [16] = ("Black spruce", "Mustakuusi"), [17] = ("Willow", "Paju"),
        [18] = ("Rowan", "Pihlaja"), [20] = ("Goat willow", "Raita"), [25] = ("Bird cherry", "Tuomi"),
        [29] = ("Broadleaf", "Lehtipuu"), [30] = ("Conifer", "Havupuu"),
    };

    public static bool IsPine(int? code) => code is 1 or 12 or 22;
    public static bool IsSpruce(int? code) => code is 2 or 16 or 23;
    public static bool IsBroadleaf(int? code) => code is 3 or 4 or 5 or 6 or 7 or 9 or 15 or 17 or 18 or 20 or 21 or 24 or 25 or 26 or 27 or 29;

    public static string Label(IReadOnlyDictionary<string, (string En, string Fi)> table, string? key, string language) =>
        key is not null && table.TryGetValue(key, out var label) ? (language == "fi" ? label.Fi : label.En) : NotRecorded(language);

    public static string Label(IReadOnlyDictionary<int, (string En, string Fi)> table, int? key, string language) =>
        key is { } k && table.TryGetValue(k, out var label) ? (language == "fi" ? label.Fi : label.En) : NotRecorded(language);

    private static string NotRecorded(string language) => language == "fi" ? "Ei tietoa" : "Not recorded";
}

// GTK superficial-deposit labels end in an abbreviation in brackets, e.g. "Paksu turvekerros (Tvp) RT".
// This folds GTK's many classes into the few that matter for berry habitat.
public static class SoilClassifier
{
    public const string ThickPeat = "thick_peat";
    public const string ThinPeat = "thin_peat";
    public const string Till = "till";
    public const string Coarse = "coarse";
    public const string Fine = "fine";
    public const string Rock = "rock";
    public const string Water = "water";
    public const string Unmapped = "unmapped";
    public const string Other = "other";

    public static readonly IReadOnlyDictionary<string, (string En, string Fi)> Labels = new Dictionary<string, (string, string)>
    {
        [ThickPeat] = ("Thick peat", "Paksu turve"),
        [ThinPeat] = ("Thin peat or paludified", "Ohut turve tai soistuma"),
        [Till] = ("Till (moraine)", "Moreeni"),
        [Coarse] = ("Sand or gravel", "Hiekka tai sora"),
        [Fine] = ("Silt or clay", "Siltti tai savi"),
        [Rock] = ("Bedrock outcrop", "Kallio"),
        [Water] = ("Water", "Vesi"),
        [Unmapped] = ("Not mapped", "Kartoittamaton"),
        [Other] = ("Other deposit", "Muu maalaji"),
    };

    public static string Classify(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return Unmapped;
        }

        var abbreviation = ExtractAbbreviation(label);
        var lower = label.ToLowerInvariant();

        return abbreviation switch
        {
            "Tvp" or "Tv" or "Tvk" or "Tvr" or "Tvs-p" => ThickPeat,
            "Tvo" or "Tvs" or "Lj" or "Mu" => ThinPeat,
            "Mr" or "SY" or "MrMr" or "Mr-Rp" => Till,
            "KY" or "Hk" or "HHk" or "KHk" or "Sr" or "Hs" or "Rk" or "Lo" => Coarse,
            "HY" or "Si" or "Sa" or "Hsi" or "Ht" or "HSi" or "LjSa" => Fine,
            "Ka" or "RK" or "Kal" => Rock,
            "Ve" or "Vesi" => Water,
            "0" => Unmapped,
            _ when lower.Contains("turve") => lower.Contains("ohut") || lower.Contains("soistuma") ? ThinPeat : ThickPeat,
            _ when lower.Contains("moreeni") || lower.Contains("sekalajitteinen") => Till,
            _ when lower.Contains("kallio") => Rock,
            _ when lower.Contains("hiekka") || lower.Contains("sora") || lower.Contains("karkearakeinen") => Coarse,
            _ when lower.Contains("savi") || lower.Contains("siltti") || lower.Contains("hienojakoinen") => Fine,
            _ when lower.Contains("vesi") => Water,
            _ when lower.Contains("kartoittamaton") => Unmapped,
            _ => Other,
        };
    }

    public static string? ExtractAbbreviation(string label)
    {
        var open = label.LastIndexOf('(');
        var close = label.LastIndexOf(')');
        return open >= 0 && close > open + 1 ? label[(open + 1)..close].Trim() : null;
    }
}
