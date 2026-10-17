using System.Globalization;
using Dapper;

// The public website: value proposition, honest methodology, species, pricing, FAQ, responsible picking, contact
public partial class BerryfinderApp
{
    private readonly ClientReactive<string> _contactEmail = new("");
    private readonly ClientReactive<string> _contactTopic = new("question");
    private readonly ClientReactive<string> _contactMessage = new("");
    private readonly ClientReactive<string?> _contactState = new((string?)null);

    private void Section(UIView view, string title, string? lead, Action<UIView> content)
    {
        view.Column(["gap-4 motion-[0:opacity-0_translate-y-[10px],100:opacity-100_translate-y-0] motion-duration-300ms"], content: s =>
        {
            s.Text([Text.H2, "font-heading tracking-tight"], text: title);
            if (lead is not null) s.Text([Text.BodyLg, "text-muted-foreground max-w-2xl"], text: lead);
            content(s);
        });
    }

    private void RenderHomePage(UIView view)
    {
        view.ScrollArea(["flex-1 min-h-0"], content: scroll =>
        {
            scroll.Column(["w-full"], content: page =>
            {
                // Hero: a deep pine panel with a berry accent; the one primary action is Open map
                page.Box(["w-full bg-[#14261b] text-[#f3efe4] relative overflow-hidden"], content: hero =>
                {
                    hero.Box(["absolute -right-24 -top-24 w-[420px] h-[420px] rounded-full bg-[#7b2e7d]/25 blur-3xl"]);
                    hero.Box(["absolute -left-24 bottom-0 w-[360px] h-[360px] rounded-full bg-[#2f5d3a]/50 blur-3xl"]);
                    hero.Column([Layout.Page, "relative py-16 md:py-28 gap-6 max-w-5xl"], content: c =>
                    {
                        c.Badge(T("Pilot: Oulu, Finland", "Pilotti: Oulu"), tone: SemanticTone.Neutral, size: BadgeSize.Md, style: ["default", "self-start bg-white/10 text-[#f3efe4] border-white/20"]);
                        c.Text(["font-heading text-4xl md:text-6xl leading-[1.05] tracking-tight max-w-3xl"],
                            text: T("Spend less time searching and more time picking.", "Vähemmän etsimistä, enemmän poimimista."));
                        c.Text([Text.BodyLg, "text-[#d8d3c2] max-w-2xl"], text: T(
                            "BerryFinder ranks forests around Oulu for bilberries and lingonberries from official forest, soil and weather data, shows you why, and tells you plainly how sure it is.",
                            "BerryFinder järjestää Oulun seudun metsät mustikalle ja puolukalle virallisten metsä-, maaperä- ja säätietojen perusteella, kertoo miksi ja sanoo suoraan, kuinka varma se on."));
                        c.Row(["gap-3 flex-wrap mt-2"], content: r =>
                        {
                            r.Button([Button.PrimaryLg, "h-12 px-6 gap-2"], onClick: async () => await GoAsync("/map"), content: b =>
                            {
                                b.Icon([Icon.Sm], name: "map");
                                b.Text(text: T("Open the map", "Avaa kartta"));
                            });
                            r.Button([Button.OutlineLg, "h-12 px-6 border-white/30 text-[#f3efe4] hover:bg-white/10"], text: T("How it works", "Miten se toimii"), onClick: async () => await GoAsync("/methodology"));
                        });
                        c.Text([Text.Caption, "text-[#b9b5a4]"], text: T("Estimates are uncertain and cannot guarantee a harvest. Early pilot: predictions are unvalidated ecological estimates until field data confirms them.",
                            "Arviot ovat epävarmoja eivätkä takaa satoa. Varhainen pilotti: ennusteet ovat validoimattomia ekologisia arvioita, kunnes maastotiedot vahvistavat ne."));
                    });
                });

                page.Column([Layout.Page, "py-12 md:py-16 gap-16 max-w-5xl"], content: c =>
                {
                    Section(c, T("A forest map made for pickers", "Metsäkartta marjastajille"), null, s =>
                        s.Box(["grid grid-cols-1 md:grid-cols-3 gap-4"], content: g =>
                        {
                            Feature(g, "trees", T("Forest suitability", "Metsän soveltuvuus"),
                                T("Stand-level estimates from the Finnish Forest Centre's open data: site type, tree species, age and canopy, joined to Geological Survey soil.",
                                  "Metsäkuviotason arviot Metsäkeskuksen avoimesta datasta: kasvupaikka, puulajit, ikä ja latvusto, yhdistettynä GTK:n maaperätietoon."));
                            Feature(g, "route", T("Time-aware trips", "Aikaan sidotut retket"),
                                T("Real road driving times, then the picking time that is actually left. Premium ranks areas by quality against the drive.",
                                  "Todelliset ajoajat ja jäljelle jäävä poimina-aika. Premium järjestää alueet laadun ja ajomatkan mukaan."));
                            Feature(g, "shield-check", T("Honest about uncertainty", "Rehellinen epävarmuudesta"),
                                T("Every result says whether it is an unvalidated estimate or a trained model, how fresh the data is, and why it was recommended.",
                                  "Jokainen tulos kertoo, onko se validoimaton arvio vai koulutettu malli, kuinka tuore data on ja miksi alue suositeltiin."));
                        }));

                    Section(c, T("From a search to a basket", "Hausta korilliseen"), null, s =>
                        s.Box(["grid grid-cols-1 md:grid-cols-4 gap-4"], content: g =>
                        {
                            Step(g, "1", T("Search", "Hae"), T("Find Oulu or use your location.", "Hae Oulu tai käytä sijaintiasi."));
                            Step(g, "2", T("Choose a berry", "Valitse marja"), T("Bilberry or lingonberry.", "Mustikka tai puolukka."));
                            Step(g, "3", T("Open an area", "Avaa alue"), T("See the forest, soil, reasons and freshness.", "Näe metsä, maaperä, perustelut ja ajantasaisuus."));
                            Step(g, "4", T("Go, then tell us", "Lähde, ja kerro meille"), T("Navigate to the road access, and report what you found.", "Navigoi tien varteen ja kerro, mitä löysit."));
                        }));

                    c.Card([Card.Elevated, "p-0 overflow-hidden"], content: card =>
                        card.Row(["flex-col md:flex-row"], content: r =>
                        {
                            r.Column(["p-6 md:p-8 gap-3 flex-1"], content: t =>
                            {
                                t.Text([Text.H3, "font-heading"], text: T("Help the forest speak: report what you find", "Auta metsää puhumaan: raportoi löytösi"));
                                t.Text([Text.Body, "text-muted-foreground"], text: T(
                                    "Field reports are how a heuristic becomes a validated model. Yours stay private by default; you choose whether they are summarised for others or used to train the model.",
                                    "Maastohavainnot muuttavat heuristiikan validoiduksi malliksi. Omasi pysyvät oletuksena yksityisinä; päätät itse, tiivistetäänkö ne muille tai käytetäänkö ne mallin koulutukseen."));
                                t.Button([Button.OutlineMd, "self-start"], text: T("Try Field mode", "Kokeile Maastotilaa"), onClick: async () => await GoAsync("/field"));
                            });
                        }));

                    c.Row(["gap-3 flex-wrap text-sm text-muted-foreground border-t border-secondary pt-6"], content: r =>
                    {
                        r.Text([Text.Caption], text: Attribution);
                    });
                });
            });
        });
    }

    private static void Feature(UIView view, string icon, string title, string body)
    {
        view.Card([Card.Default], content: c =>
        {
            c.Box(["w-10 h-10 rounded-lg bg-brand-solid/10 flex items-center justify-center mb-2"], content: b => b.Icon([Icon.Md, "text-brand-secondary"], name: icon));
            c.Text([Text.H4, "font-heading"], text: title);
            c.Text([Text.Small, "text-muted-foreground"], text: body);
        });
    }

    private static void Step(UIView view, string number, string title, string body)
    {
        view.Column(["gap-2"], content: c =>
        {
            c.Box(["w-9 h-9 rounded-full bg-[#1d3b2a] text-[#f5f1e6] flex items-center justify-center font-semibold"], content: b => b.Text(text: number));
            c.Text([Text.BodyStrong], text: title);
            c.Text([Text.Small, "text-muted-foreground"], text: body);
        });
    }

    private void RenderMethodologyPage(UIView view)
    {
        RenderScrollPage(view, page =>
        {
            Section(page, T("How the predictions work", "Miten ennusteet toimivat"),
                T("Short version: two separate layers, each labelled for what it is. We never show a number we can't stand behind.", "Lyhyesti: kaksi erillistä kerrosta, kumpikin nimetty sen mukaan, mitä se on. Emme näytä lukua, jonka takana emme voi seistä."), s =>
                {
                    s.Card([Card.Default], content: c =>
                    {
                        c.Text([Text.H4, "font-heading"], text: T("Layer A: habitat suitability", "Kerros A: kasvupaikan soveltuvuus"));
                        c.Text([Text.Body], text: T("For each forest stand we combine site fertility (the mesic-to-barren heath scale), tree species, age and development class, canopy density, and GTK's surface deposit. Bilberry does best on mesic heaths in mid-aged and mature stands; lingonberry on drier, sub-xeric pine heaths with a more open canopy. Clear-cuts score low for both because the plants take decades to recover.",
                            "Jokaiselle metsäkuviolle yhdistämme kasvupaikan ravinteisuuden, puulajit, iän ja kehitysluokan, latvuston tiheyden sekä GTK:n pintamaalajin. Mustikka viihtyy tuoreilla kankailla keski-ikäisissä ja kypsissä metsissä; puolukka kuivahkoilla mäntykankailla ja avoimemmassa latvustossa. Avohakkuut saavat alhaisen arvion molemmille, koska kasvit toipuvat vuosikymmeniä."));
                        c.Text([Text.Body, "mt-2"], text: T("Today this layer is an ecological baseline built from published research (Miina et al. 2009; Turtiainen et al. 2013). The response shapes are expert judgement, not fitted coefficients, so results are labelled unvalidated estimates and shown as classes, not probabilities.",
                            "Tänään tämä kerros on julkaistuun tutkimukseen (Miina ym. 2009; Turtiainen ym. 2013) perustuva ekologinen perusmalli. Vastekäyrät ovat asiantuntija-arvioita, ei sovitettuja kertoimia, joten tulokset merkitään validoimattomiksi arvioiksi ja näytetään luokkina, ei todennäköisyyksinä."));
                    });

                    s.Card([Card.Default], content: c =>
                    {
                        c.Text([Text.H4, "font-heading"], text: T("Layer A2: the Nordic machine-learning model", "Kerros A2: pohjoismainen koneoppimismalli"));
                        c.Text([Text.Body], text: T("For berries without forest-stand rules (cloudberry, cranberry, bog bilberry, raspberry, arctic bramble, sea buckthorn), spots come from five neural networks trained across the Nordic countries on 1 km CHELSA climate, SoilGrids soil, ESA WorldCover land cover, the Copernicus elevation model and Finnish stand data. Each 250 m square around Oulu is scored, and we keep each berry's best tenth that also matches its basic habitat, such as a mire for cloudberry.",
                            "Marjoille, joille ei ole metsäkuviosääntöjä (hilla, karpalo, juolukka, vadelma, mesimarja, tyrni), paikat tulevat viidestä neuroverkosta, jotka on koulutettu Pohjoismaissa 1 km CHELSA-ilmastolla, SoilGrids-maaperällä, ESA WorldCover -maanpeitteellä, Copernicus-korkeusmallilla ja metsäkeskuksen kuviotiedoilla. Jokainen 250 metrin ruutu Oulun ympärillä pisteytetään, ja kullekin marjalle pidetään paras kymmenes, joka sopii myös sen peruskasvupaikkaan, kuten suo hillalle."));
                        c.Text([Text.Body, "mt-2"], text: T("The networks were taught with written habitat rules, not field data, and checked against real public sightings (GBIF). Only berries that beat chance there get spots, always marked \"Worth a try\" at most. Bilberry, crowberry and wild strawberry did not pass: bilberry keeps its forest-stand spots, the other two have none yet.",
                            "Verkot opetettiin kirjatuilla kasvupaikkasäännöillä, ei maastotiedoilla, ja tarkistettiin todellisia julkisia havaintoja (GBIF) vasten. Vain sattumaa paremmin osuvat marjat saavat paikkoja, ja ne merkitään korkeintaan \"Kokeilemisen arvoinen\". Mustikka, variksenmarja ja metsämansikka eivät läpäisseet: mustikalla on yhä metsäkuvioihin perustuvat paikkansa, kahdella muulla ei vielä ole paikkoja."));
                    });

                    s.Card([Card.Default], content: c =>
                    {
                        c.Text([Text.H4, "font-heading"], text: T("Layer B: this season", "Kerros B: tämä kausi"));
                        c.Text([Text.Body], text: T("Where the season stands comes from growing degree days accumulated from 1 April at the nearest Finnish Meteorological Institute station, with spring frost during flowering and dry summers flagged. If the station has gaps, NASA POWER's modelled grid fills in, and the result says so. It estimates the stage (flowering, unripe, ripe), never a yield.",
                            "Kauden vaihe perustuu kasvuastepäiviin, jotka kertyvät 1. huhtikuuta lähimmällä Ilmatieteen laitoksen asemalla; kukinnan aikaiset hallat ja kuivat kesät merkitään. Jos asemalla on aukkoja, NASA POWERin mallinnettu ruudukko täydentää ja tulos kertoo sen. Se arvioi vaiheen (kukinta, raaka, kypsä), ei satoa."));
                    });

                    s.Card([Card.Default], content: c =>
                    {
                        c.Text([Text.H4, "font-heading"], text: T("How a model earns trust", "Miten malli ansaitsee luottamuksen"));
                        foreach (var line in new[]
                        {
                            (T("Real labels only: accepted field observations; a place nobody visited is never an absence.", "Vain oikeita tietoja: hyväksyttyjä maastohavaintoja; paikka, jossa kukaan ei käynyt, ei ole koskaan poissaolo."),
                            T("Validated on whole map blocks and a held-out season, never on neighbours of training plots.", "Validoidaan kokonaisilla karttalohkoilla ja pidätetyllä kaudella, ei koskaan koulutusalueiden naapureilla.")),
                            (T("It must beat the ecological baseline on those held-out blocks and be well calibrated.", "Sen on voitettava ekologinen perusmalli näillä lohkoilla ja oltava hyvin kalibroitu."),
                            T("Only then can an administrator promote it. Nothing replaces production silently.", "Vasta sitten ylläpitäjä voi ottaa sen tuotantoon. Mikään ei korvaa tuotantoa hiljaa.")),
                        })
                        {
                            c.Row(["items-start gap-2"], content: r => { r.Icon([Icon.Sm, "text-success-600 mt-1 shrink-0"], name: "check"); r.Text([Text.Body], text: line.Item1 + " " + line.Item2); });
                        }
                    });

                    s.Card([Card.Subtle], content: c =>
                    {
                        c.Text([Text.H4, "font-heading"], text: T("What we use, and what we don't claim", "Mitä käytämme ja mitä emme väitä"));
                        foreach (var line in new[]
                        {
                            T("Finnish Forest Centre open stand data, GTK superficial deposits, FMI weather, Syke protected areas, OpenStreetMap roads, NASA POWER modelled weather, Copernicus Sentinel-2 scene catalogue; for the machine-learning model also CHELSA climate, ISRIC SoilGrids, ESA WorldCover, the Copernicus DEM and GBIF sightings.", "Metsäkeskuksen avoin kuviotieto, GTK:n pintamaalajit, Ilmatieteen laitoksen sää, Syken suojelualueet, OpenStreetMapin tiet, NASA POWERin mallinnettu sää, Copernicus Sentinel-2 -kuvaluettelo; koneoppimismallille myös CHELSA-ilmasto, ISRIC SoilGrids, ESA WorldCover, Copernicus-korkeusmalli ja GBIF-havainnot."),
                            T("Prediction cells are forest stands (typically 1 to 5 hectares). We can't see individual berries, and a good stand can still have a poor year.", "Ennustesolut ovat metsäkuvioita (tyypillisesti 1–5 hehtaaria). Emme näe yksittäisiä marjoja, ja hyvälläkin kuviolla voi olla huono vuosi."),
                            T("GTK's map has no measured pH, nutrients or moisture, so none are used. NASA's soil wetness is a coarse model grid, shown only as context.", "GTK:n kartassa ei ole mitattua pH:ta, ravinteita tai kosteutta, joten niitä ei käytetä. NASAn maankosteus on karkea mallitieto, näytetään vain taustana."),
                            T("We don't predict kilograms. That needs yield-calibrated field measurements, which the pilot collects.", "Emme ennusta kilogrammoja. Se vaatii satokalibroituja maastomittauksia, joita pilotti kerää."),
                        })
                        {
                            c.Row(["items-start gap-2"], content: r => { r.Icon([Icon.Sm, "text-muted-foreground mt-1 shrink-0"], name: "info"); r.Text([Text.Small], text: line); });
                        }
                    });
                });
        });
    }

    private void RenderPricingPage(UIView view)
    {
        RenderScrollPage(view, page =>
        {
            Section(page, T("Plans", "Tilaukset"), T("Prices are preliminary and will be tested with real pickers during the pilot. Nothing is charged unless billing is switched on for your account.", "Hinnat ovat alustavia ja niitä testataan oikeiden marjastajien kanssa pilotin aikana. Mitään ei veloiteta, ellei laskutus ole käytössä tililläsi."), s =>
                s.Box(["grid grid-cols-1 md:grid-cols-3 gap-4"], content: g =>
                {
                    PlanCard(g, T("Free", "Ilmainen"), "€0", T("to try it", "kokeiluun"), false,
                        [T("Interactive berry map", "Interaktiivinen marjakartta"), T("General suitability", "Yleinen soveltuvuus"), T("Top 5 areas in view", "5 parasta aluetta näkymässä"), T("10 saved places", "10 tallennettua paikkaa"), T("Send field reports", "Lähetä maastohavaintoja")], null);
                    PlanCard(g, "Premium", "€4.99", T("per month · or €19.99 per season", "kuukaudessa · tai 19,99 € kaudelta"), true,
                        [T("Every ranked area", "Kaikki järjestetyt alueet"), T("Seasonal stage estimates", "Kauden vaiheen arviot"), T("Advanced filters", "Kehittyneet suodattimet"), T("Smart trip planner", "Älykäs retkisuunnittelija"), T("200 saved places", "200 tallennettua paikkaa"), T("Offline features (coming)", "Offline-ominaisuudet (tulossa)")], "premium-monthly");
                    PlanCard(g, T("Professional", "Ammattilainen"), "€29", T("per month · preliminary", "kuukaudessa · alustava"), false,
                        [T("Everything in Premium", "Kaikki Premiumin ominaisuudet"), T("Larger-area analysis", "Laajempi aluetarkastelu"), T("Exportable reports", "Vietävät raportit"), T("Team features (coming)", "Tiimiominaisuudet (tulossa)"), T("API access (later)", "API-pääsy (myöhemmin)")], "professional-monthly");
                }));

            if (_billingProblem.Value is { } problem) page.Alert(problem, tone: SemanticTone.Info);
            if (_checkoutUrl.Value is { } url)
            {
                page.Alert(T("Your checkout is ready", "Maksusi on valmis"), tone: SemanticTone.Success, content: a =>
                    a.Link([Button.PrimaryMd, "mt-2"], href: url, target: "_blank", rel: "noopener", text: T("Continue to secure checkout", "Jatka turvalliseen maksuun")));
            }

            page.Text([Text.Caption, "text-muted-foreground"], text: T("Payments are handled by the platform's payment provider; we never see card details. Prices include VAT where it applies. Cancel any time from your account.",
                "Maksut hoitaa alustan maksupalveluntarjoaja; emme näe korttitietoja. Hinnat sisältävät ALV:n, jos sitä sovelletaan. Peruuta milloin tahansa tililtäsi."));
        });
    }

    private void PlanCard(UIView view, string name, string price, string cadence, bool featured, string[] features, string? offerId)
    {
        view.Card([featured ? Card.Elevated : Card.Default, featured ? "ring-2 ring-brand" : ""], content: c =>
        {
            c.Row(["items-center justify-between"], content: r =>
            {
                r.Text([Text.H4, "font-heading"], text: name);
                if (featured) r.Badge(T("Most popular", "Suosituin"), tone: SemanticTone.Success, size: BadgeSize.Sm);
            });
            c.Text(["font-heading text-4xl"], text: price);
            c.Text([Text.Small, "text-muted-foreground"], text: cadence);
            foreach (var f in features)
            {
                c.Row(["items-start gap-2"], content: r => { r.Icon([Icon.Sm, "text-success-600 mt-0.5 shrink-0"], name: "check"); r.Text([Text.Small], text: f); });
            }

            if (offerId is not null)
            {
                c.Button([featured ? Button.PrimaryMd : Button.OutlineMd, "mt-3"], text: T("Choose", "Valitse"), onClick: async () => await StartCheckoutAsync(offerId));
                if (offerId == "premium-monthly")
                {
                    c.Button([Button.GhostSm], text: T("Or the season pass, €19.99", "Tai kausikortti 19,99 €"), onClick: async () => await StartCheckoutAsync("premium-season"));
                }
            }
            else
            {
                c.Button([Button.OutlineMd, "mt-3"], text: T("Open the map", "Avaa kartta"), onClick: async () => await GoAsync("/map"));
            }
        });
    }

    private void RenderFaqPage(UIView view)
    {
        RenderScrollPage(view, page =>
        {
            Section(page, T("Questions", "Usein kysyttyä"), null, s =>
            {
                var items = new (string QEn, string QFi, string AEn, string AFi)[]
                {
                    ("Are the predictions accurate?", "Ovatko ennusteet tarkkoja?",
                        "We don't know yet, and we say so. Until field reports validate them, results are labelled unvalidated ecological estimates. Once enough accepted reports exist, we test the model on held-out forests and publish how it did.",
                        "Emme vielä tiedä, ja sanomme sen. Ennen kuin maastohavainnot validoivat ne, tulokset merkitään validoimattomiksi ekologisiksi arvioiksi. Kun hyväksyttyjä havaintoja on riittävästi, testaamme mallin pidätetyillä metsillä ja julkaisemme tuloksen."),
                    ("Can I pick anywhere it says high suitability?", "Voinko poimia missä tahansa, missä lukee hyvä soveltuvuus?",
                        "Not automatically. Everyman's rights let you pick wild berries on most land, but not in private yards or fields, and some nature reserves have their own rules. We flag known protected areas, but you must check on site.",
                        "Ei automaattisesti. Jokaisenoikeudet sallivat luonnonmarjojen poimimisen useimmilla mailla, mutta ei pihoilla tai pelloilla, ja joillakin luonnonsuojelualueilla on omat sääntönsä. Merkitsemme tunnetut suojelualueet, mutta tarkista paikan päällä."),
                    ("Why only Oulu?", "Miksi vain Oulu?",
                        "A small pilot lets us validate properly with local pickers before spreading. The data sources cover all of Finland, so expansion is mainly a matter of ingestion and field checks.",
                        "Pieni pilotti mahdollistaa kunnollisen validoinnin paikallisten marjastajien kanssa ennen laajentamista. Tietolähteet kattavat koko Suomen, joten laajentaminen on lähinnä aineistojen haku- ja maastotarkistustyötä."),
                    ("Who can see my reports?", "Kuka näkee havaintoni?",
                        "Only you and the research team see exact spots. You can let reports count in aggregated area summaries (shown only when at least three people contributed) and separately allow model training. Both are off by default and you can delete a report at any time.",
                        "Vain sinä ja tutkimustiimi näette tarkat paikat. Voit sallia havaintojen näkymisen koottuina aluetiivistelminä (vain kun vähintään kolme henkilöä on osallistunut) ja erikseen mallin koulutuksen. Molemmat ovat oletuksena pois päältä ja voit poistaa havainnon milloin tahansa."),
                    ("Why do you use weather from a station kilometres away?", "Miksi käytätte kaukana olevan aseman säätä?",
                        "Weather stations measure only where they stand. We always name the station and distance, and never present it as measured in your forest.",
                        "Sääasemat mittaavat vain omassa paikassaan. Nimeämme aina aseman ja etäisyyden emmekä esitä sitä mitattuna metsässäsi."),
                };

                foreach (var (qEn, qFi, aEn, aFi) in items)
                {
                    s.Card([Card.Default], key: qEn, content: c =>
                    {
                        c.Text([Text.BodyStrong], text: T(qEn, qFi));
                        c.Text([Text.Body, "text-muted-foreground"], text: T(aEn, aFi));
                    });
                }
            });
        });
    }

    private void RenderResponsiblePage(UIView view)
    {
        RenderScrollPage(view, page =>
        {
            Section(page, T("Responsible picking", "Vastuullinen marjastus"), T("Finland's everyman's rights are generous. They come with duties.", "Suomen jokaisenoikeudet ovat anteliaat. Niihin kuuluu myös velvollisuuksia."), s =>
            {
                var topics = new (string Icon, string TEn, string TFi, string BEn, string BFi)[]
                {
                    ("scale", "What you may do", "Mitä saat tehdä", "Pick wild berries and mushrooms on most land without asking, as long as you cause no harm or disturbance. You may not pick in other people's yards, fields or plantations, or where a sign or rule forbids it.",
                        "Saat poimia luonnonmarjoja ja sieniä useimmilla mailla ilman lupaa, kunhan et aiheuta vahinkoa tai häiriötä. Et saa poimia pihoilla, pelloilla tai taimistoissa, etkä siellä, missä kyltti tai sääntö kieltää."),
                    ("shield", "Protected areas", "Suojelualueet", "National parks and many nature reserves allow picking, but strict nature reserves and some protected habitats restrict entry. Check the area's rules. BerryFinder warns about protected areas it knows and hides strict reserves.",
                        "Kansallispuistoissa ja monilla luonnonsuojelualueilla poimiminen on sallittua, mutta luonnonpuistoissa ja joissakin suojelluissa luontotyypeissä liikkumista rajoitetaan. Tarkista alueen säännöt. BerryFinder varoittaa tuntemistaan suojelualueista ja piilottaa luonnonpuistot."),
                    ("leaf", "Leave no trace", "Älä jätä jälkiä", "Take your rubbish with you, don't rake or damage the plants, leave some berries for wildlife, and keep off young tree stands and dripping wet bogs.",
                        "Vie roskasi mukanasi, älä haravoi tai vahingoita kasveja, jätä marjoja eläimille ja vältä nuoria taimikoita ja märkiä soita."),
                    ("footprints", "Stay safe", "Pysy turvassa", "Tell someone where you're going, carry a charged phone and water, wear bright clothing in hunting season, and be aware of ticks, adders and fast weather changes. Mobile coverage in forests can be poor.",
                        "Kerro jollekin minne menet, ota mukaan ladattu puhelin ja vettä, käytä kirkkaita värejä metsästyskaudella ja varo punkkeja, kyitä ja nopeita sään muutoksia. Metsissä kenttä voi olla heikko."),
                    ("car", "Parking and access", "Pysäköinti ja kulku", "Park without blocking forest roads, gates or driveways. Navigation from BerryFinder leads to a road or track, never through private yards. If a way is private or signed, don't use it.",
                        "Pysäköi estämättä metsäteitä, portteja tai pihateitä. BerryFinderin navigointi johtaa tielle tai metsäautotielle, ei koskaan pihojen läpi. Älä käytä yksityistä tai kyltein suljettua tietä."),
                };

                foreach (var (icon, tEn, tFi, bEn, bFi) in topics)
                {
                    s.Card([Card.Default], key: tEn, content: c =>
                    {
                        c.Row(["items-center gap-3"], content: r =>
                        {
                            r.Box(["w-9 h-9 rounded-lg bg-brand-solid/10 flex items-center justify-center"], content: b => b.Icon([Icon.Sm, "text-brand-secondary"], name: icon));
                            r.Text([Text.H4, "font-heading"], text: T(tEn, tFi));
                        });
                        c.Text([Text.Body, "text-muted-foreground"], text: T(bEn, bFi));
                    });
                }

                s.Text([Text.Caption, "text-muted-foreground"], text: T("This is general guidance, not legal advice. Berry predictions are uncertain and cannot guarantee any harvest.", "Tämä on yleisohje, ei oikeudellinen neuvo. Marjaennusteet ovat epävarmoja eivätkä takaa satoa."));
            });
        });
    }

    private void RenderContactPage(UIView view)
    {
        RenderScrollPage(view, page =>
        {
            Section(page, T("Contact", "Yhteystiedot"), T("Questions, data partnerships, pilot interest or a problem to report. We read everything.", "Kysymykset, datayhteistyö, pilotti-kiinnostus tai ilmoitettava ongelma. Luemme kaiken."), s =>
                s.Card([Card.Default, "max-w-xl"], content: c =>
                {
                    c.Column(["gap-3"], content: f =>
                    {
                        f.TextField(label: T("Your email", "Sähköpostisi"), type: "email", bind: _contactEmail);
                        f.Select(value: _contactTopic.Value, ariaLabel: T("Topic", "Aihe"), onValueChange: async v => _contactTopic.Value = v,
                            options: [new("question", T("Question", "Kysymys")), new("pilot", T("Join the pilot", "Osallistu pilottiin")), new("partner", T("Partnership or data", "Yhteistyö tai data")), new("problem", T("Report a problem", "Ilmoita ongelmasta"))]);
                        f.TextArea(label: T("Message", "Viesti"), rows: 5, bind: _contactMessage);
                        if (_contactState.Value is { } state) f.Alert(state, tone: state.StartsWith('✓') ? SemanticTone.Success : SemanticTone.Error);
                        f.Button([Button.PrimaryMd, "self-start"], text: T("Send", "Lähetä"), onClick: async () => await SendContactAsync());
                        f.Text([Text.Caption, "text-muted-foreground"], text: T("We keep your message only to answer it. See Account for data export and deletion.", "Säilytämme viestisi vain vastaamista varten. Tietojen vienti ja poisto löytyvät Tili-sivulta."));
                    });
                }));
        });
    }

    private async Task SendContactAsync()
    {
        var email = _contactEmail.Value.Trim();
        if (!email.Contains('@') || email.Length > 200 || _contactMessage.Value.Trim().Length < 5 || _contactMessage.Value.Length > 4000)
        {
            _contactState.Value = T("Enter a valid email and a message of at least a few words.", "Anna kelvollinen sähköposti ja viesti, jossa on vähintään muutama sana.");
            return;
        }

        await using var db = await OpenDbAsync();
        var recent = await db.ExecuteScalarAsync<int>("SELECT count(*) FROM contact_messages WHERE email = @email AND at > now() - interval '1 hour'", new { email });
        if (recent >= 3)
        {
            _contactState.Value = T("You've sent several messages already. We'll reply soon.", "Olet lähettänyt jo useita viestejä. Vastaamme pian.");
            return;
        }

        await db.ExecuteAsync("INSERT INTO contact_messages (user_id, email, topic, message) VALUES (@userId, @email, @topic, @message)",
            new { userId = CurrentUserId, email, topic = _contactTopic.Value, message = _contactMessage.Value.Trim() });
        _contactMessage.Value = "";
        _contactState.Value = T("✓ Thank you, we've received your message.", "✓ Kiitos, viestisi on vastaanotettu.");
    }
}
